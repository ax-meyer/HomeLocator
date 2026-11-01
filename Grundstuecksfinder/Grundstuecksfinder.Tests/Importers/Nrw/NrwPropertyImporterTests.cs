using System.IO.Compression;
using System.Net;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Polly.Timeout;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Nrw;

public sealed class NrwPropertyImporterTests : IDisposable
{
    private const string ManifestUrl = "https://example.invalid/index.json";
    private const string BaseDownloadUrl = "https://example.invalid/";
    private const string ZipFileName = "grundsteuerdaten.zip";
    private const string ZipUrl = BaseDownloadUrl + ZipFileName;
    private const string GebrefManifestUrl = "https://example.invalid/gebref/index.json";
    private const string GebrefBaseUrl = "https://example.invalid/gebref/";
    private const string GebrefZipName = "gebref_EPSG25832_ASCII.zip";

    /// <summary>Per test, so tests running in parallel never share the fixed download name.</summary>
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), $"grundstuecksfinder-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    private string DownloadPath => Path.Combine(_workDirectory, NrwPropertyImporter.DownloadFileName);

    // GEBREF is off by default: most tests are about the Grundsteuer download alone.
    private NrwPropertyImporter Importer(FakeHttpMessageHandler handler, TimeProvider? time = null, double timeoutSeconds = 1800, bool gebref = false,
        IPostcodeAreaProvider? postcodeAreas = null)
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
            GebrefManifestUrl = gebref ? GebrefManifestUrl : null,
            GebrefBaseDownloadUrl = GebrefBaseUrl,
        });
        return new NrwPropertyImporter(NullLogger<NrwPropertyImporter>.Instance, factory, options, time, _workDirectory, postcodeAreas);
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

        probe.Should().Be(new NrwProbe("grundsteuerdaten_2026.zip", $"{NrwPropertyImporter.Revision}:grundsteuerdaten_2026.zip@2026-06-08T14:19:03"));
        probe.Kind.Should().Be(FingerprintKind.Exact, "the publisher's own timestamp: an unchanged edition is never downloaded again");
    }

    private static byte[] Zip(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry(entryName).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return ms.ToArray();
    }

    /// <summary>Grundsteuer and GEBREF routes; GEBREF knows the CSV's Hauptstraße 1 in Köln as "Hauptstr.".</summary>
    private static FakeHttpMessageHandler WithGebref(string? gebrefTxt = null)
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipWithOneRow()) });
        handler.AddRoute(GebrefManifestUrl, () => Json(
            $$"""{"datasets":[{"name":"gebref","files":[{"name":"{{GebrefZipName}}","timestamp":"2026-07-28T11:37:57"}]}]}"""));
        handler.AddRoute(GebrefBaseUrl + GebrefZipName, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Zip("gebref.txt",
                gebrefTxt ?? GebrefIndexTests.Line("Köln", "Hauptstr.", "1", "", 356000.5, 5645000.5))),
        });
        return handler;
    }

    [Fact]
    public async Task ProbeAsync_WithGebref_FingerprintsBothEditions()
    {
        var probe = await Importer(WithGebref(), gebref: true).ProbeAsync(TestContext.Current.CancellationToken);

        // A new GEBREF moves or adds positions, so it re-imports NRW like a new Grundsteuer edition.
        probe.Should().Be(new NrwProbe(ZipFileName,
            $"{NrwPropertyImporter.Revision}:{ZipFileName}@2026-06-08T14:19:03|{GebrefZipName}@2026-07-28T11:37:57", GebrefZipName));
        probe.Kind.Should().Be(FingerprintKind.Exact);
    }

    [Fact]
    public async Task FetchAsync_WithGebref_GivesRowsTheirBuildingsPosition()
    {
        var rows = await FetchAllAsync(Importer(WithGebref(), gebref: true));

        var (lon, lat) = new UtmToLonLat(25832).Transform(356000.5, 5645000.5);
        var row = rows.Should().ContainSingle().Subject;
        row.Lat.Should().Be((float)lat);
        row.Lon.Should().Be((float)lon);
    }

    [Fact]
    public async Task FetchAsync_AddressGebrefDoesNotKnow_StaysWithoutPosition()
    {
        var rows = await FetchAllAsync(Importer(
            WithGebref(GebrefIndexTests.Line("Köln", "Hauptstr.", "2", "", 356000, 5645000)), gebref: true));

        rows.Should().ContainSingle().Which.Lat.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_SuffixUneindeutig_TakesTheNumbersOwnBuilding()
    {
        var handler = WithGebref(string.Join('\n',
            GebrefIndexTests.Line("Köln", "Hauptstr.", "1", "", 356000.5, 5645000.5),
            GebrefIndexTests.Line("Köln", "Hauptstr.", "1", "a", 356020.5, 5645010.5)));
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Zip("part.csv", """
                id;a;b;c;str;hnr;hnr_zus;plz;ort;gemeinde;j;k;l;m;n;o;flaeche_amtl
                1;;;;Hauptstraße;1;uneindeutig;50667;Köln;Köln;;;;;;;320
                2;;;;Hauptstraße;2;uneindeutig;50667;Köln;Köln;;;;;;;320
                """)),
        });

        var rows = await FetchAllAsync(Importer(handler, gebref: true));

        var (lon, lat) = new UtmToLonLat(25832).Transform(356000.5, 5645000.5);
        rows[0].Lat.Should().Be((float)lat, "the parcel holds no. 1 and 1a; no. 1's building is on it");
        rows[0].Lon.Should().Be((float)lon);
        rows[1].Lat.Should().BeNull("GEBREF knows no no. 2 at all");
    }

    /// <summary>The Grundsteuer ZIP holding <paramref name="csvRows"/> under the 17-column header.</summary>
    private static void WithCsv(FakeHttpMessageHandler handler, params string[] csvRows) =>
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Zip("part.csv",
                string.Join('\n', ["id;alkisoid;flstkennz;lagebeztext;str;hnr;hnr_zus;plz;ort;gemeinde;j;k;l;m;n;o;flaeche_amtl", .. csvRows]))),
        });

    private static (float Lat, float Lon) LatLon(double east, double north)
    {
        var (lon, lat) = new UtmToLonLat(25832).Transform(east, north);
        return ((float)lat, (float)lon);
    }

    [Fact]
    public async Task FetchAsync_NumberUneindeutig_TakesTheFirstAddressOfTheParcelGebrefKnows()
    {
        // The real file's form: "uneindeutig" for the number, the parcel's numbers in lagebeztext.
        var handler = WithGebref(GebrefIndexTests.Line("Münster", "Anna-Peuler-Weg", "11", "", 400100.5, 5760100.5));
        WithCsv(handler, "122;DENW49AL00005UJp;05501901500229______;Anna-Peuler-Weg 9, 11;Anna-Peuler-Weg;uneindeutig;NULL;48161;Münster;Münster;Roxel;055019;;;;;672.0");

        var rows = await FetchAllAsync(Importer(handler, gebref: true));

        var row = rows.Should().ContainSingle().Subject;
        (row.Lat, row.Lon).Should().Be(LatLon(400100.5, 5760100.5), "GEBREF has no. 11, not no. 9");
        (row.Str, row.Hnr).Should().Be(("Anna-Peuler-Weg", "uneindeutig"), "the row's address stays as the cadastre has it");
    }

    [Fact]
    public async Task FetchAsync_StreetUneindeutig_SearchesEveryStreetOfTheParcel()
    {
        // Real rows: a parcel naming two streets has "uneindeutig" as street and number, and its
        // lagebeztext is quoted because it holds the CSV's delimiter.
        var handler = WithGebref(GebrefIndexTests.Line("Lünen", "Kupferstr.", "9", "", 396000.5, 5720000.5));
        WithCsv(handler,
            "133;DENW12AL0000dBbO;05128301600104______;\"Im Wiesengrund 3; Kupferstraße 9\";uneindeutig;uneindeutig;NULL;44532;Lünen;Lünen;Lünen;051283;\"[\"\"0512830010574\"\"]\";16;104;NULL;720.0;2023-12-08 09:54:59",
            "317;DENW45AL0000ltgm;05492702700008______;\"Im Winkel 5 a; Kirchplatz 1\";uneindeutig;uneindeutig;uneindeutig;51515;Kürten;Kürten;Kürten;054927;\"[\"\"0549270000650\"\"]\";27;8;NULL;407.0;2024-09-10 14:46:00");

        var rows = await FetchAllAsync(Importer(handler, gebref: true));

        rows.Should().HaveCount(2);
        (rows[0].Lat, rows[0].Lon).Should().Be(LatLon(396000.5, 5720000.5), "the parcel's second street is one GEBREF knows");
        rows[0].FlaecheAmtl.Should().Be(720.0, "the quoted fields don't shift the columns");
        rows[1].Lat.Should().BeNull("GEBREF knows neither Im Winkel 5a nor Kirchplatz 1");
    }

    [Fact]
    public async Task FetchAsync_OwnAddressFirst_ThenTheLagebezeichnung()
    {
        // The row's own address wins even where the parcel names another GEBREF knows too.
        var handler = WithGebref(string.Join('\n',
            GebrefIndexTests.Line("Köln", "Hauptstr.", "1", "", 356000.5, 5645000.5),
            GebrefIndexTests.Line("Köln", "Nebenstr.", "2", "", 356100.5, 5645100.5)));
        WithCsv(handler, "1;x;y;\"Nebenstraße 2; Hauptstraße 1\";Hauptstraße;1;NULL;50667;Köln;Köln;;;;;;;320");

        var rows = await FetchAllAsync(Importer(handler, gebref: true));

        (rows.Single().Lat, rows.Single().Lon).Should().Be(LatLon(356000.5, 5645000.5));
    }

    /// <summary>Postcode areas split at x = 372,500: 47051 west of it, 47166 east.</summary>
    private sealed class SplitAtX : IPostcodeAreaProvider, IPostcodeLookup
    {
        public Envelope? RequestedExtent { get; private set; }

        public Task<IPostcodeLookup> LoadAsync(int utmEpsg, Envelope bbox, CancellationToken ct)
        {
            utmEpsg.Should().Be(25832);
            RequestedExtent = bbox;
            return Task.FromResult<IPostcodeLookup>(this);
        }

        public string? FindPostcode(Point location) => location.X < 372_500 ? "47051" : "47166";
    }

    /// <summary>Duisburg has five Schulstraßen; GEBREF lists no. 3 at two of them.</summary>
    private static FakeHttpMessageHandler WithTwoSchulstrassen()
    {
        var handler = WithGebref(string.Join('\n',
            GebrefIndexTests.Line("Duisburg", "Schulstr.", "3", "", 370000.5, 5700000.5),
            GebrefIndexTests.Line("Duisburg", "Schulstr.", "3", "", 375000.5, 5704000.5)));
        WithCsv(handler, "1;x;y;Schulstraße 3;Schulstraße;3;NULL;47166;Duisburg;Duisburg;;;;;;;320");
        return handler;
    }

    [Fact]
    public async Task FetchAsync_AddressGebrefListsTwice_ThePostcodePicksTheBuilding()
    {
        var postcodes = new SplitAtX();

        var rows = await FetchAllAsync(Importer(WithTwoSchulstrassen(), gebref: true, postcodeAreas: postcodes));

        (rows.Single().Lat, rows.Single().Lon).Should().Be(LatLon(375000.5, 5704000.5), "only that Schulstraße lies in 47166");
        postcodes.RequestedExtent.Should().Be(new Envelope(370000.5, 375000.5, 5700000.5, 5704000.5), "areas are loaded over GEBREF's extent");
    }

    [Fact]
    public async Task FetchAsync_PostcodeAreasFail_ImportsTheRowsWithoutThatPosition()
    {
        var failing = A.Fake<IPostcodeAreaProvider>();
        A.CallTo(() => failing.LoadAsync(A<int>._, A<Envelope>._, A<CancellationToken>._)).ThrowsAsync(new HttpRequestException("down"));

        var rows = await FetchAllAsync(Importer(WithTwoSchulstrassen(), gebref: true, postcodeAreas: failing));

        rows.Should().ContainSingle().Which.Lat.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_NoAddressListedTwice_LoadsNoPostcodeAreas()
    {
        var postcodes = A.Fake<IPostcodeAreaProvider>();

        await FetchAllAsync(Importer(WithGebref(), gebref: true, postcodeAreas: postcodes));

        A.CallTo(postcodes).MustNotHaveHappened();
    }

    [Fact]
    public async Task FetchAsync_WithGebref_DeletesBothDownloads()
    {
        await FetchAllAsync(Importer(WithGebref(), gebref: true));

        Directory.EnumerateFiles(_workDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_GebrefZipWithoutItsTextFile_FailsBeforeTheBigDownload()
    {
        var handler = WithGebref();
        handler.AddRoute(GebrefBaseUrl + GebrefZipName, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Zip("readme.txt", "moved")),
        });

        var act = () => FetchAllAsync(Importer(handler, gebref: true));

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*gebref.txt*");
        handler.RequestedUris.Should().NotContain(new Uri(ZipUrl));
        Directory.EnumerateFiles(_workDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeAsync_WithoutGebref_IsTheGrundsteuerEditionAlone()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);

        var probe = await Importer(handler).ProbeAsync(TestContext.Current.CancellationToken);

        probe.Should().Be(new NrwProbe(ZipFileName, $"{NrwPropertyImporter.Revision}:{ZipFileName}@2026-06-08T14:19:03"));
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
    public async Task FetchAsync_DownloadsIntoTheWorkDirectoryAndDeletesTheFileAfterwards()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipWithOneRow()) });
        var importer = Importer(handler);
        var probe = await importer.ProbeAsync(TestContext.Current.CancellationToken);

        await foreach (var _ in importer.FetchAsync(probe, new ImportRunContext(1, NrwPropertyImporter.SourceId), TestContext.Current.CancellationToken))
            File.Exists(DownloadPath).Should().BeTrue("the rows are read from the download in the work directory");

        File.Exists(DownloadPath).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_LeftoverFromAnInterruptedImport_IsReplaced()
    {
        Directory.CreateDirectory(_workDirectory);
        await File.WriteAllTextAsync(DownloadPath, "half a ZIP from a crashed run", TestContext.Current.CancellationToken);
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ManifestUrl, Manifest);
        handler.AddRoute(ZipUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipWithOneRow()) });

        var rows = await FetchAllAsync(Importer(handler));

        rows.Should().ContainSingle().Which.Str.Should().Be("Hauptstraße");
        File.Exists(DownloadPath).Should().BeFalse();
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
