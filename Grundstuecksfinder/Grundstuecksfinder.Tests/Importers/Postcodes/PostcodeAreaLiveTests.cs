using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Postcodes;

/// <summary>
/// Downloads the real postcode area file (~25 MB) and checks it against the few BB addresses
/// whose PLZ the state does publish (points in EPSG:25833, from the BB address WFS).
/// </summary>
[Trait("Category", "Live")]
public sealed class PostcodeAreaLiveTests
{
    private static readonly Envelope Brandenburg = new(240000, 500000, 5680000, 5950000);

    [Theory]
    [InlineData(284240.337, 5879357.345, "19322")] // Heinrich-Heine-Straße 19, Weisen
    [InlineData(296710.016, 5875337.709, "19336")] // Sigrön 20, Bad Wilsnack
    [InlineData(303804.718, 5872347.613, "19339")] // Altes Dorf 11a, Plattenburg
    public async Task LoadAsync_RealFile_FindsThePublishedPlz(double x, double y, string expected)
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient());
        var provider = new PostcodeAreaProvider(factory, new PostcodeAreaOptions(), NullLogger<PostcodeAreaProvider>.Instance);

        var lookup = await provider.LoadAsync(25833, Brandenburg, TestContext.Current.CancellationToken);

        lookup.FindPostcode(new Point(x, y)).Should().Be(expected);
    }
}
