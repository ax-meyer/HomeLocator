using System.Net;
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
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// A source whose parcels name their own addresses (Rheinland-Pfalz's ALKIS vereinfacht), end to
/// end through <see cref="InspirePropertyImporter"/> against a fake parcel WFS: one row per
/// named address, no address service, completeness in parcels.
/// </summary>
public sealed class ParcelLagebezeichnungImportTests
{
    private const string AveNamespace = "http://repository.gdi-de.org/schemas/adv/produkt/alkis-vereinfacht/2.0";

    private static InspireSourceOptions Options(Action<InspireSourceOptions>? tweak = null)
    {
        var options = new InspireSourceOptions
        {
            Source = "test",
            ParcelWfsUrl = FakeWfsServer.ParcelUrl,
            ParcelFeatureType = new ParcelFeatureTypeOptions
            {
                TypeName = "ave:Flurstueck",
                AreaField = "flaeche",
                GemeindeField = "gemeinde",
                LagebezeichnungField = "lagebeztxt",
            },
            AddressSource = new AddressSourceOptions { Type = AddressSourceType.ParcelLagebezeichnung },
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
        FakeWfsServer server, InspireSourceOptions options, IPostcodeAreaProvider? postcodeAreas = null,
        ILogger<InspirePropertyImporter>? logger = null)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(InspirePropertyImporter.HttpClientName)).ReturnsLazily(() => new HttpClient(server));
        return new InspirePropertyImporter(logger ?? NullLogger<InspirePropertyImporter>.Instance, factory, options,
            postcodeAreas: postcodeAreas);
    }

    private static InspireSourceOptions WithBuildings(Action<InspireSourceOptions>? tweak = null) =>
        Options(o =>
        {
            o.BuildingFeatureType = new BuildingFeatureTypeOptions();
            tweak?.Invoke(o);
        });

    private static (float Lat, float Lon) LatLon(double x, double y)
    {
        var (lon, lat) = new UtmToLonLat(25832).Transform(x, y);
        return ((float)lat, (float)lon);
    }

    private static IEnumerable<Uri> BuildingRequests(FakeWfsServer server) =>
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl)
            .Where(u => u.Query.Contains("GebaeudeBauwerk", StringComparison.Ordinal));

    private static FakeWfsServer Server(params FakeParcel[] parcels)
    {
        var server = new FakeWfsServer { AlkisVereinfacht = true };
        server.Parcels.AddRange(parcels);
        return server;
    }

    private static async Task<List<Property>> FetchAllAsync(InspirePropertyImporter importer, ImportRunContext? run = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = await importer.ProbeAsync(ct);
        var rows = new List<Property>();
        await foreach (var row in importer.FetchAsync(probe, run ?? new ImportRunContext(1, "test"), ct))
            rows.Add(row);
        return rows;
    }

    /// <summary>A 10 m parcel at grid cell (i, j).</summary>
    private static FakeParcel Cell(int i, int j, string? lagebezeichnung, double area = 100, string gemeinde = "Testgemeinde") =>
        new($"P{i:D2}_{j:D2}", i * 10, j * 10, i * 10 + 10, j * 10 + 10, area, lagebezeichnung, gemeinde);

    [Fact]
    public async Task FetchAsync_ARowPerNamedAddress_WithItsParcelsArea()
    {
        var server = Server(
            Cell(0, 0, "Löwenhofstraße 5; Vordere Synagogenstraße 2, 2 A", area: 405, gemeinde: "Mainz"),
            Cell(1, 0, "Steingasse", area: 80));

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().BeEquivalentTo(new[]
        {
            new { Str = "Löwenhofstraße", Hnr = "5", HnrZus = (string?)null, Ort = "Mainz", Gemeinde = "Mainz", FlaecheAmtl = 405.0 },
            new { Str = "Vordere Synagogenstraße", Hnr = "2", HnrZus = (string?)null, Ort = "Mainz", Gemeinde = "Mainz", FlaecheAmtl = 405.0 },
            new { Str = "Vordere Synagogenstraße", Hnr = "2", HnrZus = (string?)"A", Ort = "Mainz", Gemeinde = "Mainz", FlaecheAmtl = 405.0 },
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task FetchAsync_ParcelWithOneAddress_PinsItAtTheParcel()
    {
        var server = Server(Cell(1, 0, "Steingasse 3"));

        var rows = await FetchAllAsync(Importer(server, Options()));

        // The 10 m cell over (10,0)–(20,10): its interior point is the centre.
        var (lon, lat) = new UtmToLonLat(25832).Transform(15, 5);
        var row = rows.Should().ContainSingle().Subject;
        row.Lon.Should().Be((float)lon);
        row.Lat.Should().Be((float)lat);
    }

    [Fact]
    public async Task FetchAsync_ParcelWithSeveralAddresses_WithoutBuildings_HasNoPosition()
    {
        var server = Server(Cell(0, 0, "Vordere Synagogenstraße 2, 2 A"));

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Should().HaveCount(2).And.OnlyContain(r => r.Lat == null && r.Lon == null);
    }

    [Fact]
    public async Task FetchAsync_ParcelWithSeveralAddresses_PinsEachAtItsBuilding()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 105, 107"));
        server.Buildings.Add(new FakeBuilding("B1", 1, 1, 4, 4, "Rheinstraße 105"));
        server.Buildings.Add(new FakeBuilding("B2", 6, 6, 9, 9, "Rheinstraße 107"));
        server.Buildings.Add(new FakeBuilding("Garage", 4, 0, 6, 2, null));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        rows.Select(r => (r.Hnr, r.Lat, r.Lon)).Should().Equal(
            ("105", LatLon(2.5, 2.5).Lat, LatLon(2.5, 2.5).Lon),
            ("107", LatLon(7.5, 7.5).Lat, LatLon(7.5, 7.5).Lon));
    }

    [Fact]
    public async Task FetchAsync_BuildingNamingSeveralAddresses_PinsEachOfThem()
    {
        var server = Server(Cell(0, 0, "Zanggasse 3, 5"));
        server.Buildings.Add(new FakeBuilding("B", 2, 2, 8, 8, "Zanggasse 3, 5"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        rows.Should().HaveCount(2).And.OnlyContain(r => r.Lat == LatLon(5, 5).Lat && r.Lon == LatLon(5, 5).Lon);
    }

    [Fact]
    public async Task FetchAsync_BuildingOnTheNeighbouringParcel_IsTheAddressesPosition()
    {
        // A terrace over two parcels: the parcel names the address, the building stands next door.
        var server = Server(Cell(0, 0, "Am Dom 1, 3"));
        server.Buildings.Add(new FakeBuilding("B1", 2, 2, 8, 8, "Am Dom 1"));
        server.Buildings.Add(new FakeBuilding("B3", 12, 2, 18, 8, "Am Dom 3"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        rows.Single(r => r.Hnr == "3").Lon.Should().Be(LatLon(15, 5).Lon);
    }

    [Fact]
    public async Task FetchAsync_SeveralBuildingsNameTheAddress_TheNearestWins()
    {
        var server = Server(Cell(0, 0, "Am Dom 1, 3"));
        server.Buildings.Add(new FakeBuilding("Far", 60, 2, 66, 8, "Am Dom 3"));
        server.Buildings.Add(new FakeBuilding("Near", 12, 2, 18, 8, "Am Dom 3"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        rows.Single(r => r.Hnr == "3").Lon.Should().Be(LatLon(15, 5).Lon);
    }

    [Fact]
    public async Task FetchAsync_BuildingFurtherThanTheLimit_IsNoPosition()
    {
        // Fetched (within 100 m of the parcel's envelope on each axis), but 127 m from its corner.
        var server = Server(Cell(0, 0, "Am Dom 1, 3"));
        server.Buildings.Add(new FakeBuilding("Far", 100, 100, 105, 105, "Am Dom 3"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        BuildingRequests(server).Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Lat == null && r.Lon == null);
    }

    [Fact]
    public async Task FetchAsync_SuffixInAnotherCase_StillMatches()
    {
        var server = Server(Cell(0, 0, "Friedrich-Engels-Straße 34d, 35"));
        server.Buildings.Add(new FakeBuilding("B", 2, 2, 8, 8, "Friedrich-Engels-Straße 34 D"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        var row = rows.Single(r => r.Hnr == "34");
        row.HnrZus.Should().Be("d", "the row keeps the parcel's spelling");
        row.Lon.Should().Be(LatLon(5, 5).Lon);
    }

    [Fact]
    public async Task FetchAsync_OnlyParcelsWithOneAddress_FetchNoBuildings()
    {
        var server = Server(Cell(0, 0, "Steingasse 3"), Cell(3, 0, "Steingasse 5"));
        server.Buildings.Add(new FakeBuilding("B", 1, 1, 4, 4, "Steingasse 3"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings()));

        BuildingRequests(server).Should().BeEmpty();
        rows.Single(r => r.Hnr == "3").Lon.Should().Be(LatLon(5, 5).Lon, "a single address stays at its parcel");
    }

    [Fact]
    public async Task FetchAsync_BuildingsAreFetchedAroundTheParcelsThatNeedThem()
    {
        var server = Server(Cell(2, 3, "Am Dom 1, 3"), Cell(8, 8, "Steingasse 3"));

        await FetchAllAsync(Importer(server, WithBuildings()));

        // Cell (2,3) is (20,30)–(30,40); 100 m around it.
        BuildingRequests(server).Should().ContainSingle()
            .Which.Query.Should().Contain("bbox=-80,-70,130,140,");
    }

    [Fact]
    public async Task FetchAsync_FullBuildingPage_IsSplit()
    {
        var server = Server(Cell(0, 0, "Weg 1, 2, 3, 4, 5, 6, 7, 8"));
        for (var n = 1; n <= 8; n++)
            server.Buildings.Add(new FakeBuilding($"B{n}", n, 1, n + 0.5, 2, $"Weg {n}"));

        var rows = await FetchAllAsync(Importer(server, WithBuildings(o => o.PageSize = 5)));

        rows.Should().HaveCount(8).And.OnlyContain(r => r.Lat != null);
        BuildingRequests(server).Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task FetchAsync_BuildingRequestsKeepFailing_ImportsTheRowsWithoutThosePositions()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 105, 107"), Cell(3, 0, "Kaiserstraße 2"));
        server.Buildings.Add(new FakeBuilding("B1", 1, 1, 4, 4, "Rheinstraße 105"));
        server.Interceptor = (uri, _) => uri.Query.Contains("GebaeudeBauwerk", StringComparison.Ordinal) && uri.Query.Contains("bbox", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;
        var logger = new CapturingLogger<InspirePropertyImporter>();
        var run = new ImportRunContext(1, "test");

        var rows = await FetchAllAsync(Importer(server, WithBuildings(), logger: logger), run);

        rows.Should().HaveCount(3);
        rows.Where(r => r.Str == "Rheinstraße").Should().OnlyContain(r => r.Lat == null);
        rows.Single(r => r.Str == "Kaiserstraße").Lat.Should().NotBeNull();
        run.SkippedParts.Should().Be(0, "every parcel was imported");
        logger.MessagesAt(LogLevel.Warning).Should().ContainMatch("*giving up on ave:GebaeudeBauwerk*");
        logger.MessagesAt(LogLevel.Information).Should()
            .ContainMatch("*positions: 1 at their parcel, 0 at their building, 2 without*1 tiles' buildings couldn't be fetched*");
    }

    [Fact]
    public async Task FetchAsync_LogsWhereThePositionsCameFrom()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 105, 107"), Cell(3, 0, "Kaiserstraße 2"));
        server.Buildings.Add(new FakeBuilding("B1", 1, 1, 4, 4, "Rheinstraße 105"));
        var logger = new CapturingLogger<InspirePropertyImporter>();

        await FetchAllAsync(Importer(server, WithBuildings(), logger: logger));

        logger.MessagesAt(LogLevel.Information).Should()
            .ContainMatch("*positions: 1 at their parcel, 1 at their building, 1 without*0 tiles' buildings couldn't be fetched*");
    }

    [Fact]
    public async Task ProbeAsync_WithBuildings_MarksTheFingerprint()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 1"), Cell(1, 0, null));

        var probe = await Importer(server, WithBuildings()).ProbeAsync(TestContext.Current.CancellationToken);

        probe.Fingerprint.Should().Be($"{InspireProbe.Revision}{InspireProbe.BuildingsMarker}:2");
        BuildingRequests(server).Should().BeEmpty();
    }

    [Fact]
    public async Task Requests_ForBuildings_BindTheirOwnNamespace()
    {
        var server = Server(Cell(0, 0, "Am Dom 1, 3"));

        await FetchAllAsync(Importer(server, WithBuildings(o => o.BuildingFeatureType!.Namespace = AveNamespace)));

        BuildingRequests(server).Should().ContainSingle()
            .Which.Query.Should().Contain($"namespaces=xmlns(ave,{Uri.EscapeDataString(AveNamespace)})");
    }

    [Fact]
    public async Task FetchAsync_AddressTwoParcelsName_YieldsARowForEach()
    {
        // The cadastre assigns the house number to both parcels; each has its own area, and the
        // app shows both rather than guessing which one "the" property is.
        var server = Server(Cell(0, 0, "Am Dom 1", area: 300), Cell(1, 0, "Am Dom 1", area: 120));

        var rows = await FetchAllAsync(Importer(server, Options()));

        rows.Select(r => (r.Str, r.Hnr, r.FlaecheAmtl)).Should().BeEquivalentTo(new[]
        {
            ("Am Dom", "1", (double?)300), ("Am Dom", "1", (double?)120),
        });
    }

    [Fact]
    public async Task FetchAsync_GemeindeNameIsNormalized() =>
        (await FetchAllAsync(Importer(Server(Cell(0, 0, "Mannheimer Straße 1", gemeinde: "Bad Kreuznach, Stadt")), Options())))
            .Single().Gemeinde.Should().Be("Bad Kreuznach");

    [Fact]
    public async Task FetchAsync_ParcelOverATileEdge_IsImportedOnce()
    {
        // The bbox filter returns the parcel for both tiles it touches.
        var server = Server(new FakeParcel("P", 40, 0, 60, 10, 200, "Brückstraße 3"));

        var rows = await FetchAllAsync(Importer(server, Options(o => o.TileSizeMeters = 50)));

        rows.Should().ContainSingle().Which.Str.Should().Be("Brückstraße");
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task FetchAsync_FullTile_IsSplit()
    {
        var cells = Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 4).Select(j => Cell(i * 2, j * 2, $"Straße {i}{j} 1")));
        var server = Server(cells.ToArray());

        var rows = await FetchAllAsync(Importer(server, Options(o => o.PageSize = 5)));

        rows.Should().HaveCount(16);
        rows.Select(r => r.Str).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FetchAsync_FillsThePlzFromThePostcodeAreaAtTheParcel()
    {
        var lookup = A.Fake<IPostcodeLookup>();
        A.CallTo(() => lookup.FindPostcode(A<Point>._)).ReturnsLazily((Point p) => p.X < 50 ? "55116" : "55118");
        var areas = A.Fake<IPostcodeAreaProvider>();
        A.CallTo(() => areas.LoadAsync(25832, A<Envelope>._, A<CancellationToken>._)).Returns(lookup);
        var server = Server(Cell(0, 0, "Rheinstraße 105, 107"), Cell(6, 0, "Kaiserstraße 2"));

        var rows = await FetchAllAsync(Importer(server, Options(o => o.FillMissingPlzFromPostcodeAreas = true), areas));

        rows.Select(r => (r.Hnr, r.Plz)).Should().Equal(("105", "55116"), ("107", "55116"), ("2", "55118"));
    }

    [Fact]
    public async Task FetchAsync_TooFewPostcodeMatches_Fails()
    {
        var lookup = A.Fake<IPostcodeLookup>();
        A.CallTo(() => lookup.FindPostcode(A<Point>._)).Returns(null);
        var areas = A.Fake<IPostcodeAreaProvider>();
        A.CallTo(() => areas.LoadAsync(A<int>._, A<Envelope>._, A<CancellationToken>._)).Returns(lookup);

        var act = () => FetchAllAsync(Importer(Server(Cell(0, 0, "Rheinstraße 1")),
            Options(o => o.FillMissingPlzFromPostcodeAreas = true), areas));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*only 0 of 1 addresses lie in a postcode area*");
    }

    [Fact]
    public async Task FetchAsync_FewerParcelsThanTheServiceCounts_Fails()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 1"), Cell(1, 0, null));
        server.ParcelHitsOverride = "100";

        var act = () => FetchAllAsync(Importer(server, Options()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*fetched only 2 of 100 parcels*");
    }

    [Fact]
    public async Task FetchAsync_ParcelsWithoutAddresses_CountTowardsCompleteness()
    {
        // Most parcels are roads, fields and woods: they name no address but are fetched all the same.
        var server = Server(Cell(0, 0, "Rheinstraße 1"), Cell(1, 0, null), Cell(2, 0, "Wald"), Cell(3, 0, "Kurze 5 Morgen"));
        var logger = new CapturingLogger<InspirePropertyImporter>();

        var rows = await FetchAllAsync(Importer(server, Options(), logger: logger));

        rows.Should().ContainSingle();
        logger.MessagesAt(LogLevel.Information).Should()
            .ContainMatch("*fetched 4 of 4 parcels*1 named 1 addresses, 1 Lagebezeichnung parts couldn't be read*");
    }

    [Fact]
    public async Task FetchAsync_NoParcelNamesAnAddress_Fails()
    {
        var act = () => FetchAllAsync(Importer(Server(Cell(0, 0, null), Cell(1, 0, "Steingasse")), Options()));

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*no parcel named an address*LagebezeichnungField*");
    }

    [Fact]
    public async Task FetchAsync_OneTileKeepsFailing_SkipsItAndReportsItToTheRun()
    {
        var cells = Enumerable.Range(0, 10).SelectMany(i => Enumerable.Range(0, 10).Select(j => Cell(i, j, $"Straße {i}{j} 1")));
        var server = Server(cells.ToArray());
        server.Interceptor = (uri, _) => uri.Query.Contains("bbox=50,50,60,60,", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;
        var run = new ImportRunContext(1, "test");

        var rows = await FetchAllAsync(Importer(server, Options(o => o.TileSizeMeters = 10)), run);

        rows.Should().HaveCount(99).And.NotContain(r => r.Str == "Straße 55");
        run.SkippedParts.Should().Be(1);
    }

    [Fact]
    public async Task ProbeAsync_IsTheParcelCountAlone_AndNoAddressServiceIsAsked()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 1"), Cell(1, 0, null));
        var importer = Importer(server, Options());

        var probe = await importer.ProbeAsync(TestContext.Current.CancellationToken);
        await FetchAllAsync(importer);

        probe.Fingerprint.Should().Be($"{InspireProbe.Revision}:2");
        probe.Kind.Should().Be(FingerprintKind.Approximate);
        probe.Should().BeOfType<InspireProbe>().Which.Addresses.Should().BeNull();
        server.Requests.Should().OnlyContain(u => u.ToString().StartsWith(FakeWfsServer.ParcelUrl, StringComparison.Ordinal));
        server.Requests.Should().OnlyContain(u => u.Query.Contains("typenames=ave%3AFlurstueck", StringComparison.Ordinal)
                                                  || u.Query.Contains("request=GetCapabilities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_BindTheTypesPrefix_WhenANamespaceIsConfigured()
    {
        // Thüringen answers "Unable to determine targeted feature type" without NAMESPACES.
        var server = Server(Cell(0, 0, "Rheinstraße 1"));

        await FetchAllAsync(Importer(server, Options(o => o.ParcelFeatureType.Namespace = AveNamespace)));

        var expected = $"namespaces=xmlns(ave,{Uri.EscapeDataString(AveNamespace)})";
        server.Requests.Where(u => !u.Query.Contains("GetCapabilities", StringComparison.Ordinal))
            .Should().NotBeEmpty().And.OnlyContain(u => u.Query.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_WithoutANamespace_SendNone()
    {
        var server = Server(Cell(0, 0, "Rheinstraße 1"));

        await FetchAllAsync(Importer(server, Options()));

        server.Requests.Should().OnlyContain(u => !u.Query.Contains("namespaces", StringComparison.Ordinal));
    }
}
