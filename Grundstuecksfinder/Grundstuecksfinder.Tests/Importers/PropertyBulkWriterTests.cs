using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers;

/// <summary>Staging, swap, regression guard and per-source locking of the shared writer.</summary>
[Collection("Postgres")]
public sealed class PropertyBulkWriterTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<int> NewImportLogAsync(string version)
    {
        await using var context = fixture.CreateContext();
        var log = new ImportLog { Source = "a", DatasetName = "ds", FileName = "f", FileTimestamp = version, ImportedAt = 1 };
        context.ImportLogs.Add(log);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return log.Id;
    }

    private static async IAsyncEnumerable<Property> Rows(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new Property { Str = $"Straße {i}", Hnr = "1", FlaecheAmtl = 100, Source = "a" };
        }
    }

    private async Task<(int Rows, ImportLog Log)> StateAsync(int importLogId)
    {
        await using var context = fixture.CreateContext();
        var rows = await context.Properties.CountAsync(p => p.Source == "a", TestContext.Current.CancellationToken);
        var log = await context.ImportLogs.SingleAsync(l => l.Id == importLogId, TestContext.Current.CancellationToken);
        return (rows, log);
    }

    [Fact]
    public async Task WriteAsync_SwapsRowsInAndCompletesTheImportLog()
    {
        var logId = await NewImportLogAsync("v1");

        var count = await new PropertyBulkWriter(fixture.DataSource, batchSize: 3)
            .WriteAsync("a", Rows(10), logId, TestContext.Current.CancellationToken);

        count.Should().Be(10);
        var (rows, log) = await StateAsync(logId);
        rows.Should().Be(10);
        log.RecordCount.Should().Be(10);
        log.CompletedAt.Should().NotBeNull("completion is recorded in the swap transaction");
    }

    [Fact]
    public async Task WriteAsync_ImportMuchSmallerThanCurrentData_IsRejectedAndKeepsTheOldRows()
    {
        var writer = new PropertyBulkWriter(fixture.DataSource);
        await writer.WriteAsync("a", Rows(100), await NewImportLogAsync("v1"), TestContext.Current.CancellationToken);
        var shrunkLogId = await NewImportLogAsync("v2");

        var act = () => writer.WriteAsync("a", Rows(90), shrunkLogId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ImportRegressionException>();
        var (rows, log) = await StateAsync(shrunkLogId);
        rows.Should().Be(100, "losing 10% of a source is far more likely a broken import than real change");
        log.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_LowerRetainedRatio_AcceptsADeliberateDrop()
    {
        await new PropertyBulkWriter(fixture.DataSource)
            .WriteAsync("a", Rows(100), await NewImportLogAsync("v1"), TestContext.Current.CancellationToken);

        var count = await new PropertyBulkWriter(fixture.DataSource, minRetainedRatio: 0)
            .WriteAsync("a", Rows(90), await NewImportLogAsync("v2"), TestContext.Current.CancellationToken);

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
                .WriteAsync("a", Rows(1), await NewImportLogAsync("v1"), TestContext.Current.CancellationToken);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already running*");
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
        await writer.WriteAsync("a", Rows(5), await NewImportLogAsync("v1"), TestContext.Current.CancellationToken);

        var act = async () => await writer.WriteAsync("a", Rows(5), await NewImportLogAsync("v2"), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
