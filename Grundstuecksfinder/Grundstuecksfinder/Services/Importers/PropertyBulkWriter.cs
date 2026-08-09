using Grundstuecksfinder.Models;
using Npgsql;
using NpgsqlTypes;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// Shared bulk-insert path for every importer: replaces a source's existing rows and
/// streams the new ones in via Npgsql COPY BINARY, the fastest bulk-insert path into
/// PostgreSQL from .NET. Delete + COPY run in one transaction so a failed import never
/// leaves a source's data empty.
/// </summary>
public class PropertyBulkWriter(NpgsqlDataSource dataSource)
{
    public async Task<long> WriteAsync(string source, IAsyncEnumerable<Property> properties, int importLogId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var delete = new NpgsqlCommand("DELETE FROM \"Properties\" WHERE \"Source\" = $1", conn, tx))
        {
            delete.Parameters.AddWithValue(source);
            await delete.ExecuteNonQueryAsync(ct);
        }

        long count = 0;
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY \"Properties\" (\"Str\", \"Hnr\", \"HnrZus\", \"Plz\", \"Ort\", \"Gemeinde\", \"FlaecheAmtl\", \"Source\", \"ImportLogId\") FROM STDIN (FORMAT BINARY)", ct))
        {
            await foreach (var property in properties.WithCancellation(ct))
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
                await writer.WriteAsync(importLogId, NpgsqlDbType.Integer, ct);

                count++;
            }

            await writer.CompleteAsync(ct);
        }

        await tx.CommitAsync(ct);
        return count;
    }

    private static async Task WriteNullableTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value != null)
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
        else
            await writer.WriteNullAsync(ct);
    }
}
