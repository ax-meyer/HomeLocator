using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Grundstuecksfinder.Tests.TestHelpers;

public static class ImportRunnerExtensions
{
    /// <summary>
    /// One import run over <paramref name="sources"/> against the fixture's database, wired like
    /// the app does it; the clock drives both the planner and the writer's completion times.
    /// </summary>
    public static async Task RunImportsAsync(this PostgresFixture fixture, IEnumerable<IPropertySource> sources,
        TimeProvider? time = null, RefreshOptions? options = null, int writerBatchSize = PropertyBulkWriter.DefaultBatchSize,
        CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        await using var context = fixture.CreateContext();
        var runner = new ImportRunner(
            sources,
            new ImportStateStore(context),
            new PropertyBulkWriter(fixture.DataSource, batchSize: writerBatchSize, timeProvider: time),
            options ?? new RefreshOptions(),
            time,
            NullLogger<ImportRunner>.Instance);
        await runner.RunAsync(ct);
    }
}
