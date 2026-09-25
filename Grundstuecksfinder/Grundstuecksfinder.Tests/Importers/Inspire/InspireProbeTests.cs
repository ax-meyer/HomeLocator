using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public sealed class InspireProbeTests
{
    [Fact]
    public void Combine_JoinsThePartsParcelsFirst() =>
        InspireProbe.Combine(new FingerprintPart("123", false), new FingerprintPart("456", false))
            .Fingerprint.Should().Be("123:456");

    [Fact]
    public void Combine_KeepsBothPartsForTheFetch()
    {
        var parcels = new FingerprintPart("123", false);
        var addresses = new FingerprintPart("\"etag\"", true);

        var probe = InspireProbe.Combine(parcels, addresses);

        probe.Parcels.Should().Be(parcels);
        probe.Addresses.Should().Be(addresses);
    }

    [Fact]
    public void Combine_AllPartsExact_IsExact() =>
        InspireProbe.Combine(new FingerprintPart("a", true), new FingerprintPart("b", true))
            .Kind.Should().Be(FingerprintKind.Exact);

    [Fact]
    public void Combine_OneApproximatePart_MakesTheWholeApproximate() =>
        // Hit counts drift without the data changing, so a composite can't claim more precision
        // than its weakest part.
        InspireProbe.Combine(new FingerprintPart("2890412", false), new FingerprintPart("\"etag\"", true))
            .Kind.Should().Be(FingerprintKind.Approximate);

    [Fact]
    public void FromCount_IsApproximate() =>
        FingerprintPart.FromCount(42, "parcel").Should().Be(new FingerprintPart("42", false));

    [Theory]
    [InlineData(null, "*doesn't report a feature count*")]
    [InlineData(0L, "*zero features*")]
    public void FromCount_NothingToImport_FailsTheProbe(long? count, string message)
    {
        var act = () => FingerprintPart.FromCount(count, "address");

        act.Should().Throw<InspireImportException>().WithMessage(message);
    }
}
