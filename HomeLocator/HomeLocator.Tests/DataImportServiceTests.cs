using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Xunit;
using FluentAssertions;
using HomeLocator.Data;
using HomeLocator.Services;
using HomeLocator.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeLocator.Tests;

[Collection("Postgres")]
public class DataImportServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ManifestUrl = "http://fake/manifest.json";
    private const string BaseDownloadUrl = "http://fake/downloads/";
    private const string DatasetName = "grundsteuer_nrw";
    private const string ZipFileName = "grundsteuer.zip";
    private const string ZipTimestamp = "2026-01-01T00:00:00Z";

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private DataImportService BuildService(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var httpFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpFactory.CreateClient(A<string>._)).Returns(http);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Import:ManifestUrl"] = ManifestUrl,
                ["Import:BaseDownloadUrl"] = BaseDownloadUrl,
            })
            .Build();

        return new DataImportService(
            NullLogger<DataImportService>.Instance,
            httpFactory,
            scopeFactory,
            fixture.DataSource,
            config);
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
            $"{BaseDownloadUrl}{DatasetName}/{ZipFileName}",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BuildZipWithCsv())
            });

        var service = BuildService(handler);
        await service.CheckAndImportAsync();

        await using var context = fixture.CreateContext();
        var count = await context.Properties.CountAsync();
        count.Should().BeGreaterThan(0, "CSV rows should have been imported");
    }

    [Fact]
    public async Task CheckAndImportAsync_ValidData_CreatesImportLog()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildManifestJson(), Encoding.UTF8, "application/json")
        });
        handler.AddRoute(
            $"{BaseDownloadUrl}{DatasetName}/{ZipFileName}",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BuildZipWithCsv())
            });

        var service = BuildService(handler);
        await service.CheckAndImportAsync();

        await using var context = fixture.CreateContext();
        var log = await context.ImportLogs.FirstOrDefaultAsync();

        log.Should().NotBeNull();
        log!.DatasetName.Should().Be(DatasetName);
        log.FileName.Should().Be(ZipFileName);
        log.FileTimestamp.Should().Be(ZipTimestamp);
        log.RecordCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CheckAndImportAsync_AlreadyImported_SkipsImport()
    {
        // Pre-populate the import log with the same timestamp
        await using (var context = fixture.CreateContext())
        {
            context.ImportLogs.Add(new HomeLocator.Models.ImportLog
            {
                DatasetName = DatasetName,
                FileName = ZipFileName,
                FileTimestamp = ZipTimestamp,
                ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RecordCount = 999
            });
            await context.SaveChangesAsync();
        }

        var downloadCalled = false;
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildManifestJson(), Encoding.UTF8, "application/json")
        });
        handler.AddRoute(
            $"{BaseDownloadUrl}{DatasetName}/{ZipFileName}",
            () =>
            {
                downloadCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildZipWithCsv())
                };
            });

        var service = BuildService(handler);
        await service.CheckAndImportAsync();

        downloadCalled.Should().BeFalse("ZIP should not be downloaded when already imported");
    }

    [Fact]
    public async Task CheckAndImportAsync_ManifestFetchFails_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var service = BuildService(handler);
        var act = () => service.CheckAndImportAsync();

        await act.Should().NotThrowAsync();
    }
}
