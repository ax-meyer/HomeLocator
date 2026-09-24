using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire.Addresses;

public sealed class HkFileAddressProviderTests : IDisposable
{
    private const string FileUrl = "https://opengeodata.example/data/hk/hk_bw.zip";

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), $"hk-provider-tests-{Guid.NewGuid():N}");
    private readonly FakeHttpMessageHandler _handler = new();

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true);
    }

    private HkFileAddressProvider Provider(
        Action<AddressSourceOptions>? tweak = null, ILogger? logger = null, Func<InspireServiceClient, IHkFileLocator>? locator = null)
    {
        var options = new InspireSourceOptions
        {
            Source = "test",
            Crs = "urn:ogc:def:crs:EPSG::25832",
            MaxAttempts = 3,
            RetryBaseDelaySeconds = 0,
            MaxRetryDelaySeconds = 0,
            MinRequestIntervalSeconds = 0,
            AddressSource = new AddressSourceOptions { Type = AddressSourceType.HkFile, Url = FileUrl, Member = HkFiles.Member },
        };
        tweak?.Invoke(options.AddressSource);
        var client = new InspireServiceClient(new HttpClient(_handler), options, NullLogger.Instance, TimeProvider.System);
        return new HkFileAddressProvider(client, options, locator?.Invoke(client) ?? new StaticUrlHkFileLocator(client, FileUrl),
            _workDirectory, logger ?? NullLogger.Instance);
    }

    /// <summary>What a probe of the file <see cref="HkFiles.Response"/> serves finds.</summary>
    private static readonly FingerprintPart Probed = new("\"4607dc5-656a14021d8ec\"|2026-07-15T07:27:05Z", IsExact: true);

    private void Serve(byte[] zip) => _handler.AddRoute(FileUrl, () => HkFiles.Response(zip));

    private static readonly string TwoQualities = HkFiles.Text(
    [
        HkFiles.Row(5, 5, "Lindenweg", "1"),
        HkFiles.Row(15, 5, "Lindenweg", "3", qua: "B"),
        HkFiles.Row(25, 5, "Distr. Am Ottersberg", "86188851", qua: "C"),
    ]);

    private string[] FilesLeft() => Directory.Exists(_workDirectory) ? Directory.GetFiles(_workDirectory) : [];

    [Fact]
    public async Task ProbeAsync_IsTheFilesExactVersion()
    {
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));

        var part = await Provider().ProbeAsync(TestContext.Current.CancellationToken);

        part.Should().Be(new FingerprintPart("\"4607dc5-656a14021d8ec\"|2026-07-15T07:27:05Z", IsExact: true));
        _handler.RequestDetails.Should().OnlyContain(r => r.Method == HttpMethod.Head, "a probe never downloads the file");
    }

    [Fact]
    public async Task LoadAsync_ReadsTheMemberKeepingTheAllowedQualities()
    {
        Serve(HkFiles.Zip(("Aktualitaet-HK.txt", "2026-07-15"), (HkFiles.Member, TwoQualities)));

        var addresses = (PreloadedAddresses)await Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);

        addresses.Query(new Envelope(0, 100, 0, 100)).Select(a => a.Hnr).Should().BeEquivalentTo("1", "3");
        addresses.ExpectedCount.Should().Be(2, "the completeness check measures against the rows kept, not the file's total");
    }

    [Fact]
    public async Task LoadAsync_FileChangedSinceTheProbe_ImportsTheCurrentOneAndSaysSo()
    {
        // An initial run probes every source first and may reach this one hours later.
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));
        var logger = new CapturingLogger<HkFileAddressProviderTests>();

        var addresses = await Provider(logger: logger).LoadAsync(
            new FingerprintPart("\"older\"|2026-01-15T07:27:05Z", IsExact: true), TestContext.Current.CancellationToken);

        addresses.ExpectedCount.Should().Be(2);
        logger.MessagesAt(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("was \"older\"|2026-01-15T07:27:05Z, now \"4607dc5-656a14021d8ec\"|2026-07-15T07:27:05Z");
    }

    [Fact]
    public async Task LoadAsync_FileAsProbed_WarnsAboutNothing()
    {
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));
        var logger = new CapturingLogger<HkFileAddressProviderTests>();
        var provider = Provider(logger: logger);

        await provider.LoadAsync(await provider.ProbeAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        logger.MessagesAt(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_DeletesTheDownloadAfterReadingIt()
    {
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));

        await Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);

        Directory.Exists(_workDirectory).Should().BeTrue("the download went there");
        FilesLeft().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_ReadFails_StillDeletesTheDownload()
    {
        Serve(HkFiles.Zip(("something-else.txt", TwoQualities)));

        var act = () => Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*0 entries*match \"adressen-bw.txt\"*something-else.txt*");
        FilesLeft().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_NoAddressOfAnAllowedQuality_FailsTheImport()
    {
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));

        var act = () => Provider(o => o.AllowedQualities = ["D"]).LoadAsync(Probed, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*no addresses of quality D*");
    }

    [Fact]
    public async Task LoadAsync_DownloadFailsOnce_IsRetried()
    {
        var zip = HkFiles.Zip((HkFiles.Member, TwoQualities));
        var gets = 0;
        _handler.AddRoute(FileUrl, () => ++gets == 2 // the first request is the locator's HEAD
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : HkFiles.Response(zip));

        var addresses = await Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);

        addresses.ExpectedCount.Should().Be(2);
        _handler.RequestDetails.Count(r => r.Method == HttpMethod.Get).Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_TruncatedDownload_IsRetried()
    {
        var zip = HkFiles.Zip((HkFiles.Member, TwoQualities));
        var gets = 0;
        _handler.AddRoute(FileUrl, () =>
        {
            var response = HkFiles.Response(++gets == 2 ? zip[..(zip.Length / 2)] : zip);
            response.Content.Headers.ContentLength = zip.Length; // claims the whole file
            return response;
        });

        var addresses = await Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);

        addresses.ExpectedCount.Should().Be(2);
        gets.Should().Be(4, "HEAD and the short GET, then HEAD and the full GET: every attempt locates the file anew");
    }

    [Fact]
    public async Task ProbeAsync_ServerError_IsRetried()
    {
        var calls = 0;
        _handler.AddRoute(FileUrl, () => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : HkFiles.Response([]));

        var part = await Provider().ProbeAsync(TestContext.Current.CancellationToken);

        part.Should().Be(Probed);
        calls.Should().Be(2);
    }

    /// <summary>A locator handing out a new link on every call, as Hessen's does from one day to the next.</summary>
    private sealed class DailyLinkLocator : IHkFileLocator
    {
        public List<string> Handed { get; } = [];

        public Task<HkFileLocation> LocateAsync(CancellationToken ct)
        {
            var url = FormattableString.Invariant($"https://gds.example/downloadcenter/day{Handed.Count + 1}/hk.zip");
            Handed.Add(url);
            return Task.FromResult(new HkFileLocation(url, "Edition-2026-01|24.06.2026"));
        }
    }

    [Fact]
    public async Task LoadAsync_DownloadRetried_LocatesTheFileAgain()
    {
        // A retry after midnight must not reuse yesterday's Hessen link: it only works on its day.
        var zip = HkFiles.Zip((HkFiles.Member, TwoQualities));
        _handler.AddRoute("https://gds.example/downloadcenter/day1/hk.zip", () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _handler.AddRoute("https://gds.example/downloadcenter/day2/hk.zip", () => HkFiles.Response(zip));
        var locator = new DailyLinkLocator();

        var addresses = await Provider(locator: _ => locator)
            .LoadAsync(new FingerprintPart("Edition-2026-01|24.06.2026", true), TestContext.Current.CancellationToken);

        addresses.ExpectedCount.Should().Be(2);
        locator.Handed.Should().HaveCount(2);
        _handler.RequestedUris.Select(u => u.ToString()).Should().Equal(locator.Handed);
    }

    [Fact]
    public async Task LoadAsync_LeftoverFromAKilledImport_IsReplacedAndDeleted()
    {
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));
        Directory.CreateDirectory(_workDirectory);
        var leftover = Path.Combine(_workDirectory, HkFileAddressProvider.DownloadFileName("test"));
        await File.WriteAllTextAsync(leftover, "half a ZIP", TestContext.Current.CancellationToken);

        var addresses = await Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);

        addresses.ExpectedCount.Should().Be(2, "the leftover was replaced by a fresh download");
        FilesLeft().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_CarriesTheVersionItLoaded()
    {
        Serve(HkFiles.Zip((HkFiles.Member, TwoQualities)));

        var asProbed = await Provider().LoadAsync(Probed, TestContext.Current.CancellationToken);
        var newer = await Provider().LoadAsync(new FingerprintPart("\"older\"|2026-01-15T07:27:05Z", true), TestContext.Current.CancellationToken);

        asProbed.Loaded.Should().Be(Probed);
        newer.Loaded.Should().Be(Probed, "the file downloaded is the current edition, whatever the probe saw");
    }

    [Theory]
    [InlineData("adressen-bw.txt", "adressen-bw.txt")]
    [InlineData("Hauskoordinaten*.txt", "Hauskoordinaten ohne Postalische Angaben-2026-01.txt")]
    [InlineData("hauskoordinaten ohne postalische angaben-????-??.txt", "Hauskoordinaten ohne Postalische Angaben-2026-01.txt")]
    public void SelectMember_MatchesANameOrAPattern(string pattern, string expected)
    {
        using var zip = new ZipArchive(new MemoryStream(HkFiles.Zip(
            ("Hauskoordinaten ohne Postalische Angaben-2026-01.txt", ""), ("adressen-bw.txt", ""), ("Meta-ALKIS-HK.txt", ""))));

        HkFileAddressProvider.SelectMember(zip.Entries, pattern).FullName.Should().Be(expected);
    }

    [Fact]
    public void SelectMember_SeveralMatches_FailsRatherThanGuess()
    {
        using var zip = new ZipArchive(new MemoryStream(HkFiles.Zip(("a.txt", ""), ("b.txt", ""))));

        var act = () => HkFileAddressProvider.SelectMember(zip.Entries, "*.txt");

        act.Should().Throw<InspireImportException>().WithMessage("2 entries*");
    }
}
