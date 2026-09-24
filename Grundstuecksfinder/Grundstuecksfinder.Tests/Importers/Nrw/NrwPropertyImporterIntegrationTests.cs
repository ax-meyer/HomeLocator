using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Xunit;
using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Grundstuecksfinder.Tests.Importers.Nrw;

[Collection("Postgres")]
public sealed class NrwPropertyImporterIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ManifestUrl = "http://fake/manifest.json";
    private const string BaseDownloadUrl = "http://fake/downloads/";
    private const string DatasetName = "grundsteuer_nrw";
    private const string ZipFileName = "grundsteuer.zip";

    // opengeodata.nrw.de serves files directly under the base URL, with no dataset-name
    // path segment. Format matches the real manifest (and the "s" format the service stores).
    private const string ZipTimestamp = "2026-01-01T00:00:00";
    private const string ZipUrl = $"{BaseDownloadUrl}{ZipFileName}";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ImportOrchestrator BuildOrchestrator(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var httpFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpFactory.CreateClient(A<string>._)).Returns(http);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new NrwImporterOptions { ManifestUrl = ManifestUrl, BaseDownloadUrl = BaseDownloadUrl });
        var importer = new NrwPropertyImporter(NullLogger<NrwPropertyImporter>.Instance, httpFactory, options);
        var bulkWriter = new PropertyBulkWriter(fixture.DataSource);

        return new ImportOrchestrator([importer], NullLogger<ImportOrchestrator>.Instance, scopeFactory, bulkWriter);
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
    public async Task CheckAndImportAsync_ValidData_ImportsRowsFromCsv()
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

        var orchestrator = BuildOrchestrator(handler);
        await orchestrator.CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var properties = await context.Properties.ToListAsync(TestContext.Current.CancellationToken);
        properties.Should().NotBeEmpty("CSV rows should have been imported");
        properties.Should().OnlyContain(p => p.Source == NrwPropertyImporter.SourceId);
    }

    [Fact]
    public async Task CheckAndImportAsync_ValidData_RecordsACompletedRun()
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

        var orchestrator = BuildOrchestrator(handler);
        await orchestrator.CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var run = await context.ImportRuns.FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        run.Should().NotBeNull();
        run!.Source.Should().Be(NrwPropertyImporter.SourceId);
        run.Fingerprint.Should().Be($"{DatasetName}/{ZipFileName}/{ZipTimestamp}");
        run.RecordCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CheckAndImportAsync_AlreadyImported_SkipsImport()
    {
        // The same version is already being served
        await using (var context = fixture.CreateContext())
        {
            context.SourceStates.Add(ImportSeed.Serving(ImportSeed.Completed(
                NrwPropertyImporter.SourceId, DateTimeOffset.UtcNow, recordCount: 999,
                fingerprint: $"{DatasetName}/{ZipFileName}/{ZipTimestamp}")));
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

        var orchestrator = BuildOrchestrator(handler);
        await orchestrator.CheckAndImportAsync(TestContext.Current.CancellationToken);

        downloadCalled.Should().BeFalse("ZIP should not be downloaded when already imported");
    }

    [Fact]
    public async Task CheckAndImportAsync_ManifestFetchFails_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var orchestrator = BuildOrchestrator(handler);
        var act = () => orchestrator.CheckAndImportAsync();

        await act.Should().NotThrowAsync();
    }
}
