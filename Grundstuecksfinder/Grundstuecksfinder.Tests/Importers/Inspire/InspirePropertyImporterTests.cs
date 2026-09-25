using System.Diagnostics.Metrics;
using System.Net;
using System.Xml.Linq;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NetTopologySuite.Geometries;
using Polly.CircuitBreaker;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// The importer's fetch strategy against a fake WFS pair: tile splitting instead of paging,
/// server page caps, border duplicates, retries, and every check that must fail the import
/// rather than let partial or misjoined data through.
/// </summary>
public sealed class InspirePropertyImporterTests
{
    /// <summary>The WFS sides re-read their counts themselves, so any probe of this source's kind will do.</summary>
    private static readonly InspireProbe Probe = InspireProbe.Combine(new FingerprintPart("4", false), new FingerprintPart("4", false));

    private static InspireSourceOptions Options(Action<InspireSourceOptions>? tweak = null)
    {
        var options = new InspireSourceOptions
        {
            Source = "test",
            ParcelWfsUrl = FakeWfsServer.ParcelUrl,
            AddressSource = new AddressSourceOptions { Url = FakeWfsServer.AddressUrl },
            Crs = "urn:ogc:def:crs:EPSG::25832",
            BoundingBox = new InspireBoundingBox { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 },
            TileSizeMeters = 100,
            MinTileSizeMeters = 1,
            PageSize = 1000,
            MaxAttempts = 3,
            RetryBaseDelaySeconds = 0,
            MaxRetryDelaySeconds = 0,
            MinRequestIntervalSeconds = 0,
        };
        tweak?.Invoke(options);
        return options;
    }

    private static InspirePropertyImporter Importer(
        FakeWfsServer server, InspireSourceOptions options, TimeProvider? time = null,
        IPostcodeAreaProvider? postcodeAreas = null, ILoggerFactory? loggerFactory = null,
        ILogger<InspirePropertyImporter>? logger = null, string? workDirectory = null)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(InspirePropertyImporter.HttpClientName)).ReturnsLazily(() => new HttpClient(server));
        return new InspirePropertyImporter(logger ?? NullLogger<InspirePropertyImporter>.Instance, factory, options, time, postcodeAreas,
            loggerFactory: loggerFactory, workDirectory: workDirectory);
    }

    /// <summary>
    /// Runs an operation that waits on the pipeline's clock and advances that clock until it
    /// finishes, so retry and timeout waits cost no real time. Returns how far the clock moved.
    /// </summary>
    private static async Task<(T Result, TimeSpan Advanced)> WithVirtualTimeAsync<T>(
        FakeTimeProvider time, Task<T> task, TimeSpan step, TimeSpan limit)
    {
        var advanced = TimeSpan.Zero;
        while (!task.IsCompleted && advanced < limit)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
            time.Advance(step);
            advanced += step;
        }
        if (!task.IsCompleted)
            throw new TimeoutException($"still waiting after advancing the clock by {advanced}");
        return (await task, advanced);
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

    private static async Task<List<Property>> FetchAllAsync(
        InspirePropertyImporter importer, ImportRunContext? run = null, SourceProbe? probe = null)
    {
        var rows = new List<Property>();
        await foreach (var row in importer.FetchAsync(probe ?? Probe, run ?? new ImportRunContext(1, "test"), TestContext.Current.CancellationToken))
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

    /// <summary>One 10 m tile of the 10x10 grid, as the WFS bbox filter spells it.</summary>
    private static bool IsTileOf(Uri uri, string baseUrl, int x, int y) =>
        uri.ToString().StartsWith(baseUrl, StringComparison.Ordinal)
        && uri.Query.Contains(FormattableString.Invariant($"bbox={x},{y},{x + 10},{y + 10},"), StringComparison.Ordinal);

    [Fact]
    public async Task FetchAsync_MinRequestInterval_PacesEveryRequest()
    {
        // A whole state is tens of thousands of requests against one public download service;
        // nothing else in the fetch paces them.
        var server = GridServer(2);
        var time = new FakeTimeProvider();

        var (rows, advanced) = await WithVirtualTimeAsync(
            time, FetchAllAsync(Importer(server, Options(o => o.MinRequestIntervalSeconds = 0.5), time)),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(5));

        rows.Should().HaveCount(4);
        server.Requests.Should().HaveCountGreaterThan(3);
        advanced.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.5 * (server.Requests.Count - 1)),
            "every request after the first waits out the interval");
    }

    [Fact]
    public async Task FetchAsync_NoMinRequestInterval_DoesNotWait()
    {
        var server = GridServer(2);
        var time = new FakeTimeProvider();

        var rows = await FetchAllAsync(Importer(server, Options(o => o.MinRequestIntervalSeconds = 0), time));

        rows.Should().HaveCount(4, "nothing waits on a clock that is never advanced");
    }

    [Fact]
    public async Task FetchAsync_OneTileKeepsFailing_SkipsItAndImportsTheRest()
    {
        // The real case this exists for: one BW parcel tile answered 500 for hours while the rest
        // of the state came back fine. Losing 1 of 100 addresses is far inside MinCompleteness,
        // and must not discard the hours already fetched.
        var server = GridServer(10);
        server.Interceptor = (uri, _) => IsTileOf(uri, FakeWfsServer.AddressUrl, 50, 50)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;
        var logger = new CapturingLogger<InspirePropertyImporter>();

        var rows = await FetchAllAsync(Importer(server, Options(o => o.TileSizeMeters = 10), logger: logger));

        rows.Should().HaveCount(99).And.NotContain(p => p.Str == "Straße 05_05");
        server.GetFeatureRequests(FakeWfsServer.AddressUrl).Where(u => IsTileOf(u, FakeWfsServer.AddressUrl, 50, 50))
            .Should().HaveCount(3, "the tile is only skipped once MaxAttempts are spent");
        logger.MessagesAt(LogLevel.Warning).Should()
            .ContainSingle(m => m.Contains("skipping it", StringComparison.Ordinal))
            .Which.Should().Contain("tile 50,50,60,60").And.Contain("1 of at most 10");
        logger.MessagesAt(LogLevel.Information).Should().ContainMatch("*fetched 99 of 100*1 tiles were skipped*");
    }

    [Fact]
    public async Task FetchAsync_TileSkipped_ReportsItToTheRun()
    {
        var server = GridServer(10);
        server.Interceptor = (uri, _) => IsTileOf(uri, FakeWfsServer.AddressUrl, 50, 50)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;
        var importer = Importer(server, Options(o => o.TileSizeMeters = 10));
        var run = new ImportRunContext(1, "test");

        await FetchAllAsync(importer, run);

        run.SkippedParts.Should().Be(1, "the writer records this on the ImportRun");
    }

    [Fact]
    public async Task FetchAsync_NothingSkipped_ReportsZero()
    {
        var run = new ImportRunContext(1, "test");

        await FetchAllAsync(Importer(GridServer(2), Options()), run);

        run.SkippedParts.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_MoreFailingTilesThanAllowed_FailsTheImport()
    {
        // A server failing all over must not quietly import half a state.
        var server = GridServer(10);
        server.Interceptor = (uri, _) => uri.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal)
                                         && uri.Query.Contains("bbox=", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;

        var act = () => FetchAllAsync(Importer(server, Options(o =>
        {
            o.TileSizeMeters = 10;
            o.MaxFailedTiles = 2;
        })));

        var thrown = (await act.Should().ThrowAsync<InspireImportException>()
            .WithMessage("*gave up on 3 tiles (maximum 2)*")).Which;
        thrown.InnerException.Should().BeOfType<SourceHttpException>("the failure that broke the budget is kept");
        server.GetFeatureRequests(FakeWfsServer.AddressUrl).Should().HaveCount(9,
            "it stops at the fourth failing tile, each tried MaxAttempts times");
    }

    [Fact]
    public async Task FetchAsync_SkippedTilesLeaveTooMuchMissing_FailsTheCompletenessCheck()
    {
        // Under MaxFailedTiles, but the completeness check is the real safety net behind it.
        var server = GridServer(10);
        server.Interceptor = (uri, _) => uri.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal)
                                         && uri.Query.Contains("bbox=0,", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;

        var act = () => FetchAllAsync(Importer(server, Options(o =>
        {
            o.TileSizeMeters = 10;       // the x=0 column is 10 of the 100 tiles
            o.MaxFailedTiles = 20;
        })));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*fetched only 90 of 100 addresses*");
    }

    [Fact]
    public async Task FetchAsync_ServerFailsHalfTheRequests_OpensTheCircuitInsteadOfGrindingOn()
    {
        // A degraded server answers often enough that every tile eventually succeeds on retry, so
        // without a breaker the import would crawl through all 100 tiles at MaxAttempts each.
        var server = GridServer(2);
        var requests = 0;
        server.Interceptor = (uri, _) =>
        {
            if (!uri.Query.Contains("bbox=")) return null;
            requests++;
            return requests % 2 == 0 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        };

        var options = Options(o =>
        {
            o.TileSizeMeters = 10;          // 100 tiles
            o.CircuitMinimumThroughput = 4;
            o.CircuitFailureRatio = 0.4;
        });

        var act = () => FetchAllAsync(Importer(server, options));

        await act.Should().ThrowAsync<BrokenCircuitException>();
        requests.Should().BeLessThan(40, "the circuit opens long before all 100 tiles have been tried");
    }

    [Fact]
    public async Task FetchAsync_WithLoggerFactory_ReportsRetriesAsPollyTelemetry()
    {
        var server = GridServer(2);
        var failed = false;
        server.Interceptor = (uri, _) =>
        {
            if (!IsAddressGetFeature(uri) || failed) return null;
            failed = true;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        using var meterReader = new MeterReader("Polly");

        var rows = await FetchAllAsync(Importer(server, Options(), loggerFactory: NullLoggerFactory.Instance));

        rows.Should().HaveCount(4);
        meterReader.Instruments.Should().Contain("resilience.polly.strategy.attempt.duration",
            "the pipeline is wired to Polly's meter, so retries show up next to the app's own metrics");
        meterReader.Tags.Should().Contain(t => t.Key == "pipeline.name" && (string?)t.Value == "inspire:test",
            "the measurements name the source whose server failed");
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

        // MaxFailedTiles = 0 so the tile's own failure surfaces: a truncated response must never
        // be read as an empty tile, and skipping the tile must keep what went wrong with it.
        var act = () => FetchAllAsync(Importer(server, Options(o => o.MaxFailedTiles = 0)));

        var thrown = (await act.Should().ThrowAsync<InspireImportException>().WithMessage("*gave up on 1 tiles*")).Which;
        thrown.InnerException.Should().BeOfType<WfsResponseException>().Which.Message.Should().Contain("truncated");
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

        var time = new FakeTimeProvider();
        var options = Options(o => o.RequestTimeoutSeconds = 60);

        var (rows, advanced) = await WithVirtualTimeAsync(
            time, FetchAllAsync(Importer(server, options, time)),
            step: TimeSpan.FromSeconds(5), limit: TimeSpan.FromSeconds(300));

        stalled.Should().BeTrue();
        rows.Should().HaveCount(4);
        advanced.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60),
            "the stalled attempt runs until RequestTimeoutSeconds is up");
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
            return response;
        };

        var time = new FakeTimeProvider();
        var options = Options(o => { o.RetryBaseDelaySeconds = 1; o.MaxRetryDelaySeconds = 5; });

        var (rows, advanced) = await WithVirtualTimeAsync(
            time, FetchAllAsync(Importer(server, options, time)),
            step: TimeSpan.FromSeconds(1), limit: TimeSpan.FromSeconds(29));

        rows.Should().HaveCount(4);
        advanced.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "MaxRetryDelaySeconds caps the wait at 5 s, so the server's Retry-After of 30 s is not obeyed literally");
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
    public async Task ProbeAsync_FingerprintIsParcelAndAddressCountsAndApproximate()
    {
        // Counts drift daily in active states; no time component — how often to re-import is
        // the planner's decision.
        var probe = await Importer(GridServer(2), Options()).ProbeAsync(TestContext.Current.CancellationToken);

        probe.Fingerprint.Should().Be("4:4");
        probe.Kind.Should().Be(FingerprintKind.Approximate);
    }

    [Fact]
    public async Task ProbeAsync_CarriesEachSidesPartForTheFetch_ParcelsFirst()
    {
        var server = GridServer(2);
        server.Parcels.RemoveAt(0);

        var probe = await Importer(server, Options()).ProbeAsync(TestContext.Current.CancellationToken);

        probe.Should().BeOfType<InspireProbe>().Which.Should().BeEquivalentTo(new
        {
            Fingerprint = "3:4",
            Parcels = new FingerprintPart("3", IsExact: false),
            Addresses = new FingerprintPart("4", IsExact: false),
        });
    }

    [Fact]
    public async Task FetchAsync_SomeoneElsesProbe_IsRejected()
    {
        var act = () => FetchAllAsync(Importer(GridServer(1), Options()), probe: new SourceProbe("1:1", FingerprintKind.Approximate));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Expected the probe of InspirePropertyImporter*");
    }

    [Fact]
    public async Task ProbeAsync_UnknownFeatureCount_Fails()
    {
        var server = GridServer(2);
        server.AddressHitsOverride = "unknown";

        var act = () => Importer(server, Options()).ProbeAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InspireImportException>("without a total, the import's completeness can't be checked")
            .WithMessage("the address service doesn't report a feature count*");
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

        WfsFeatureType.ParseCountDefault(capabilities).Should().Be(10000);
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

    /// <summary>Records which instruments of a meter were written to, and with what tags.</summary>
    private sealed class MeterReader : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<string> Instruments { get; } = [];
        public List<KeyValuePair<string, object?>> Tags { get; } = [];

        public MeterReader(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            {
                lock (Instruments)
                {
                    Instruments.Add(instrument.Name);
                    Tags.AddRange(tags.ToArray());
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    // ── Addresses paged with startIndex (Hamburg) ────────────────────────────

    [Fact]
    public async Task FetchAsync_AddressBboxFails_AndPagingIsOff_FailsTheImport()
    {
        var server = GridServer(10);
        server.FailAddressBbox = true;

        var fetch = async () => await FetchAllAsync(Importer(server, Options()));

        await fetch.Should().ThrowAsync<Exception>("without paging there is no way around a broken bbox filter");
    }

    [Fact]
    public async Task FetchAsync_PagingConfigured_FetchesEveryAddressWithStartIndexAndNoBbox()
    {
        var server = GridServer(10); // 100 parcels, 100 addresses
        server.FailAddressBbox = true;

        var rows = await FetchAllAsync(Importer(server, Options(o =>
        {
            o.PageSize = 30;
            o.AddressSource.Type = AddressSourceType.InspireWfsStartIndex;
        })));

        rows.Select(r => r.Str).Should().OnlyHaveUniqueItems().And.HaveCount(100);
        rows.Single(r => r.Str == "Straße 01_02").FlaecheAmtl.Should().Be(100 + 1 * 10 + 2);

        var addressRequests = server.GetFeatureRequests(FakeWfsServer.AddressUrl).ToList();
        addressRequests.Should().OnlyContain(u => !u.Query.Contains("bbox"), "the bbox filter is what paging avoids");
        addressRequests.Should().OnlyContain(u => u.Query.Contains("startIndex"));
        addressRequests.Select(u => u.Query).Should().HaveCount(4, "30 + 30 + 30 + 10 addresses ends the paging");
    }

    [Fact]
    public async Task FetchAsync_PagingConfigured_ServerIgnoresStartIndex_FailsTheImport()
    {
        var server = GridServer(10);
        server.FailAddressBbox = true;
        server.IgnoreStartIndex = true; // hands out its first page forever

        var fetch = async () => await FetchAllAsync(Importer(server, Options(o =>
        {
            o.PageSize = 30;
            o.AddressSource.Type = AddressSourceType.InspireWfsStartIndex;
        })));

        (await fetch.Should().ThrowAsync<InspireImportException>())
            .WithMessage("*is startIndex ignored?*");
    }

    // ── Addresses paged via OGC API Features (Saarland) ──────────────────────

    [Fact]
    public async Task FetchAsync_OgcApiAddressesConfigured_FetchesEveryAddressByOffset()
    {
        var server = GridServer(10); // 100 parcels, 100 addresses

        var rows = await FetchAllAsync(Importer(server, Options(o =>
        {
            o.AddressSource.Type = AddressSourceType.OgcApiFeatures;
            o.AddressSource.OgcApiPageSize = 30;
        })));

        rows.Select(r => r.Str).Should().OnlyHaveUniqueItems().And.HaveCount(100);
        rows.Single(r => r.Str == "Straße 01_02").FlaecheAmtl.Should().Be(100 + 1 * 10 + 2);

        // Excludes the initial "limit=1" hits check that establishes the expected total.
        var pageRequests = server.OgcApiRequests(FakeWfsServer.AddressUrl).Where(u => u.Query.Contains("offset")).ToList();
        pageRequests.Should().OnlyContain(u => !u.Query.Contains("bbox"), "the whole address set is fetched in one pass");
        pageRequests.Should().HaveCount(4, "30 + 30 + 30 + 10 addresses ends the paging");
    }

    [Fact]
    public async Task FetchAsync_OgcApiAddresses_ServerIgnoresOffset_FailsTheImport()
    {
        var server = GridServer(10);
        server.IgnoreOffset = true; // hands out its first page forever

        var fetch = async () => await FetchAllAsync(Importer(server, Options(o =>
        {
            o.AddressSource.Type = AddressSourceType.OgcApiFeatures;
            o.AddressSource.OgcApiPageSize = 30;
        })));

        (await fetch.Should().ThrowAsync<InspireImportException>())
            .WithMessage("*is offset ignored?*");
    }

    /// <summary>
    /// Regression test for a bug the live tests caught: Saarland's real OGC API server answers
    /// its HTML viewer instead of GeoJSON unless Accept explicitly asks for it. The fake mimics
    /// that quirk, so this fails (a JsonException after exhausted retries) if the importer ever
    /// stops sending the header.
    /// </summary>
    [Fact]
    public async Task FetchAsync_OgcApiAddresses_SendsAcceptHeaderForGeoJson()
    {
        var server = GridServer(3); // 9 parcels, 9 addresses

        var rows = await FetchAllAsync(Importer(server, Options(o => o.AddressSource.Type = AddressSourceType.OgcApiFeatures)));

        rows.Should().HaveCount(9);
    }

    [Fact]
    public async Task ProbeAsync_OgcApiAddressesConfigured_ReadsHitsFromTheOgcApiEndpoint()
    {
        var server = GridServer(2); // 4 parcels, 4 addresses

        var probe = await Importer(server, Options(o => o.AddressSource.Type = AddressSourceType.OgcApiFeatures))
            .ProbeAsync(TestContext.Current.CancellationToken);

        probe.Fingerprint.Should().Be("4:4");
        server.OgcApiRequests(FakeWfsServer.AddressUrl).Should().ContainSingle(u => u.Query.Contains("limit=1"));
    }

    // ── Addresses from a Hauskoordinaten file (Baden-Württemberg, Hessen) ────

    private const string HkUrl = "http://fake/hk/hk_bw.zip";

    /// <summary>
    /// The grid server's parcels, with its addresses served as a Hauskoordinaten file instead
    /// of from the address WFS — plus the given extra rows.
    /// </summary>
    private static (FakeWfsServer Server, InspireSourceOptions Options) HkSource(int n, params string[] extraRows)
    {
        var server = GridServer(n);
        var zip = HkFiles.Zip((HkFiles.Member, HkFiles.Text(server.Addresses.Select(a => HkFiles.Row(a.X, a.Y, a.Street, a.Hnr)).Concat(extraRows))));
        server.Interceptor = (uri, _) => uri.ToString() == HkUrl ? HkFiles.Response(zip) : null;
        var options = Options(o => o.AddressSource = new AddressSourceOptions
        {
            Type = AddressSourceType.HkFile,
            Url = HkUrl,
            Member = HkFiles.Member,
        });
        return (server, options);
    }

    [Fact]
    public async Task FetchAsync_HkFile_JoinsTheFilesAddressesToTheWfsParcels()
    {
        var (server, options) = HkSource(3, HkFiles.Row(25, 25, "Distr. Am Ottersberg", "86188851", qua: "C"));
        var workDirectory = Path.Combine(Path.GetTempPath(), $"hk-importer-tests-{Guid.NewGuid():N}");
        var importer = Importer(server, options, workDirectory: workDirectory);

        var rows = await FetchAllAsync(importer, probe: await importer.ProbeAsync(TestContext.Current.CancellationToken));

        rows.Should().HaveCount(9, "the quality C row is dropped");
        rows.Single(r => r.Str == "Straße 01_02").FlaecheAmtl.Should().Be(100 + 1 * 3 + 2);
        rows.Should().OnlyContain(r => r.Gemeinde == "Testgemeinde" && r.Ort == "Testgemeinde");
        server.GetFeatureRequests(FakeWfsServer.AddressUrl).Should().BeEmpty("the address WFS isn't asked at all");
        Directory.GetFiles(workDirectory).Should().BeEmpty("the download is deleted once read");
        Directory.Delete(workDirectory);
    }

    [Fact]
    public async Task FetchAsync_HkFileAddressesOutsideTheBoundingBox_FailTheCompletenessCheck()
    {
        // Every kept row counts as expected, so a bounding box too small for the file's data
        // fails the import instead of silently dropping what lies outside it.
        var (server, options) = HkSource(2, HkFiles.Row(500, 500, "Außerhalb", "1"), HkFiles.Row(600, 600, "Außerhalb", "2"));

        var importer = Importer(server, options);
        var probe = await importer.ProbeAsync(TestContext.Current.CancellationToken);

        var act = () => FetchAllAsync(importer, probe: probe);

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*fetched only 4 of 6 addresses*");
    }

    [Fact]
    public async Task FetchAsync_HkFileAsProbed_KeepsTheProbesFingerprint()
    {
        var (server, options) = HkSource(2);
        var importer = Importer(server, options);
        var run = new ImportRunContext(1, "test");

        await FetchAllAsync(importer, run, await importer.ProbeAsync(TestContext.Current.CancellationToken));

        run.ImportedFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_HkFileChangedSinceTheProbe_ReportsWhatWasReallyImported()
    {
        // The newer edition is imported; the run must record it, or the next run would compare
        // against the older one and download this edition again.
        var (server, options) = HkSource(2);
        var importer = Importer(server, options);
        var probe = (InspireProbe)await importer.ProbeAsync(TestContext.Current.CancellationToken);
        var olderProbe = probe with { Addresses = new FingerprintPart("\"older\"|2026-01-15T07:27:05Z", true) };
        var run = new ImportRunContext(1, "test");

        await FetchAllAsync(importer, run, olderProbe);

        run.ImportedFingerprint.Should().Be(probe.Fingerprint);
    }

    [Fact]
    public async Task ProbeAsync_HkFile_CombinesTheParcelCountWithTheFilesVersion()
    {
        var (server, options) = HkSource(2);

        var probe = await Importer(server, options).ProbeAsync(TestContext.Current.CancellationToken);

        probe.Fingerprint.Should().Be("4:\"4607dc5-656a14021d8ec\"|2026-07-15T07:27:05Z");
        probe.Kind.Should().Be(FingerprintKind.Approximate, "the parcel count from the WFS is only approximate");
        probe.Should().BeOfType<InspireProbe>().Which.Addresses.IsExact.Should().BeTrue("the file's own version is exact");
    }
}
