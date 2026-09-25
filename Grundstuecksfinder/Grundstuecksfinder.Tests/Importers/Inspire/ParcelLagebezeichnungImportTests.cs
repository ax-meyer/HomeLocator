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

        probe.Fingerprint.Should().Be("2");
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
