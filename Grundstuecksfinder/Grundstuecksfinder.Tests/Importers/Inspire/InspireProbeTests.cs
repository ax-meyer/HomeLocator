using FluentAssertions;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Grundstuecksfinder.Tests.TestHelpers;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

public sealed class InspireProbeTests
{
    [Fact]
    public void Combine_JoinsThePartsParcelsFirst() =>
        InspireProbe.Combine(new FingerprintPart("123", false), new FingerprintPart("456", false))
            .Fingerprint.Should().Be($"{InspireProbe.Revision}:123:456");

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
    public void ParcelsOnly_IsTheParcelPartAlone()
    {
        var parcels = new FingerprintPart("6208246", false);

        var probe = InspireProbe.ParcelsOnly(parcels);

        probe.Fingerprint.Should().Be($"{InspireProbe.Revision}:6208246");
        probe.Kind.Should().Be(FingerprintKind.Approximate);
        probe.Parcels.Should().Be(parcels);
        probe.Addresses.Should().BeNull();
    }

    [Fact]
    public void Revision_MakesRowsImportedBeforeItDue()
    {
        // Served before r2: the same upstream version, but rows without coordinates.
        var probe = InspireProbe.Combine(new FingerprintPart("\"a\"", true), new FingerprintPart("\"b\"", true));
        var served = new ServedImport("\"a\":\"b\"", ImportSeed.Day(0));

        var plan = RefreshPlanner.Plan(
            [new SourceStatus("be", RefreshPolicy.Default, probe, served)], ImportSeed.Day(1), maxRoutineImportsPerRun: 1);

        plan.Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.Changed);
    }

    [Fact]
    public void Revision_IsStable_SoAnUnchangedSourceStaysUpToDate()
    {
        var probe = InspireProbe.Combine(new FingerprintPart("\"a\"", true), new FingerprintPart("\"b\"", true));
        var served = new ServedImport(probe.Fingerprint, ImportSeed.Day(0));

        var plan = RefreshPlanner.Plan(
            [new SourceStatus("be", RefreshPolicy.Default, probe, served)], ImportSeed.Day(1), maxRoutineImportsPerRun: 1);

        plan.UpToDate.Should().Equal("be");
    }

    [Fact]
    public void ParcelsOnly_ExactPart_IsExact() =>
        InspireProbe.ParcelsOnly(new FingerprintPart("\"etag\"", true)).Kind.Should().Be(FingerprintKind.Exact);

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
