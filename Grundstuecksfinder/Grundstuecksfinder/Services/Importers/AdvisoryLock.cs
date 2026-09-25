using Npgsql;

namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// A Postgres session-level advisory lock, held on a dedicated connection for as long as this
/// object lives. Guards against a second process (a deploy overlap, a dev machine pointed at
/// prod) importing at the same time: the database is the one thing both can see.
/// </summary>
public sealed partial class AdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _scope;
    private readonly string _key;
    private readonly ILogger? _logger;

    private AdvisoryLock(NpgsqlConnection connection, string scope, string key, ILogger? logger)
    {
        _connection = connection;
        _scope = scope;
        _key = key;
        _logger = logger;
    }

    /// <summary>Takes the lock <paramref name="scope"/>/<paramref name="key"/> if it is free; null if someone else holds it.</summary>
    public static async Task<AdvisoryLock?> TryAcquireAsync(
        NpgsqlDataSource dataSource, string scope, string key, ILogger? logger, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        var acquired = false;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1), hashtext($2))", connection);
            command.Parameters.AddWithValue(scope);
            command.Parameters.AddWithValue(key);
            acquired = (bool)(await command.ExecuteScalarAsync(ct))!;
            return acquired ? new AdvisoryLock(connection, scope, key, logger) : null;
        }
        finally
        {
            if (!acquired) await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext($1), hashtext($2))", _connection);
            command.Parameters.AddWithValue(_scope);
            command.Parameters.AddWithValue(_key);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A pooled connection keeps session-level advisory locks when it goes back to the
            // pool; make sure this one is closed instead of reused, which releases the lock.
            NpgsqlConnection.ClearPool(_connection);
            if (_logger is not null) LogUnlockFailed(_logger, ex, _scope, _key);
        }
        await _connection.DisposeAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't release the advisory lock {Scope}/{Key} explicitly; closing its connection instead")]
    private static partial void LogUnlockFailed(ILogger logger, Exception exception, string scope, string key);
}

/// <summary>
/// Another process holds the lock for this import. Not the source's failure — nothing was
/// attempted — so it is not recorded as one.
/// </summary>
public sealed class ImportAlreadyRunningException(string message) : InvalidOperationException(message);
