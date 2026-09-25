using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire.Addresses;

/// <summary>
/// Flat address features (Bremen's native ALKIS Gebäudeadressen) joined to the parcels end to
/// end through <see cref="InspirePropertyImporter"/>, against a fake WFS pair.
/// </summary>
public sealed class FlatWfsAddressProviderTests
{
    private const string AppNamespace = "http://www.deegree.org/app";

    private static InspireSourceOptions Options(Action<InspireSourceOptions>? tweak = null)
    {
        var options = new InspireSourceOptions
        {
            Source = "test",
            ParcelWfsUrl = FakeWfsServer.ParcelUrl,
            AddressSource = new AddressSourceOptions
            {
                Type = AddressSourceType.FlatWfs,
                Url = FakeWfsServer.AddressUrl,
                TypeName = "app:adressen",
                Fields = new AddressFieldOptions
                {
                    Street = "stn", HouseNumber = "hnr", HouseNumberSuffix = "adz", Plz = "plz", Ort = "onm", Gemeinde = "onm",
                },
            },
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

    private static InspirePropertyImporter Importer(FakeWfsServer server, InspireSourceOptions options)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(InspirePropertyImporter.HttpClientName)).ReturnsLazily(() => new HttpClient(server));
        return new InspirePropertyImporter(NullLogger<InspirePropertyImporter>.Instance, factory, options);
    }

    /// <summary>A 2×2 grid of 10 m parcels, one flat address in each centre.</summary>
    private static FakeWfsServer Server()
    {
        var server = new FakeWfsServer { FlatAddresses = true };
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < 2; j++)
            {
                server.Parcels.Add(new FakeParcel($"P{i}{j}", i * 10, j * 10, i * 10 + 10, j * 10 + 10, 100 + i * 2 + j));
                server.Addresses.Add(new FakeAddress($"A{i}{j}", i * 10 + 5, j * 10 + 5, $"Straße {i}{j}", "1",
                    Plz: "28195", Suffix: i == 1 && j == 1 ? "a" : null));
            }
        }
        return server;
    }

    private static async Task<(SourceProbe Probe, List<Property> Rows)> ImportAsync(InspirePropertyImporter importer)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = await importer.ProbeAsync(ct);
        var rows = new List<Property>();
        await foreach (var row in importer.FetchAsync(probe, new ImportRunContext(1, "test"), ct))
            rows.Add(row);
        return (probe, rows);
    }

    [Fact]
    public async Task FetchAsync_JoinsEachAddressToItsParcel_WithTheMappedFields()
    {
        var (_, rows) = await ImportAsync(Importer(Server(), Options()));

        rows.Should().HaveCount(4);
        rows.Should().ContainEquivalentOf(new
        {
            Str = "Straße 11", Hnr = "1", HnrZus = "a", Plz = "28195", Ort = "Testort", Gemeinde = "Testort", FlaecheAmtl = 103.0,
        });
        rows.Where(r => r.Str != "Straße 11").Should().OnlyContain(r => r.HnrZus == null);
    }

    [Fact]
    public async Task ProbeAsync_CombinesBothCounts()
    {
        var (probe, _) = await ImportAsync(Importer(Server(), Options()));

        probe.Fingerprint.Should().Be("4:4");
        probe.Kind.Should().Be(FingerprintKind.Approximate);
    }

    [Fact]
    public async Task FetchAsync_FullAddressPage_SplitsTheTile()
    {
        // Four parcels far apart, two addresses in each: the tile's 4 parcels fit a page of 5,
        // its 8 addresses don't.
        var server = new FakeWfsServer { FlatAddresses = true };
        foreach (var (x, y) in new[] { (0, 0), (60, 0), (0, 60), (60, 60) })
        {
            server.Parcels.Add(new FakeParcel($"P{x}_{y}", x, y, x + 10, y + 10, 100));
            server.Addresses.Add(new FakeAddress($"A{x}_{y}_1", x + 2, y + 2, $"Straße {x} {y}", "1", Plz: "28195"));
            server.Addresses.Add(new FakeAddress($"A{x}_{y}_2", x + 7, y + 7, $"Straße {x} {y}", "2", Plz: "28195"));
        }

        var (_, rows) = await ImportAsync(Importer(server, Options(o => o.PageSize = 5)));

        rows.Should().HaveCount(8);
        server.GetFeatureRequests(FakeWfsServer.AddressUrl).Should().HaveCount(5, "the whole tile, then its four quarters");
        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().ContainSingle("the quarters reuse the complete parcel page");
    }

    [Fact]
    public async Task Requests_AskForTheConfiguredType_AndBindItsPrefix()
    {
        var server = Server();

        await ImportAsync(Importer(server, Options(o => o.AddressSource.Namespace = AppNamespace)));

        var addressRequests = server.Requests
            .Where(u => u.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal)
                        && !u.Query.Contains("GetCapabilities", StringComparison.Ordinal))
            .ToList();
        addressRequests.Should().NotBeEmpty().And.OnlyContain(u =>
            u.Query.Contains("typenames=app%3Aadressen", StringComparison.Ordinal)
            && u.Query.Contains($"namespaces=xmlns(app,{Uri.EscapeDataString(AppNamespace)})", StringComparison.Ordinal)
            && !u.Query.Contains("resolve", StringComparison.Ordinal));
    }
}
