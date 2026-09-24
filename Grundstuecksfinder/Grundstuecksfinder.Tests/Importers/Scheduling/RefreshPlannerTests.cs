using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Scheduling;

public sealed class RefreshPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
    private static readonly RefreshPolicy Policy = new(TimeSpan.FromDays(30), TimeSpan.FromDays(90));

    private static SourceProbe Exact(string fingerprint) => new(fingerprint, FingerprintKind.Exact);
    private static SourceProbe Approximate(string fingerprint) => new(fingerprint, FingerprintKind.Approximate);

    private static SourceStatus Served(string source, SourceProbe probe, string servedFingerprint, double ageDays,
        bool lastAttemptFailed = false) =>
        new(source, Policy, probe, new ServedImport(servedFingerprint, Now - TimeSpan.FromDays(ageDays)), lastAttemptFailed);

    private static SourceStatus NeverServed(string source, SourceProbe? probe) => new(source, Policy, probe, Served: null);

    private static RefreshPlan Plan(int maxRoutine, params SourceStatus[] sources) =>
        RefreshPlanner.Plan(sources, Now, maxRoutine);

    [Fact]
    public void Plan_NoServedData_ImportsEverySourceBackToBackDespiteTheRoutineCap()
    {
        var plan = Plan(1,
            NeverServed("bw", Approximate("1:1")),
            NeverServed("nrw", Exact("v1")),
            NeverServed("sh", Approximate("2:2")));

        plan.Imports.Select(i => (i.Source, i.Reason)).Should().Equal(
            ("bw", ImportReason.Initial), ("nrw", ImportReason.Initial), ("sh", ImportReason.Initial));
        plan.Deferred.Should().BeEmpty();
    }

    [Fact]
    public void Plan_InitialImports_ComeBeforeTheRoutineOne()
    {
        var plan = Plan(1,
            Served("nrw", Exact("v2"), "v1", ageDays: 1),
            NeverServed("sh", Approximate("2:2")));

        plan.Imports.Select(i => i.Source).Should().Equal("sh", "nrw");
    }

    [Fact]
    public void Plan_InitialImport_CarriesTheProbeToFetch()
    {
        var probe = Exact("v1");

        Plan(1, NeverServed("nrw", probe)).Imports.Should().ContainSingle().Which.Probe.Should().BeSameAs(probe);
    }

    [Fact]
    public void Plan_ExactFingerprintChanged_IsImportedHoweverRecentTheServedData()
    {
        var plan = Plan(1, Served("nrw", Exact("v2"), "v1", ageDays: 0.1));

        plan.Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.Changed);
    }

    [Fact]
    public void Plan_ExactFingerprintUnchanged_IsUpToDateHoweverOldTheServedData()
    {
        // A publisher's version marker is trusted: an unchanged file is never downloaded again.
        var plan = Plan(1, Served("nrw", Exact("v1"), "v1", ageDays: 400));

        plan.Imports.Should().BeEmpty();
        plan.UpToDate.Should().Equal("nrw");
    }

    [Theory]
    [InlineData(29.9, false)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    public void Plan_ApproximateFingerprintChanged_IsImportedOnceTheDataReachesMinAge(double ageDays, bool imported)
    {
        var plan = Plan(1, Served("bw", Approximate("101:99"), "100:99", ageDays));

        if (imported)
            plan.Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.Changed);
        else
            plan.UpToDate.Should().Equal(["bw"], "hit counts drift daily; a young import isn't redone for that");
    }

    [Theory]
    [InlineData(89.9, false)]
    [InlineData(90, true)]
    public void Plan_ApproximateFingerprintUnchanged_IsImportedOnceTheDataReachesMaxAge(double ageDays, bool imported)
    {
        var plan = Plan(1, Served("bw", Approximate("100:99"), "100:99", ageDays));

        if (imported)
            plan.Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.MaxAge,
                "unchanged counts can hide renamings and corrected areas");
        else
            plan.UpToDate.Should().Equal("bw");
    }

    [Fact]
    public void Plan_PerSourcePolicy_IsHonoured()
    {
        var weekly = new RefreshPolicy(TimeSpan.Zero, TimeSpan.FromDays(7));
        var status = new SourceStatus("hh", weekly, Approximate("1:1"), new ServedImport("1:1", Now - TimeSpan.FromDays(8)));

        Plan(1, status).Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.MaxAge);
    }

    [Fact]
    public void Plan_RoutineImports_AreCappedAndTakeTheMostOverdueFirst()
    {
        var plan = Plan(1,
            Served("sh", Approximate("2"), "1", ageDays: 40),    // changed: due for 10 days
            Served("bw", Approximate("1"), "1", ageDays: 120),   // max age: due for 30 days
            Served("ni", Approximate("2"), "1", ageDays: 35));   // changed: due for 5 days

        plan.Imports.Select(i => i.Source).Should().Equal("bw");
        plan.Deferred.Select(i => i.Source).Should().Equal(["sh", "ni"], "the rest wait for later nights, most overdue first");
        plan.UpToDate.Should().BeEmpty("deferred sources are due, not current");
    }

    [Fact]
    public void Plan_HigherCap_RunsThatManyRoutineImports()
    {
        var plan = Plan(2,
            Served("sh", Approximate("2"), "1", ageDays: 40),
            Served("bw", Approximate("1"), "1", ageDays: 120),
            Served("ni", Approximate("2"), "1", ageDays: 35));

        plan.Imports.Select(i => i.Source).Should().Equal("bw", "sh");
        plan.Deferred.Select(i => i.Source).Should().Equal("ni");
    }

    [Fact]
    public void Plan_CapOfZero_PausesRoutineImportsButNotInitialOnes()
    {
        var plan = Plan(0, Served("bw", Approximate("1"), "1", ageDays: 120), NeverServed("sh", Approximate("1")));

        plan.Imports.Select(i => i.Source).Should().Equal("sh");
        plan.Deferred.Select(i => i.Source).Should().Equal("bw");
    }

    [Fact]
    public void Plan_ProbeFailed_SkipsTheSourceWhetherServedOrNot()
    {
        var plan = Plan(5,
            new SourceStatus("bw", Policy, Probe: null, new ServedImport("1", Now - TimeSpan.FromDays(200))),
            NeverServed("sh", probe: null),
            NeverServed("sn", Approximate("1")));

        plan.Imports.Select(i => i.Source).Should().Equal("sn");
        plan.Unprobed.Should().Equal("bw", "sh");
        plan.UpToDate.Should().BeEmpty("nothing confirmed the unprobed sources' data");
    }

    [Fact]
    public void Plan_FailedRoutineImport_IsDueAgainNextRunAsARetry()
    {
        // The served data didn't change when the import failed, so the same rule still holds.
        var plan = Plan(1, Served("nrw", Exact("v2"), "v1", ageDays: 1, lastAttemptFailed: true));

        plan.Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.Retry);
    }

    [Fact]
    public void Plan_FailedInitialImport_IsRetriedAsInitial()
    {
        // Never served, so it doesn't compete for the routine slot.
        var status = new SourceStatus("he", Policy, Approximate("1"), Served: null, LastAttemptFailed: true);

        Plan(0, status).Imports.Should().ContainSingle().Which.Reason.Should().Be(ImportReason.Initial);
    }

    [Fact]
    public void Plan_SourceThatKeepsFailing_DoesNotStarveTheOthers()
    {
        var plan = Plan(1,
            Served("bw", Approximate("1"), "1", ageDays: 300, lastAttemptFailed: true),
            Served("sh", Approximate("2"), "1", ageDays: 31));

        plan.Imports.Select(i => i.Source).Should().Equal("sh");
        plan.Deferred.Select(i => i.Source).Should().Equal("bw");
    }
}
