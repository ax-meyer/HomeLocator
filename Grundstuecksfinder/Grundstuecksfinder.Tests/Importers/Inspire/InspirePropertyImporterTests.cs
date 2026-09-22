using System.Net;
using System.Xml.Linq;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// The importer's fetch strategy against a fake WFS pair: tile splitting instead of paging,
/// server page caps, border duplicates, retries, and every check that must fail the import
/// rather than let partial or misjoined data through.
/// </summary>
public sealed class InspirePropertyImporterTests
{
    private static readonly ImportCandidate Candidate = new("test-alkis", "statewide", "v");

    private static InspireSourceOptions Options(Action<InspireSourceOptions>? tweak = null)
    {
        var options = new InspireSourceOptions
        {
            Source = "test",
            DatasetName = "test-alkis",
            ParcelWfsUrl = FakeWfsServer.ParcelUrl,
            AddressWfsUrl = FakeWfsServer.AddressUrl,
            Crs = "urn:ogc:def:crs:EPSG::25832",
            BoundingBox = new InspireBoundingBox { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 },
            TileSizeMeters = 100,
            MinTileSizeMeters = 1,
            PageSize = 1000,
            MaxAttempts = 3,
            RetryBaseDelaySeconds = 0,
            MaxRetryDelaySeconds = 0,
        };
        tweak?.Invoke(options);
        return options;
    }

    private static InspirePropertyImporter Importer(
        FakeWfsServer server, InspireSourceOptions options, TimeProvider? time = null, IPostcodeAreaProvider? postcodeAreas = null)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(InspirePropertyImporter.HttpClientName)).ReturnsLazily(() => new HttpClient(server));
        return new InspirePropertyImporter(NullLogger<InspirePropertyImporter>.Instance, factory, options, time, postcodeAreas);
    }

    /// <summary>Postcode areas as vertical strips: "1xxxx" for x &lt; 50, "2xxxx" for 50 ≤ x &lt; 90, none beyond.</summary>
    private static IPostcodeAreaProvider StripPostcodeAreas()
    {
        var lookup = A.Fake<IPostcodeLookup>();
        A.CallTo(() => lookup.FindPostcode(A<Point>._)).ReturnsLazily((Point p) => p.X switch
        {
            < 50 => "10000",
            < 90 => "20000",
            _ => null,
        });
        var provider = A.Fake<IPostcodeAreaProvider>();
        A.CallTo(() => provider.LoadAsync(A<int>._, A<Envelope>._, A<CancellationToken>._)).Returns(lookup);
        return provider;
    }

    private static async Task<List<Property>> FetchAllAsync(InspirePropertyImporter importer)
    {
        var rows = new List<Property>();
        await foreach (var row in importer.FetchAsync(Candidate, TestContext.Current.CancellationToken))
            rows.Add(row);
        return rows;
    }

    /// <summary>An n×n grid of 10 m parcels over (0,0)–(10n,10n), one address in each centre.</summary>
    private static FakeWfsServer GridServer(int n)
    {
        var server = new FakeWfsServer();
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var id = $"{i:D2}_{j:D2}";
                server.Parcels.Add(new FakeParcel($"P{id}", i * 10, j * 10, i * 10 + 10, j * 10 + 10, 100 + i * n + j));
                server.Addresses.Add(new FakeAddress($"A{id}", i * 10 + 5, j * 10 + 5, $"Straße {id}", "1"));
            }
        }
        return server;
    }

    [Fact]
    public async Task FetchAsync_SmallTile_JoinsEveryAddressToItsParcel()
    {
        var server = GridServer(3);

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().HaveCount(9);
        rows.Single(r => r.Str == "Straße 01_02").FlaecheAmtl.Should().Be(100 + 1 * 3 + 2);
        rows.Should().OnlyContain(r => r.Gemeinde == "Testgemeinde");
    }

    [Fact]
    public async Task FetchAsync_TileComesBackFull_IsSplitUntilEverythingFits()
    {
        var server = GridServer(10); // 100 parcels, 100 addresses in the single initial tile

        var rows = await FetchAllAsync(Importer(server, Options(o => o.PageSize = 30)));

        rows.Select(r => r.Str).Should().OnlyHaveUniqueItems().And.HaveCount(100);
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().HaveCountGreaterThan(1, "the full tile must have been split");
        server.Requests.Should().NotContain(u => u.Query.Contains("startIndex"), "paging via startIndex isn't reliable across servers");
    }

    [Fact]
    public async Task FetchAsync_ServerAdvertisesSmallerCountDefault_RequestsAtMostThatMany()
    {
        var server = GridServer(10);
        server.ServerCap = 20;
        server.AdvertisedCountDefault = 20;

        var rows = await FetchAllAsync(Importer(server, Options(o => o.PageSize = 1000)));

        rows.Should().HaveCount(100, "a page capped at CountDefault is recognised as full and its tile split");
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().OnlyContain(u => u.Query.Contains("count=20"));
    }

    [Fact]
    public async Task FetchAsync_ServerCapsSilently_FailsTheCompletenessCheck()
    {
        var server = GridServer(10);
        server.ServerCap = 20; // caps pages without advertising it: tiles look complete but aren't

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*fetched only*");
    }

    [Fact]
    public async Task FetchAsync_AddressExactlyOnTileBorder_IsImportedOnce()
    {
        var server = new FakeWfsServer();
        server.Parcels.Add(new FakeParcel("P1", 0, 0, 20, 10, 200)); // spans both tiles
        server.Addresses.Add(new FakeAddress("A1", 10, 5, "Grenzweg", "1")); // on the x=10 tile edge

        var rows = await FetchAllAsync(Importer(server, Options(o =>
        {
            o.BoundingBox = new InspireBoundingBox { MinX = 0, MinY = 0, MaxX = 20, MaxY = 10 };
            o.TileSizeMeters = 10;
        })));

        rows.Should().ContainSingle().Which.Str.Should().Be("Grenzweg");
    }

    [Fact]
    public async Task FetchAsync_TransientFailures_AreRetried()
    {
        var server = GridServer(2);
        var failures = 0;
        server.Interceptor = (uri, _) =>
        {
            if (!uri.Query.Contains("bbox=") || failures >= 2) return null;
            failures++;
            return failures == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : throw new TaskCanceledException("simulated HttpClient timeout");
        };

        var rows = await FetchAllAsync(Importer(server, Options()));

        failures.Should().Be(2);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task FetchAsync_RequestKeepsFailing_FailsTheImportInsteadOfSkippingTheTile()
    {
        var server = GridServer(2);
        server.Interceptor = (uri, _) => uri.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal)
                                         && uri.Query.Contains("bbox=")
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<HttpRequestException>();
        server.GetFeatureRequests(FakeWfsServer.AddressUrl).Should().HaveCount(3, "MaxAttempts is 3");
    }

    [Fact]
    public async Task FetchAsync_ClientError_IsNotRetried()
    {
        var server = GridServer(2);
        server.Interceptor = (uri, _) => uri.Query.Contains("bbox=") ? new HttpResponseMessage(HttpStatusCode.BadRequest) : null;

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<HttpRequestException>();
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().ContainSingle();
    }

    [Fact]
    public async Task FetchAsync_ResponseInAnotherCrs_FailsTheImport()
    {
        var server = GridServer(2);
        server.ResponseSrsName = "http://www.opengis.net/def/crs/epsg/0/4258";

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*epsg/0/4258 instead of EPSG:25832*");
    }

    [Fact]
    public async Task FetchAsync_OtherSrsNameFormOfTheSameCrs_IsAccepted()
    {
        var server = GridServer(2);
        server.ResponseSrsName = "http://www.opengis.net/def/crs/epsg/0/25832"; // SN/BB answer like this to a urn: request

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task FetchAsync_MostAddressesWithoutParcel_FailsTheImport()
    {
        var server = GridServer(2);
        server.Parcels.RemoveRange(1, 3); // 3 of 4 addresses now fall outside every parcel

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*no containing parcel*");
    }

    [Fact]
    public async Task FetchAsync_TileStillFullAtMinimumSize_FailsTheImport()
    {
        var server = new FakeWfsServer();
        server.Parcels.Add(new FakeParcel("P1", 0, 0, 100, 100, 10_000));
        for (var i = 0; i < 5; i++)
            server.Addresses.Add(new FakeAddress($"A{i}", 50, 50, "Hochhaus", $"{i}")); // same point

        var act = () => FetchAllAsync(Importer(server, Options(o => o.PageSize = 3)));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*minimum tile size*");
    }

    private static bool IsAddressGetFeature(Uri uri) =>
        uri.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal) && uri.Query.Contains("bbox=");

    private static HttpResponseMessage XmlOk(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml"),
    };

    [Fact]
    public async Task FetchAsync_ExceptionReportWithStatus200_IsRetriedNotTakenAsEmptyTile()
    {
        var server = GridServer(2);
        var served = 0;
        server.Interceptor = (uri, _) => IsAddressGetFeature(uri) && served++ == 0
            ? XmlOk("""<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1"><ows:Exception><ows:ExceptionText>Server overloaded</ows:ExceptionText></ows:Exception></ows:ExceptionReport>""")
            : null;

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().HaveCount(4);
        served.Should().Be(2, "the error document must have been retried");
    }

    [Fact]
    public async Task FetchAsync_ErrorDocumentEveryTime_FailsTheImport()
    {
        var server = GridServer(2);
        server.Interceptor = (uri, _) => IsAddressGetFeature(uri)
            ? XmlOk("""<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"><wfs:truncatedResponse/></wfs:FeatureCollection>""")
            : null;

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<WfsResponseException>().WithMessage("*truncated*");
    }

    [Fact]
    public async Task FetchAsync_ServerStallsMidBody_TimesOutAndRetries()
    {
        var server = GridServer(2);
        var stalled = false;
        server.Interceptor = (uri, _) =>
        {
            if (!IsAddressGetFeature(uri) || stalled) return null;
            stalled = true;
            // Headers arrive, then the body never does: HttpClient.Timeout wouldn't catch this.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        };

        var rows = await FetchAllAsync(Importer(server, Options(o => o.RequestTimeoutSeconds = 0.5)));

        stalled.Should().BeTrue();
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task FetchAsync_TooManyRequestsWithRetryAfter_IsRetried()
    {
        var server = GridServer(2);
        var throttled = false;
        server.Interceptor = (uri, _) =>
        {
            if (!IsAddressGetFeature(uri) || throttled) return null;
            throttled = true;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response; // MaxRetryDelaySeconds = 0 in these tests caps the wait
        };

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task FetchAsync_UnrecognisedSrsName_FailsTheImport()
    {
        var server = GridServer(2);
        server.ResponseSrsName = "urn:x-custom:crs:local";

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*urn:x-custom:crs:local*");
    }

    [Fact]
    public async Task FetchAsync_AdvSrsNameOfTheSameCrs_IsAccepted()
    {
        var server = GridServer(2);
        server.ResponseSrsName = "urn:adv:crs:ETRS89_UTM32";

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task FetchAsync_OnlyAddressPageFull_ReusesTheParcelsInsteadOfRefetching()
    {
        var server = new FakeWfsServer();
        server.Parcels.Add(new FakeParcel("P1", 0, 0, 100, 100, 10_000)); // one big parcel
        for (var i = 0; i < 40; i++)
            server.Addresses.Add(new FakeAddress($"A{i:D2}", 1 + i * 2.4, 1 + i * 2.4, "Hofweg", $"{i}"));

        var rows = await FetchAllAsync(Importer(server, Options(o => o.PageSize = 10)));

        rows.Should().HaveCount(40);
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().ContainSingle(
            "the parcel page wasn't full, so split tiles reuse it");
    }

    [Fact]
    public async Task DiscoverAsync_VersionCombinesCountsAndMonth()
    {
        var server = GridServer(2);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

        var candidates = await Importer(server, Options(), time).DiscoverAsync(TestContext.Current.CancellationToken);

        candidates.Should().ContainSingle().Which.VersionTimestamp.Should().Be("4:4:2026-09");
    }

    [Fact]
    public async Task DiscoverAsync_UnknownFeatureCount_ReturnsNoCandidate()
    {
        var server = GridServer(2);
        server.AddressHitsOverride = "unknown";

        var candidates = await Importer(server, Options()).DiscoverAsync(TestContext.Current.CancellationToken);

        candidates.Should().BeEmpty("without a total, the import's completeness can't be checked");
    }

    [Fact]
    public void ParseCountDefault_ReadsTheCapabilitiesConstraint()
    {
        var capabilities = XDocument.Parse("""
            <wfs:WFS_Capabilities xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:ows="http://www.opengis.net/ows/1.1">
              <ows:OperationsMetadata>
                <ows:Constraint name="ImplementsResultPaging"><ows:NoValues/><ows:DefaultValue>TRUE</ows:DefaultValue></ows:Constraint>
                <ows:Constraint name="CountDefault"><ows:NoValues/><ows:DefaultValue>10000</ows:DefaultValue></ows:Constraint>
              </ows:OperationsMetadata>
            </wfs:WFS_Capabilities>
            """);

        InspirePropertyImporter.ParseCountDefault(capabilities).Should().Be(10000);
    }

    [Fact]
    public async Task FetchAsync_FillMissingPlz_TakesThePlzOfTheContainingArea()
    {
        var server = GridServer(3); // addresses at x = 5, 15, 25 — all in the "10000" strip

        var rows = await FetchAllAsync(Importer(server, Options(o => o.FillMissingPlzFromPostcodeAreas = true), postcodeAreas: StripPostcodeAreas()));

        rows.Should().HaveCount(9).And.OnlyContain(r => r.Plz == "10000");
    }

    [Fact]
    public async Task FetchAsync_FillMissingPlz_KeepsThePlzTheSourcePublishes()
    {
        var server = new FakeWfsServer();
        server.Parcels.Add(new FakeParcel("P1", 0, 0, 10, 10, 500));
        server.Parcels.Add(new FakeParcel("P2", 60, 0, 70, 10, 600));
        server.Addresses.Add(new FakeAddress("A1", 5, 5, "Amtliche Straße", "1", Plz: "12345"));
        server.Addresses.Add(new FakeAddress("A2", 65, 5, "Leere Straße", "2"));

        var rows = await FetchAllAsync(Importer(server, Options(o => o.FillMissingPlzFromPostcodeAreas = true), postcodeAreas: StripPostcodeAreas()));

        rows.Single(r => r.Str == "Amtliche Straße").Plz.Should().Be("12345", "the source's own PLZ wins over the area's 10000");
        rows.Single(r => r.Str == "Leere Straße").Plz.Should().Be("20000");
    }

    [Fact]
    public async Task FetchAsync_FillMissingPlz_ReplacesAPublishedPlzThatIsNoPlz()
    {
        var server = new FakeWfsServer();
        server.Parcels.Add(new FakeParcel("P1", 0, 0, 10, 10, 500));
        server.Addresses.Add(new FakeAddress("A1", 5, 5, "Prenzlauer Chaussee", "1", Plz: "Wandlitz"));

        var rows = await FetchAllAsync(Importer(server, Options(o => o.FillMissingPlzFromPostcodeAreas = true), postcodeAreas: StripPostcodeAreas()));

        rows.Should().ContainSingle().Which.Plz.Should().Be("10000");
    }

    [Fact]
    public async Task FetchAsync_TooFewAddressesInAnyArea_Fails()
    {
        var server = GridServer(10); // x = 5 … 95: the column at 95 lies outside every area (10 %)

        var act = () => FetchAllAsync(Importer(server,
            Options(o => { o.FillMissingPlzFromPostcodeAreas = true; o.MinPostcodeFillRatio = 0.95; }),
            postcodeAreas: StripPostcodeAreas()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*90 of 100 addresses without a PLZ*");
    }

    [Fact]
    public async Task FetchAsync_SomeAddressesInNoArea_KeepsThemWithoutPlz()
    {
        var server = GridServer(10);

        var rows = await FetchAllAsync(Importer(server,
            Options(o => { o.FillMissingPlzFromPostcodeAreas = true; o.MinPostcodeFillRatio = 0.9; }),
            postcodeAreas: StripPostcodeAreas()));

        rows.Should().HaveCount(100);
        rows.Count(r => r.Plz is null).Should().Be(10);
    }

    [Fact]
    public async Task FetchAsync_FillMissingPlzWithoutProvider_FailsBeforeFetchingTiles()
    {
        var server = GridServer(1);

        var act = () => FetchAllAsync(Importer(server, Options(o => o.FillMissingPlzFromPostcodeAreas = true)));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no postcode area provider*");
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_FillMissingPlzOff_DoesNotLoadAreas()
    {
        var provider = StripPostcodeAreas();

        var rows = await FetchAllAsync(Importer(GridServer(1), Options(), postcodeAreas: provider));

        rows.Should().ContainSingle().Which.Plz.Should().BeNull();
        A.CallTo(provider).MustNotHaveHappened();
    }

    [Fact]
    public async Task FetchAsync_FillMissingPlz_PassesTheSourceCrsAndBoundingBox()
    {
        var provider = StripPostcodeAreas();

        await FetchAllAsync(Importer(GridServer(1), Options(o => o.FillMissingPlzFromPostcodeAreas = true), postcodeAreas: provider));

        A.CallTo(() => provider.LoadAsync(25832, new Envelope(0, 100, 0, 100), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    /// <summary>A response body that never delivers a byte until the read is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
