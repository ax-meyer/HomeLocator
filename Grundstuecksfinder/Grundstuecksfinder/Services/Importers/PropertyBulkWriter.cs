using Grundstuecksfinder.Models;
using Npgsql;
using NpgsqlTypes;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Shared bulk-insert path for every importer: replaces a source's existing rows with new ones.
/// Rows stream into the unlogged PropertyStaging table via COPY BINARY in short batches, each its
/// own transaction, so a multi-hour import never holds a transaction open (which would pin
/// autovacuum's horizon for the whole database). Only once the importer has yielded every row
/// without failing are they swapped into Properties, in one short transaction — a failed or
/// cancelled import leaves the source's previous rows untouched.
/// </summary>
public class PropertyBulkWriter(NpgsqlDataSource dataSource, int batchSize = PropertyBulkWriter.DefaultBatchSize)
{
    public const int DefaultBatchSize = 50_000;

    public async Task<long> WriteAsync(string source, IAsyncEnumerable<Property> properties, int importLogId, CancellationToken ct)
    {
        // Leftovers of a crashed earlier run must not end up in this one.
        await ClearStagingAsync(source, ct);
        try
        {
            var count = await StageAsync(source, properties, ct);
            await SwapAsync(source, importLogId, ct);
            return count;
        }
        finally
        {
            await ClearStagingAsync(source, CancellationToken.None);
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

    private async Task SwapAsync(string source, int importLogId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var delete = new NpgsqlCommand("DELETE FROM \"Properties\" WHERE \"Source\" = $1", conn, tx))
        {
            delete.Parameters.AddWithValue(source);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "Properties" ("Str", "Hnr", "HnrZus", "Plz", "Ort", "Gemeinde", "FlaecheAmtl", "Source", "ImportLogId")
            SELECT "Str", "Hnr", "HnrZus", "Plz", "Ort", "Gemeinde", "FlaecheAmtl", "Source", $2
            FROM "PropertyStaging" WHERE "Source" = $1
            """, conn, tx))
        {
            // Millions of rows: allow the INSERT ... SELECT longer than Npgsql's 30 s default.
            insert.CommandTimeout = 0;
            insert.Parameters.AddWithValue(source);
            insert.Parameters.AddWithValue(importLogId);
            await insert.ExecuteNonQueryAsync(ct);
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

    private static async Task WriteNullableTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value != null)
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
        else
            await writer.WriteNullAsync(ct);
    }
}
