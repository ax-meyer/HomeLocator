using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire.Addresses;

public sealed class HkFileLocatorTests
{
    private const string FileUrl = "https://opengeodata.example/data/hk/hk_bw.zip";
    private const string ListingUrl =
        "https://gds.example/INTERSHOP/rest/WFS/HLBG-Geodaten-Site/-/downloadcenter?path=Liegenschaftskataster/Hauskoordinaten%20ohne%20Postalische%20Angaben%20(txt)&navigation=all";

    private static InspireServiceClient Client(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), new InspireSourceOptions
        {
            Source = "test",
            MaxAttempts = 3,
            RetryBaseDelaySeconds = 0,
            MaxRetryDelaySeconds = 0,
            MinRequestIntervalSeconds = 0,
        }, NullLogger.Instance, TimeProvider.System);

    // ── Static URL ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StaticUrl_VersionsTheFileByItsHeadersWithoutDownloadingIt()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(FileUrl, () => HkFiles.Response([1, 2, 3]));

        var location = await new StaticUrlHkFileLocator(Client(handler), FileUrl).LocateAsync(TestContext.Current.CancellationToken);

        location.Should().Be(new HkFileLocation(FileUrl, "\"4607dc5-656a14021d8ec\"|2026-07-15T07:27:05Z"));
        handler.RequestDetails.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Head);
    }

    [Fact]
    public async Task StaticUrl_OnlyLastModified_IsEnough()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(FileUrl, () =>
        {
            var response = HkFiles.Response([]);
            response.Headers.ETag = null;
            return response;
        });

        var location = await new StaticUrlHkFileLocator(Client(handler), FileUrl).LocateAsync(TestContext.Current.CancellationToken);

        location.Version.Should().Be("2026-07-15T07:27:05Z");
    }

    [Fact]
    public async Task StaticUrl_NoVersionHeaders_IsNothingToImport()
    {
        // Without a version marker a new edition can't be told from the last one.
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(FileUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

        var act = () => new StaticUrlHkFileLocator(Client(handler), FileUrl).LocateAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InspireImportException>().WithMessage("*neither an ETag nor a Last-Modified*");
    }

    // ── Hessen download center ───────────────────────────────────────────────

    /// <summary>The shape of the real listing (trimmed to what matters), with one or more files.</summary>
    private static string Listing(params (string Name, string Created, string Extension, string Uri)[] files) =>
        JsonSerializer.Serialize(new
        {
            breadcrumb = new[] { new { name = "Downloadcenter", uri = "/INTERSHOP/rest/WFS/HLBG-Geodaten-Site/-/downloadcenter" } },
            searchresult = new
            {
                packages = Array.Empty<object>(),
                product = new { name = "Hauskoordinaten ohne Postalische Angaben (txt)", id = "DC0100200" },
                downloads = files.Select(f => new
                {
                    name = f.Name,
                    id = "DP0111000",
                    fileExtension = f.Extension,
                    fileSize = "27,4 MB",
                    creationDate = f.Created,
                    downloadType = "File",
                    downloadLink = new { name = "Datei herunterladen", uri = f.Uri },
                }),
            },
        });

    private const string EditionUri =
        "/downloadcenter/20260924/Liegenschaftskataster/Hauskoordinaten ohne Postalische Angaben (txt)/Hauskoordinaten ohne Postalische Angaben-2026-01.zip";

    private static HkFileLocation Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return HessenDownloadCenterLocator.ParseListing(doc, new Uri(ListingUrl));
    }

    [Fact]
    public void DownloadCenter_ResolvesTheLinkAgainstTheHostAndEscapesIt()
    {
        var location = Parse(Listing(("Hauskoordinaten ohne Postalische Angaben-2026-01", "24.06.2026", "ZIP", EditionUri)));

        location.Url.Should().Be(
            "https://gds.example/downloadcenter/20260924/Liegenschaftskataster/Hauskoordinaten%20ohne%20Postalische%20Angaben%20%28txt%29/Hauskoordinaten%20ohne%20Postalische%20Angaben-2026-01.zip");
    }

    [Fact]
    public void DownloadCenter_VersionIsTheEditionNameAndDate_NotTheDailyLink()
    {
        // The link changes every day (its date segment); the edition doesn't.
        var today = Parse(Listing(("Hauskoordinaten ohne Postalische Angaben-2026-01", "24.06.2026", "ZIP", EditionUri)));
        var tomorrow = Parse(Listing(("Hauskoordinaten ohne Postalische Angaben-2026-01", "24.06.2026", "ZIP",
            EditionUri.Replace("20260924", "20260925", StringComparison.Ordinal))));

        today.Version.Should().Be("Hauskoordinaten ohne Postalische Angaben-2026-01|24.06.2026");
        tomorrow.Version.Should().Be(today.Version);
        tomorrow.Url.Should().NotBe(today.Url);
    }

    [Fact]
    public void DownloadCenter_SeveralEditions_TakesTheNewest()
    {
        var location = Parse(Listing(
            ("Hauskoordinaten-2025-07", "20.12.2025", "ZIP", "/downloadcenter/20260924/x/Hauskoordinaten-2025-07.zip"),
            ("Hauskoordinaten-2026-01", "24.06.2026", "ZIP", "/downloadcenter/20260924/x/Hauskoordinaten-2026-01.zip"),
            ("Beschreibung", "30.06.2026", "PDF", "/downloadcenter/20260924/x/Beschreibung.pdf")));

        location.Version.Should().Be("Hauskoordinaten-2026-01|24.06.2026");
    }

    [Fact]
    public void DownloadCenter_AlreadyEscapedLink_IsNotEscapedTwice()
    {
        var location = Parse(Listing(("Edition", "24.06.2026", "ZIP", "/downloadcenter/20260924/Hauskoordinaten%20ohne%20Angaben.zip")));

        location.Url.Should().EndWith("/Hauskoordinaten%20ohne%20Angaben.zip");
    }

    [Fact]
    public void DownloadCenter_NoZipListed_IsNothingToImport()
    {
        var act = () => Parse(Listing(("Beschreibung", "30.06.2026", "PDF", "/downloadcenter/20260924/x/Beschreibung.pdf")));

        act.Should().Throw<InspireImportException>().WithMessage("*lists no ZIP file*");
    }

    [Fact]
    public async Task DownloadCenter_AsksTheListingForJson()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(new Uri(ListingUrl).ToString(), () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Listing(("Edition", "24.06.2026", "ZIP", EditionUri)), Encoding.UTF8, "application/json"),
        });

        var location = await new HessenDownloadCenterLocator(Client(handler), ListingUrl).LocateAsync(TestContext.Current.CancellationToken);

        location.Version.Should().Be("Edition|24.06.2026");
        handler.RequestDetails.Should().ContainSingle().Which.Accept.Should().Be("application/json");
    }
}
