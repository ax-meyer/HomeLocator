using Grundstuecksfinder.Models;
using Npgsql;
using NpgsqlTypes;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Shared bulk-insert path for every importer: replaces a source's existing rows with new ones.
/// Rows stream into the unlogged PropertyStaging table via COPY BINARY in short batches, each its
/// own transaction, so a multi-hour import never holds a transaction open (which would pin
/// autovacuum's horizon for the whole database). Only once the importer has yielded every row
/// without failing are they swapped into Properties, in one short transaction that also marks
/// the <see cref="ImportLog"/> completed — a failed or cancelled import leaves the source's
/// previous rows untouched.
/// </summary>
public partial class PropertyBulkWriter(
    NpgsqlDataSource dataSource,
    ILogger<PropertyBulkWriter>? logger = null,
    double minRetainedRatio = PropertyBulkWriter.DefaultMinRetainedRatio,
    int batchSize = PropertyBulkWriter.DefaultBatchSize)
{
    public const int DefaultBatchSize = 50_000;

    /// <summary>
    /// An import may shrink a source by at most 2%. A bigger drop is far more likely lost data
    /// (a broken tile, a too-small bounding box) than real change; to accept a deliberate one,
    /// lower "Import:MinRetainedRatio" for that run.
    /// </summary>
    public const double DefaultMinRetainedRatio = 0.98;

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(5);

    public async Task<long> WriteAsync(string source, IAsyncEnumerable<Property> properties, int importLogId, CancellationToken ct)
    {
        // Two processes importing the same source (a deploy overlap, a dev machine pointed at
        // prod) would clear and fill the same staging rows. The session-level lock lives as
        // long as this connection.
        await using var lockConnection = await dataSource.OpenConnectionAsync(ct);
        if (!await TryLockSourceAsync(lockConnection, source, ct))
            throw new InvalidOperationException($"Another import of {source} is already running.");

        try
        {
            // Leftovers of a crashed earlier run must not end up in this one.
            await ClearStagingAsync(source, ct);
            try
            {
                var count = await StageAsync(source, properties, ct);
                await SwapAsync(source, importLogId, count, ct);
                return count;
            }
            finally
            {
                await TryClearStagingAsync(source);
            }
        }
        finally
        {
            await UnlockSourceAsync(lockConnection, source);
        }
    }

    private async Task<long> StageAsync(string source, IAsyncEnumerable<Property> properties, CancellationToken ct)
    {
        long count = 0;
        var batch = new List<Property>(batchSize);
        await foreach (var property in properties.WithCancellation(ct))
        {
            batch.Add(property);
            if (batch.Count < batchSize) continue;

            await CopyBatchAsync(source, batch, ct);
            count += batch.Count;
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await CopyBatchAsync(source, batch, ct);
            count += batch.Count;
        }
        return count;
    }

    private async Task CopyBatchAsync(string source, List<Property> batch, CancellationToken ct)
    {
        // A fresh pooled connection per batch: nothing stays open across the slow WFS fetches
        // in between, so idle-connection timeouts can't kill a long import.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY \"PropertyStaging\" (\"Str\", \"Hnr\", \"HnrZus\", \"Plz\", \"Ort\", \"Gemeinde\", \"FlaecheAmtl\", \"Source\") FROM STDIN (FORMAT BINARY)", ct);

        foreach (var property in batch)
        {
            await writer.StartRowAsync(ct);
            await WriteNullableTextAsync(writer, property.Str, ct);
            await WriteNullableTextAsync(writer, property.Hnr, ct);
            await WriteNullableTextAsync(writer, property.HnrZus, ct);
            await WriteNullableTextAsync(writer, property.Plz, ct);
            await WriteNullableTextAsync(writer, property.Ort, ct);
            await WriteNullableTextAsync(writer, property.Gemeinde, ct);

            if (property.FlaecheAmtl.HasValue)
                await writer.WriteAsync(property.FlaecheAmtl.Value, NpgsqlDbType.Double, ct);
            else
                await writer.WriteNullAsync(ct);

            await writer.WriteAsync(source, NpgsqlDbType.Text, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private async Task SwapAsync(string source, int importLogId, long count, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long previous;
        await using (var countCommand = new NpgsqlCommand("SELECT count(*) FROM \"Properties\" WHERE \"Source\" = $1", conn, tx))
        {
            countCommand.CommandTimeout = 0;
            countCommand.Parameters.AddWithValue(source);
            previous = (long)(await countCommand.ExecuteScalarAsync(ct))!;
        }
        if (previous > 0 && count < minRetainedRatio * previous)
            throw new ImportRegressionException(
                $"{source}: new import has {count} rows, previous {previous} (minimum {minRetainedRatio:P0} retained); keeping the previous data.");

        // Millions of rows: no command timeout (Npgsql's default is 30 s) for the bulk statements.
        await using (var delete = new NpgsqlCommand("DELETE FROM \"Properties\" WHERE \"Source\" = $1", conn, tx))
        {
            delete.CommandTimeout = 0;
            delete.Parameters.AddWithValue(source);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "Properties" ("Str", "Hnr", "HnrZus", "Plz", "Ort", "Gemeinde", "FlaecheAmtl", "Source", "ImportLogId")
            SELECT "Str", "Hnr", "HnrZus", "Plz", "Ort", "Gemeinde", "FlaecheAmtl", "Source", $2
            FROM "PropertyStaging" WHERE "Source" = $1
            """, conn, tx))
        {
            insert.CommandTimeout = 0;
            insert.Parameters.AddWithValue(source);
            insert.Parameters.AddWithValue(importLogId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        // In the same transaction, so swapped data is never logged as a failed import (which
        // would re-import it the next night) and a logged completion always has its data.
        await using (var complete = new NpgsqlCommand("""
            UPDATE "ImportLogs" SET "RecordCount" = $2, "CompletedAt" = $3, "LastError" = NULL WHERE "Id" = $1
            """, conn, tx))
        {
            complete.Parameters.AddWithValue(importLogId);
            complete.Parameters.AddWithValue(count);
            complete.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await complete.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private async Task ClearStagingAsync(string source, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var delete = new NpgsqlCommand("DELETE FROM \"PropertyStaging\" WHERE \"Source\" = $1", conn);
        delete.CommandTimeout = 0;
        delete.Parameters.AddWithValue(source);
        await delete.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Best effort and bounded: runs on the way out of success, failure and shutdown alike, so it
    /// must neither hang past the host's shutdown nor replace the original exception. Anything
    /// left behind is cleared at the start of the source's next import.
    /// </summary>
    private async Task TryClearStagingAsync(string source)
    {
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await ClearStagingAsync(source, timeout.Token);
        }
        catch (Exception ex)
        {
            if (logger is not null) LogStagingCleanupFailed(logger, ex, source);
        }
    }

    private static async Task<bool> TryLockSourceAsync(NpgsqlConnection conn, string source, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtext('property-import'), hashtext($1))", conn);
        command.Parameters.AddWithValue(source);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task UnlockSourceAsync(NpgsqlConnection conn, string source)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('property-import'), hashtext($1))", conn);
            command.Parameters.AddWithValue(source);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A pooled connection keeps session-level advisory locks when it goes back to the
            // pool; make sure this one is closed instead of reused, which releases the lock.
            NpgsqlConnection.ClearPool(conn);
            if (logger is not null) LogUnlockFailed(logger, ex, source);
        }
    }

    private static async Task WriteNullableTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value != null)
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
        else
            await writer.WriteNullAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: couldn't clear staged rows; the next import of this source clears them")]
    private static partial void LogStagingCleanupFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: couldn't release the import lock explicitly; closing its connection instead")]
    private static partial void LogUnlockFailed(ILogger logger, Exception exception, string source);
}

/// <summary>An import would shrink its source by more than the allowed ratio; not swapped in.</summary>
public sealed class ImportRegressionException(string message) : Exception(message);
