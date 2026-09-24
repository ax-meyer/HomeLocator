using FluentAssertions;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Scheduling;

public sealed class RefreshOptionsTests
{
    private static readonly Dictionary<string, RefreshOverride?> NoOverrides = new() { ["nrw"] = null, ["bw"] = null };

    [Fact]
    public void Defaults_AreMonthlyToQuarterlyWithOneRoutineImportPerRun()
    {
        var options = new RefreshOptions();

        options.PolicyFor(null).Should().Be(new RefreshPolicy(TimeSpan.FromDays(30), TimeSpan.FromDays(90)));
        options.MaxRoutineImportsPerRun.Should().Be(1);
        options.Validate(NoOverrides).Should().BeEmpty();
    }

    [Fact]
    public void PolicyFor_Override_ReplacesOnlyTheValuesItSets()
    {
        var policy = new RefreshOptions().PolicyFor(new RefreshOverride { MaxAgeDays = 60 });

        policy.Should().Be(new RefreshPolicy(TimeSpan.FromDays(30), TimeSpan.FromDays(60)));
    }

    [Fact]
    public void Validate_BrokenDefaults_AreRejected()
    {
        var options = new RefreshOptions { MinAgeDays = 40, MaxAgeDays = 20, MaxRoutineImportsPerRun = -1 };

        options.Validate(NoOverrides).Should().BeEquivalentTo(
            "Import:Refresh: MaxRoutineImportsPerRun must not be negative.",
            "Import:Refresh: MaxAgeDays (20) must be at least MinAgeDays (40).");
    }

    [Fact]
    public void Validate_OverrideConflictingWithTheDefaults_NamesTheSource()
    {
        // Each value is fine on its own; only the effective policy (MinAge 30 from the defaults) is broken.
        var overrides = new Dictionary<string, RefreshOverride?> { ["bw"] = new() { MaxAgeDays = 10 } };

        new RefreshOptions().Validate(overrides).Should().Equal("bw: Refresh: MaxAgeDays (10) must be at least MinAgeDays (30).");
    }

    [Fact]
    public void Validate_NegativeMinAge_IsRejected()
    {
        var overrides = new Dictionary<string, RefreshOverride?> { ["bw"] = new() { MinAgeDays = -1 } };

        new RefreshOptions().Validate(overrides).Should().Equal("bw: Refresh: MinAgeDays must not be negative.");
    }
}
