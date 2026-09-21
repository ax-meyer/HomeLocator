using System.Net;
using System.Xml.Linq;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
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
        };
        tweak?.Invoke(options);
        return options;
    }

    private static InspirePropertyImporter Importer(FakeWfsServer server, InspireSourceOptions options, TimeProvider? time = null)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(InspirePropertyImporter.HttpClientName)).ReturnsLazily(() => new HttpClient(server));
        return new InspirePropertyImporter(NullLogger<InspirePropertyImporter>.Instance, factory, options, time);
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

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*EPSG:4258*");
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
