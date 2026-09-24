using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Scheduling;

/// <summary>
/// The runner end to end against Postgres: what gets probed, imported and recorded; that one
/// source failing never stops the others and never touches their rows; and that the planner's
/// rules hold across runs on a fake clock.
/// </summary>
[Collection("Postgres")]
public sealed class ImportRunnerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(T0);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task RunAsync(params IPropertySource[] sources) => fixture.RunImportsAsync(sources, _time, ct: Ct);

    private void AdvanceDays(double days) => _time.Advance(TimeSpan.FromDays(days));

    private async Task<List<string?>> StreetsAsync(string source)
    {
        await using var context = fixture.CreateContext();
        return await context.Properties.Where(p => p.Source == source).Select(p => p.Str).ToListAsync(Ct);
    }

    private async Task<List<ImportRun>> RunsAsync(string source)
    {
        await using var context = fixture.CreateContext();
        return await context.ImportRuns.Where(r => r.Source == source).OrderBy(r => r.Id).ToListAsync(Ct);
    }

    private async Task<SourceState> StateAsync(string source)
    {
        await using var context = fixture.CreateContext();
        return await context.SourceStates.Include(s => s.ServedRun).SingleAsync(s => s.Source == source, Ct);
    }

    [Fact]
    public async Task FirstRun_ImportsEverySourceBackToBack_DespiteTheRoutineCap()
    {
        var sources = new[] { new StubPropertySource("a", "a1"), new StubPropertySource("b", "b1"), new StubPropertySource("c", "c1") };

        await RunAsync(sources);

        foreach (var source in sources)
        {
            (await StreetsAsync(source.Id)).Should().ContainSingle();
            var run = (await RunsAsync(source.Id)).Should().ContainSingle().Subject;
            run.Reason.Should().Be(ImportReason.Initial);
            run.Fingerprint.Should().Be(source.Fingerprint);
            run.StartedAt.Should().Be(T0);
            (await StateAsync(source.Id)).ServedRunId.Should().Be(run.Id);
        }
    }

    [Fact]
    public async Task ReimportingOneSource_DoesNotTouchTheOtherSourcesRows()
    {
        var a = new StubPropertySource("a", "a1", street: "Alte Straße");
        var b = new StubPropertySource("b", "b1");
        await RunAsync(a, b);

        a.Fingerprint = "a2";
        a.Street = "Neue Straße";
        AdvanceDays(1);
        await RunAsync(a, b);

        (await StreetsAsync("a")).Should().Equal("Neue Straße");
        (await StreetsAsync("b")).Should().ContainSingle("source b's rows must survive source a's re-import");
        b.Fetches.Should().Be(1);
    }

    [Fact]
    public async Task ExactFingerprintUnchanged_IsNotFetchedAgainButConfirmedCurrent()
    {
        var source = new StubPropertySource("nrw", "v1");
        await RunAsync(source);

        AdvanceDays(200);
        await RunAsync(source);

        source.Fetches.Should().Be(1, "an unchanged file is never downloaded again");
        (await RunsAsync("nrw")).Should().ContainSingle();
        var state = await StateAsync("nrw");
        state.LastCheckedAt.Should().Be(T0.AddDays(200), "the home page's Stand moves on with every confirmation");
        state.LastProbeAt.Should().Be(T0.AddDays(200));
        state.LastProbeFingerprint.Should().Be("v1");
    }

    [Fact]
    public async Task ApproximateChange_WaitsUntilTheDataIsMinAgeOld()
    {
        var source = new StubPropertySource("bw", "100:100", FingerprintKind.Approximate);
        await RunAsync(source);

        source.Fingerprint = "101:100";
        AdvanceDays(10);
        await RunAsync(source);

        source.Fetches.Should().Be(1, "hit counts drift daily; a 10-day-old import isn't redone for that");
        (await StateAsync("bw")).LastCheckedAt.Should().Be(T0.AddDays(10), "the data is within its policy");

        AdvanceDays(20);
        await RunAsync(source);

        source.Fetches.Should().Be(2);
        (await RunsAsync("bw")).Last().Reason.Should().Be(ImportReason.Changed);
    }

    [Fact]
    public async Task ApproximateUnchanged_IsReimportedAtMaxAge()
    {
        var source = new StubPropertySource("bw", "100:100", FingerprintKind.Approximate);
        await RunAsync(source);

        AdvanceDays(90);
        await RunAsync(source);

        source.Fetches.Should().Be(2);
        (await RunsAsync("bw")).Last().Reason.Should().Be(ImportReason.MaxAge);
    }

    [Fact]
    public async Task DataAge_CountsFromTheStartOfItsImport()
    {
        // A long import completes a day after it started; its data is as old as the start.
        var source = new StubPropertySource("bw", "1", FingerprintKind.Approximate) { DuringFetch = () => AdvanceDays(1) };
        await RunAsync(source);
        source.DuringFetch = null;

        AdvanceDays(89); // 90 days after the fetch started, 89 after it completed
        await RunAsync(source);

        source.Fetches.Should().Be(2);
        (await RunsAsync("bw")).Last().Reason.Should().Be(ImportReason.MaxAge);
    }

    [Fact]
    public async Task RoutineImports_OnePerRunMostOverdueFirst_TheRestOnLaterRuns()
    {
        var a = new StubPropertySource("a", "1", FingerprintKind.Approximate) { RefreshPolicy = new(TimeSpan.Zero, TimeSpan.FromDays(10)) };
        var b = new StubPropertySource("b", "1", FingerprintKind.Approximate) { RefreshPolicy = new(TimeSpan.Zero, TimeSpan.FromDays(20)) };
        await RunAsync(a, b);

        AdvanceDays(30); // a is due for 20 days, b for 10
        await RunAsync(a, b);

        (a.Fetches, b.Fetches).Should().Be((2, 1));
        (await StateAsync("b")).LastCheckedAt.Should().Be(T0, "a deferred source is due, not confirmed current");

        AdvanceDays(1);
        await RunAsync(a, b);

        (a.Fetches, b.Fetches).Should().Be((2, 2), "a is fresh now, so b gets the slot");
    }

    [Fact]
    public async Task StartupRun_OnlyImportsSourcesWithoutData()
    {
        // However often the app restarts, routine re-imports stay within the nightly cap.
        var due = new StubPropertySource("bw", "1", FingerprintKind.Approximate);
        await RunAsync(due);
        AdvanceDays(100);
        var fresh = new StubPropertySource("sh", "1", FingerprintKind.Approximate);

        await fixture.RunImportsAsync([due, fresh], _time, kind: ImportRunKind.Startup, ct: Ct);

        (due.Fetches, fresh.Fetches).Should().Be((1, 1));
        (await StateAsync("bw")).LastCheckedAt.Should().Be(T0, "it is due, not confirmed current");

        await RunAsync(due, fresh);

        due.Fetches.Should().Be(2, "the nightly run does the routine re-import");
    }

    [Fact]
    public async Task ProbeFails_TheOtherSourcesStillRunAndTheErrorIsRecorded()
    {
        var broken = new StubPropertySource("broken", "x") { ProbeFailure = new HttpRequestException("503 from the WFS") };
        var working = new StubPropertySource("ok", "v1");

        await RunAsync(broken, working);

        (await StreetsAsync("ok")).Should().ContainSingle();
        broken.Fetches.Should().Be(0);
        (await RunsAsync("broken")).Should().BeEmpty();
        var state = await StateAsync("broken");
        state.LastProbeError.Should().Be("503 from the WFS");
        state.ServedRunId.Should().BeNull();
    }

    [Fact]
    public async Task ProbeTimesOut_IsTreatedAsThatSourcesFailure()
    {
        // HttpClient reports a timeout as TaskCanceledException, an OperationCanceledException.
        // Unless the run itself was cancelled, it must not escape and stop the host.
        var timingOut = new StubPropertySource("slow", "x") { ProbeFailure = new TaskCanceledException("HttpClient timeout") };
        var working = new StubPropertySource("ok", "v1");

        var act = () => RunAsync(timingOut, working);

        await act.Should().NotThrowAsync();
        (await StreetsAsync("ok")).Should().ContainSingle();
    }

    [Fact]
    public async Task ProbeFailsAfterASuccess_KeepsTheServedDataAndTheLastFingerprint()
    {
        var source = new StubPropertySource("sh", "1:1", FingerprintKind.Approximate);
        await RunAsync(source);

        source.ProbeFailure = new HttpRequestException("down");
        AdvanceDays(100);
        await RunAsync(source);

        var state = await StateAsync("sh");
        state.LastProbeFingerprint.Should().Be("1:1");
        state.LastProbeError.Should().Be("down");
        state.LastCheckedAt.Should().Be(T0, "nothing confirmed the data this run");
        (await StreetsAsync("sh")).Should().ContainSingle();

        source.ProbeFailure = null;
        AdvanceDays(1);
        await RunAsync(source);

        (await StateAsync("sh")).LastProbeError.Should().BeNull();
    }

    [Fact]
    public async Task AnotherInstanceRunning_SkipsTheRunWithoutRecordingAnything()
    {
        var source = new StubPropertySource("a", "v1");
        await using var otherInstance = await AdvisoryLock.TryAcquireAsync(
            fixture.DataSource, ImportRunner.RunLockScope, ImportRunner.RunLockKey, logger: null, Ct);

        await RunAsync(source);

        source.Probes.Should().Be(0);
        await using var context = fixture.CreateContext();
        (await context.ImportRuns.AnyAsync(Ct)).Should().BeFalse();
        (await context.SourceStates.AnyAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task SourceLockedByAnotherImport_IsNotRecordedAsAFailedRun()
    {
        var locked = new StubPropertySource("a", "v1");
        var other = new StubPropertySource("b", "v1");
        await using (await AdvisoryLock.TryAcquireAsync(fixture.DataSource, PropertyBulkWriter.SourceLockScope, "a", logger: null, Ct))
            await RunAsync(locked, other);

        (await RunsAsync("a")).Should().BeEmpty("nothing was attempted, so nothing failed");
        (await StreetsAsync("b")).Should().ContainSingle();

        AdvanceDays(1);
        await RunAsync(locked, other);

        (await RunsAsync("a")).Should().ContainSingle().Which.Reason.Should().Be(ImportReason.Initial);
    }

    [Fact]
    public async Task RunCutShortByACrash_IsRecordedAsFailedAndRetriedAfterTheOthers()
    {
        await using (var context = fixture.CreateContext())
        {
            context.ImportRuns.Add(ImportSeed.Unfinished("bw", T0.AddDays(-1)));
            await context.SaveChangesAsync(Ct);
        }
        var order = new List<string>();
        StubPropertySource Recording(string id) => new(id, "1", FingerprintKind.Approximate) { DuringFetch = () => order.Add(id) };

        await RunAsync(Recording("bw"), Recording("sh"), Recording("sn"));

        order.Should().Equal(["sh", "sn", "bw"], "a source that may have taken the process down goes last");
        var interrupted = (await RunsAsync("bw")).First();
        interrupted.FailedAt.Should().Be(T0);
        interrupted.Error.Should().Be(ImportStateStore.InterruptedError);
    }

    [Fact]
    public async Task RunCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => fixture.RunImportsAsync([new StubPropertySource("a", "v1")], _time, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchFailsMidway_KeepsThePreviousRowsAndIsRetriedNextRun()
    {
        var source = new StubPropertySource("a", "v1", street: "Alt");
        await RunAsync(source);

        source.Fingerprint = "v2";
        source.Street = "Neu";
        source.RowCount = 2;
        source.FailAfter = 1;
        AdvanceDays(1);
        await RunAsync(source);

        (await StreetsAsync("a")).Should().Equal(["Alt"], "a failed import must not replace the previous data");
        var failed = (await RunsAsync("a")).Last();
        failed.FailedAt.Should().Be(T0.AddDays(1));
        failed.CompletedAt.Should().BeNull();
        failed.Error.Should().Be("upstream failed midway");
        (await StateAsync("a")).ServedRun!.Fingerprint.Should().Be("v1");

        source.FailAfter = null;
        AdvanceDays(1);
        await RunAsync(source);

        (await StreetsAsync("a")).Should().Equal(["Neu", "Neu"]);
        var retried = (await RunsAsync("a")).Last();
        retried.Reason.Should().Be(ImportReason.Retry);
        retried.CompletedAt.Should().Be(T0.AddDays(2));
        (await StateAsync("a")).ServedRunId.Should().Be(retried.Id);
    }

    [Fact]
    public async Task SkippedPartsReportedByTheSource_AreRecordedOnTheServedRun()
    {
        await RunAsync(new StubPropertySource("bw", "v1") { SkippedParts = 2 });

        (await StateAsync("bw")).ServedRun!.SkippedParts.Should().Be(2);
    }

    [Fact]
    public async Task FetchGetsTheProbeOfTheSameRun()
    {
        var source = new StubPropertySource("nrw", "file-2026.zip");

        await RunAsync(source);

        source.FetchedProbes.Should().ContainSingle().Which.Fingerprint.Should().Be("file-2026.zip");
    }
}
