using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Xunit;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Grundstuecksfinder.Tests.Importers.Nrw;

[Collection("Postgres")]
public sealed class NrwPropertyImporterIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), $"grundstuecksfinder-test-{Guid.NewGuid():N}");

    private const string ManifestUrl = "http://fake/manifest.json";
    private const string BaseDownloadUrl = "http://fake/downloads/";
    private const string DatasetName = "grundsteuer_nrw";
    private const string ZipFileName = "grundsteuer.zip";

    // opengeodata.nrw.de serves files directly under the base URL, with no dataset-name
    // path segment. Format matches the real manifest (and the "s" format the service stores).
    private const string ZipTimestamp = "2026-01-01T00:00:00";
    private const string ZipUrl = $"{BaseDownloadUrl}{ZipFileName}";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }

    private const string GebrefManifestUrl = "http://fake/gebref/index.json";
    private const string GebrefBaseUrl = "http://fake/gebref/";

    private Task RunImportsAsync(FakeHttpMessageHandler handler, CancellationToken ct = default) =>
        RunImportsAsync(handler, gebref: false, ct);

    private Task RunImportsAsync(FakeHttpMessageHandler handler, bool gebref, CancellationToken ct)
    {
        var http = new HttpClient(handler);
        var httpFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpFactory.CreateClient(A<string>._)).Returns(http);

        var options = Options.Create(new NrwImporterOptions
        {
            ManifestUrl = ManifestUrl,
            BaseDownloadUrl = BaseDownloadUrl,
            GebrefManifestUrl = gebref ? GebrefManifestUrl : null,
            GebrefBaseDownloadUrl = GebrefBaseUrl,
        });
        var importer = new NrwPropertyImporter(NullLogger<NrwPropertyImporter>.Instance, httpFactory, options, workDirectory: _workDirectory);
        return fixture.RunImportsAsync([importer], ct: ct);
    }

    private static byte[] BuildZipWithCsv()
    {
        var csvPath = Path.Combine(AppContext.BaseDirectory, "TestData", "part.csv");
        var csvBytes = File.ReadAllBytes(csvPath);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("grundsteuer.csv");
            using var entryStream = entry.Open();
            entryStream.Write(csvBytes);
        }
        return ms.ToArray();
    }

    private static string BuildManifestJson() =>
        JsonSerializer.Serialize(new
        {
            datasets = new[]
            {
                new
                {
                    name = DatasetName,
                    title = "Grundsteuer NRW",
                    files = new[] { new { name = ZipFileName, size = "1000", timestamp = ZipTimestamp } }
                }
            }
        });

    [Fact]
    public async Task RunAsync_ValidData_ImportsRowsFromCsv()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildManifestJson(), Encoding.UTF8, "application/json")
        });
        handler.AddRoute(
            ZipUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BuildZipWithCsv())
            });

        await RunImportsAsync(handler, TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var properties = await context.Properties.ToListAsync(TestContext.Current.CancellationToken);
        properties.Should().NotBeEmpty("CSV rows should have been imported");
        properties.Should().OnlyContain(p => p.Source == NrwPropertyImporter.SourceId);
    }

    [Fact]
    public async Task RunAsync_WithGebref_StoresTheBuildingsPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildManifestJson(), Encoding.UTF8, "application/json")
        });
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BuildZipWithCsv()) });
        handler.AddRoute(GebrefManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"datasets":[{"name":"gebref","files":[{"name":"gebref.zip","timestamp":"2026-07-28T11:37:57"}]}]}""",
                Encoding.UTF8, "application/json")
        });
        handler.AddRoute(GebrefBaseUrl + "gebref.zip", () =>
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var stream = archive.CreateEntry("gebref.txt").Open();
                // part.csv's "Im Sand 4, 53225 Bonn".
                stream.Write(Encoding.UTF8.GetBytes(GebrefIndexTests.Line("Bonn", "Im Sand", "4", "", 368000.25, 5626000.75)));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ms.ToArray()) };
        });

        await RunImportsAsync(handler, gebref: true, ct);

        await using var context = fixture.CreateContext();
        var located = await context.Properties.Where(p => p.Lat != null).ToListAsync(ct);
        var (lon, lat) = new UtmToLonLat(25832).Transform(368000.25, 5626000.75);
        located.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Str = "Im Sand", Hnr = "4", Gemeinde = "Bonn", Lat = (float?)lat, Lon = (float?)lon });
        (await context.ImportRuns.SingleAsync(ct)).Fingerprint.Should().EndWith("|gebref.zip@2026-07-28T11:37:57");
    }

    [Fact]
    public async Task RunAsync_ValidData_RecordsACompletedRun()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildManifestJson(), Encoding.UTF8, "application/json")
        });
        handler.AddRoute(
            ZipUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BuildZipWithCsv())
            });

        await RunImportsAsync(handler, TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var run = await context.ImportRuns.FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        run.Should().NotBeNull();
        run!.Source.Should().Be(NrwPropertyImporter.SourceId);
        run.Fingerprint.Should().Be($"{ZipFileName}@{ZipTimestamp}");
        run.RecordCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAsync_AlreadyImported_SkipsImport()
    {
        // The same version is already being served
        await using (var context = fixture.CreateContext())
        {
            context.SourceStates.Add(ImportSeed.Serving(ImportSeed.Completed(
                NrwPropertyImporter.SourceId, DateTimeOffset.UtcNow, recordCount: 999,
                fingerprint: $"{ZipFileName}@{ZipTimestamp}")));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var downloadCalled = false;
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildManifestJson(), Encoding.UTF8, "application/json")
        });
        handler.AddRoute(
            ZipUrl,
            () =>
            {
                downloadCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildZipWithCsv())
                };
            });

        await RunImportsAsync(handler, TestContext.Current.CancellationToken);

        downloadCalled.Should().BeFalse("ZIP should not be downloaded when already imported");
    }

    [Fact]
    public async Task RunAsync_ManifestFetchFails_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => RunImportsAsync(handler);

        await act.Should().NotThrowAsync();
        await using var context = fixture.CreateContext();
        (await context.SourceStates.SingleAsync(TestContext.Current.CancellationToken)).LastProbeError.Should().Contain("503");
    }
}
