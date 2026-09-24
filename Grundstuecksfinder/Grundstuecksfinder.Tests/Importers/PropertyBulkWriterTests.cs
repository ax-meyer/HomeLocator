using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers;

/// <summary>Staging, swap, regression guard and per-source locking of the shared writer.</summary>
[Collection("Postgres")]
public sealed class PropertyBulkWriterTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<ImportRunContext> NewRunAsync(string fingerprint)
    {
        await using var context = fixture.CreateContext();
        var run = new ImportRun { Source = "a", Fingerprint = fingerprint, Reason = ImportReason.Initial, StartedAt = ImportSeed.Epoch };
        context.ImportRuns.Add(run);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new ImportRunContext(run.Id, "a");
    }

    private static async IAsyncEnumerable<Property> Rows(int count, Action? afterLast = null)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new Property { Str = $"Straße {i}", Hnr = "1", FlaecheAmtl = 100, Source = "a" };
        }
        afterLast?.Invoke();
    }

    private async Task<(int Rows, ImportRun Run, SourceState? State)> StateAsync(int runId)
    {
        await using var context = fixture.CreateContext();
        var rows = await context.Properties.CountAsync(p => p.Source == "a", TestContext.Current.CancellationToken);
        var run = await context.ImportRuns.SingleAsync(r => r.Id == runId, TestContext.Current.CancellationToken);
        var state = await context.SourceStates.SingleOrDefaultAsync(s => s.Source == "a", TestContext.Current.CancellationToken);
        return (rows, run, state);
    }

    [Fact]
    public async Task WriteAsync_SwapsRowsInAndCompletesAndServesTheRun()
    {
        var run = await NewRunAsync("v1");
        var time = new FakeTimeProvider(ImportSeed.Day(3));

        var count = await new PropertyBulkWriter(fixture.DataSource, batchSize: 3, timeProvider: time)
            .WriteAsync(run, Rows(10), TestContext.Current.CancellationToken);

        count.Should().Be(10);
        var (rows, stored, state) = await StateAsync(run.RunId);
        rows.Should().Be(10);
        stored.RecordCount.Should().Be(10);
        stored.CompletedAt.Should().Be(ImportSeed.Day(3), "completion is recorded in the swap transaction");
        state.Should().NotBeNull();
        state!.ServedRunId.Should().Be(run.RunId);
        state.LastCheckedAt.Should().Be(stored.CompletedAt, "freshly imported data is current as of its completion");
    }

    [Fact]
    public async Task WriteAsync_SkippedPartsReportedByTheFetch_AreRecordedOnTheRun()
    {
        var run = await NewRunAsync("v1");

        // Reported only once the last row has been read, as INSPIRE tiles are.
        await new PropertyBulkWriter(fixture.DataSource)
            .WriteAsync(run, Rows(3, afterLast: () => run.AddSkippedParts(2)), TestContext.Current.CancellationToken);

        (await StateAsync(run.RunId)).Run.SkippedParts.Should().Be(2);
    }

    [Fact]
    public async Task WriteAsync_FingerprintReportedByTheFetch_IsRecordedInsteadOfTheProbes()
    {
        var run = await NewRunAsync("probed");
        run.ReportFingerprint("imported");

        await new PropertyBulkWriter(fixture.DataSource).WriteAsync(run, Rows(1), TestContext.Current.CancellationToken);

        (await StateAsync(run.RunId)).Run.Fingerprint.Should().Be("imported");
    }

    [Fact]
    public async Task WriteAsync_NothingReported_KeepsTheProbesFingerprint()
    {
        var run = await NewRunAsync("probed");

        await new PropertyBulkWriter(fixture.DataSource).WriteAsync(run, Rows(1), TestContext.Current.CancellationToken);

        (await StateAsync(run.RunId)).Run.Fingerprint.Should().Be("probed");
    }

    [Fact]
    public async Task WriteAsync_NewerImport_ReplacesTheServedRun()
    {
        var writer = new PropertyBulkWriter(fixture.DataSource);
        await writer.WriteAsync(await NewRunAsync("v1"), Rows(5), TestContext.Current.CancellationToken);
        var second = await NewRunAsync("v2");

        await writer.WriteAsync(second, Rows(5), TestContext.Current.CancellationToken);

        var (rows, _, state) = await StateAsync(second.RunId);
        rows.Should().Be(5);
        state!.ServedRunId.Should().Be(second.RunId);
    }

    [Fact]
    public async Task WriteAsync_ImportMuchSmallerThanCurrentData_IsRejectedAndKeepsTheOldRows()
    {
        var writer = new PropertyBulkWriter(fixture.DataSource);
        var first = await NewRunAsync("v1");
        await writer.WriteAsync(first, Rows(100), TestContext.Current.CancellationToken);
        var shrunk = await NewRunAsync("v2");

        var act = () => writer.WriteAsync(shrunk, Rows(90), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ImportRegressionException>();
        var (rows, run, state) = await StateAsync(shrunk.RunId);
        rows.Should().Be(100, "losing 10% of a source is far more likely a broken import than real change");
        run.CompletedAt.Should().BeNull();
        state!.ServedRunId.Should().Be(first.RunId);
    }

    [Fact]
    public async Task WriteAsync_LowerRetainedRatio_AcceptsADeliberateDrop()
    {
        await new PropertyBulkWriter(fixture.DataSource)
            .WriteAsync(await NewRunAsync("v1"), Rows(100), TestContext.Current.CancellationToken);

        var count = await new PropertyBulkWriter(fixture.DataSource, minRetainedRatio: 0)
            .WriteAsync(await NewRunAsync("v2"), Rows(90), TestContext.Current.CancellationToken);

        count.Should().Be(90);
    }

    [Fact]
    public async Task WriteAsync_SameSourceAlreadyImporting_IsRefused()
    {
        await using var otherProcess = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext('property-import'), hashtext('a'))", otherProcess))
        {
            await lockCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var act = async () => await new PropertyBulkWriter(fixture.DataSource)
                .WriteAsync(await NewRunAsync("v1"), Rows(1), TestContext.Current.CancellationToken);

            await act.Should().ThrowAsync<ImportAlreadyRunningException>().WithMessage("*already running*");
        }
        finally
        {
            // Pooled connections keep session locks; release it so later tests can import "a".
            await using var unlock = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('property-import'), hashtext('a'))", otherProcess);
            await unlock.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WriteAsync_ReleasesItsLock_SoTheNextImportOfTheSourceCanRun()
    {
        var writer = new PropertyBulkWriter(fixture.DataSource);
        await writer.WriteAsync(await NewRunAsync("v1"), Rows(5), TestContext.Current.CancellationToken);

        var act = async () => await writer.WriteAsync(await NewRunAsync("v2"), Rows(5), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
