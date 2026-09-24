using System.IO.Compression;
using System.Net;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Nrw;

public sealed class NrwPropertyImporterTests
{
    private const string ManifestUrl = "https://example.invalid/index.json";
    private const string BaseDownloadUrl = "https://example.invalid/";
    private const string ZipFileName = "grundsteuerdaten.zip";
    private const string ZipUrl = BaseDownloadUrl + ZipFileName;

    private static NrwPropertyImporter Importer(FakeHttpMessageHandler handler, TimeProvider? time = null, double timeoutSeconds = 1800)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(handler));
        var options = Options.Create(new NrwImporterOptions
        {
            ManifestUrl = ManifestUrl,
            BaseDownloadUrl = BaseDownloadUrl,
            MaxAttempts = 3,
            RetryBaseDelaySeconds = 0,
            MaxRetryDelaySeconds = 0,
            DownloadTimeoutSeconds = timeoutSeconds,
        });
        return new NrwPropertyImporter(NullLogger<NrwPropertyImporter>.Instance, factory, options, time);
    }

    /// <summary>One CSV row with the 17 columns the NRW column map expects.</summary>
    private static byte[] ZipWithOneRow()
    {
        var csv = new StringBuilder()
            .AppendLine("id;a;b;c;str;hnr;hnr_zus;plz;ort;gemeinde;j;k;l;m;n;o;flaeche_amtl")
            .AppendLine("1;;;;Hauptstraße;1;;50667;Köln;Köln;;;;;;;320")
            .ToString();

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("part.csv");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(csv));
        }
        return ms.ToArray();
    }

    private static HttpResponseMessage Manifest() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"datasets":[{"name":"grundsteuerdaten","files":[{"name":"{{ZipFileName}}","timestamp":"2026-06-08T14:19:03"}]}]}""",
            Encoding.UTF8, "application/json"),
    };

    private static async Task<List<Property>> FetchAllAsync(NrwPropertyImporter importer)
    {
        var probe = await importer.ProbeAsync(TestContext.Current.CancellationToken);
        var rows = new List<Property>();
        await foreach (var row in importer.FetchAsync(probe, new ImportRunContext(1, NrwPropertyImporter.SourceId), TestContext.Current.CancellationToken))
            rows.Add(row);
        return rows;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task ProbeAsync_FingerprintIsTheNewestEditionsNameAndTimestamp()
    {
        // The real manifest keeps every year's edition; only the newest is imported, since each
        // import replaces all of NRW's rows.
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => Json("""
            {"datasets":[{"name":"grundsteuerdaten","files":[
              {"name":"grundsteuerdaten_2025.zip","timestamp":"2025-08-20T13:14:42"},
              {"name":"grundsteuerdaten_2026.zip","timestamp":"2026-06-08T14:19:03"},
              {"name":"grundsteuerdaten_2024.zip","timestamp":"2025-01-24T21:47:18"}]}]}
            """));

        var probe = await Importer(handler).ProbeAsync(TestContext.Current.CancellationToken);

        probe.Should().Be(new NrwProbe("grundsteuerdaten_2026.zip", "grundsteuerdaten_2026.zip@2026-06-08T14:19:03"));
        probe.Kind.Should().Be(FingerprintKind.Exact, "the publisher's own timestamp: an unchanged edition is never downloaded again");
    }

    [Fact]
    public async Task ProbeAsync_ManifestWithoutFiles_Fails()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, () => Json("""{"datasets":[]}"""));

        var act = () => Importer(handler).ProbeAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*lists no files*");
    }

    [Fact]
    public async Task FetchAsync_DownloadsTheFileTheProbeFound()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(BaseDownloadUrl + "probed.zip", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipWithOneRow()) });
        var probe = new NrwProbe("probed.zip", "probed.zip@2026-01-01T00:00:00");

        var rows = new List<Property>();
        await foreach (var row in Importer(handler).FetchAsync(probe, new ImportRunContext(1, NrwPropertyImporter.SourceId), TestContext.Current.CancellationToken))
            rows.Add(row);

        rows.Should().ContainSingle();
        handler.RequestedUris.Should().NotContain(u => u.ToString() == ManifestUrl, "the probe already said which file");
    }

    [Fact]
    public async Task FetchAsync_DownloadFailsTransiently_IsRetried()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        var attempts = 0;
        handler.AddRoute(ZipUrl, () =>
        {
            attempts++;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipWithOneRow()) };
        });

        var rows = await FetchAllAsync(Importer(handler));

        attempts.Should().Be(3, "the first two attempts got a 503");
        rows.Should().ContainSingle().Which.Str.Should().Be("Hauptstraße");
    }

    [Fact]
    public async Task FetchAsync_DownloadNotFound_IsNotRetried()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => FetchAllAsync(Importer(handler));

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.RequestedUris.Count(u => u.ToString() == ZipUrl)
            .Should().Be(1, "a 404 means the request itself is wrong");
    }

    [Fact]
    public async Task ProbeAsync_ManifestFailsTransiently_IsRetried()
    {
        var handler = new FakeHttpMessageHandler();
        var attempts = 0;
        handler.AddRoute(ManifestUrl, () =>
        {
            attempts++;
            return attempts < 2 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Manifest();
        });

        var probe = await Importer(handler).ProbeAsync(TestContext.Current.CancellationToken);

        attempts.Should().Be(2);
        probe.Should().BeOfType<NrwProbe>().Which.FileName.Should().Be(ZipFileName);
    }

    /// <summary>
    /// Runs an operation that waits on the pipeline's clock and advances that clock until it
    /// finishes, so the timeout costs no real time.
    /// </summary>
    private static async Task<T> WithVirtualTimeAsync<T>(
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
        return await task;
    }

    [Fact]
    public async Task FetchAsync_DownloadStalls_IsAbandonedAfterTheDownloadTimeout()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        // Headers arrive, then the body never does: HttpClient.Timeout wouldn't catch this.
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });

        var time = new FakeTimeProvider();
        var task = FetchAllAsync(Importer(handler, time, timeoutSeconds: 600));

        var act = () => WithVirtualTimeAsync(time, task, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3000));

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        handler.RequestedUris.Count(u => u.ToString() == ZipUrl)
            .Should().Be(3, "a stalled download is still a transient failure, so MaxAttempts applies");
    }

    [Fact]
    public async Task FetchAsync_DownloadKeepsFailing_StopsAfterMaxAttempts()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var act = () => FetchAllAsync(Importer(handler));

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.RequestedUris.Count(u => u.ToString() == ZipUrl).Should().Be(3, "MaxAttempts is 3");
    }

    /// <summary>A body that never arrives and only ends when the read is cancelled.</summary>
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
}
