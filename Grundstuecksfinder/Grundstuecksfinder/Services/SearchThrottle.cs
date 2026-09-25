using Grundstuecksfinder.Models;

namespace Grundstuecksfinder.Services;

/// <summary>
/// Bounds on the location search. The HTTP pipeline never sees a search: Blazor runs it over the
/// circuit's WebSocket, so a middleware rate limiter only counts page loads.
/// </summary>
public sealed record SearchLimits
{
    /// <summary>Searches one circuit may start per <see cref="Window"/>; the page itself allows about one per second.</summary>
    public int PerCircuit { get; init; } = 20;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Searches running against the database at once, across all circuits.</summary>
    public int Concurrent { get; init; } = 8;
    /// <summary>How long a search waits for a free slot before it is turned away.</summary>
    public TimeSpan QueueWait { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Longest a query may run; Npgsql cancels it on the server.</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>A search was turned away or cut short; <see cref="Exception.Message"/> is meant for the visitor.</summary>
public sealed class SearchRejectedException(string message) : Exception(message);

/// <summary>Caps the searches running against the database at once, across all circuits.</summary>
public sealed partial class SearchGate(SearchLimits limits, ILogger<SearchGate> logger) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(limits.Concurrent, limits.Concurrent);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(limits.QueueWait, cancellationToken))
        {
            LogGateFull(limits.Concurrent, limits.QueueWait.TotalSeconds);
            throw new SearchRejectedException("Gerade sind viele Suchen gleichzeitig aktiv. Bitte versuche es gleich noch einmal.");
        }

        return new Slot(_slots);
    }

    public void Dispose() => _slots.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search turned away: all {Concurrent} concurrent search slots stayed busy for {QueueWait} s")]
    private partial void LogGateFull(int concurrent, double queueWait);

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slots.Release();
        }
    }
}

/// <summary>
/// The location search as the page runs it: a per-circuit budget so one client can't hammer the
/// database, the shared <see cref="SearchGate"/>, and a timeout on the query. Scoped, so the budget
/// is per circuit; opening more circuits is what the gate's global cap answers.
/// </summary>
public sealed partial class SearchThrottle(
    PropertyService properties, SearchGate gate, SearchLimits limits, TimeProvider time,
    ILogger<SearchThrottle> logger)
{
    private readonly Queue<DateTimeOffset> _recent = new();

    public async Task<List<Property>> SearchAsync(
        string? gemeinde, string? plz, double? minFlaeche, double? maxFlaeche, int limit)
    {
        var now = time.GetUtcNow();
        while (_recent.Count > 0 && now - _recent.Peek() >= limits.Window)
            _recent.Dequeue();
        if (_recent.Count >= limits.PerCircuit)
        {
            LogBudgetSpent(limits.PerCircuit, limits.Window.TotalSeconds);
            throw new SearchRejectedException("Zu viele Suchanfragen in kurzer Zeit. Bitte warte einen Moment.");
        }

        _recent.Enqueue(now);

        using var slot = await gate.EnterAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(limits.QueryTimeout, time);
        try
        {
            return await properties.GetPropertiesAsync(gemeinde, plz, minFlaeche, maxFlaeche, limit, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            LogTimedOut(limits.QueryTimeout.TotalSeconds);
            throw new SearchRejectedException("Die Suche hat zu lange gedauert. Bitte grenze sie weiter ein.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search turned away: one circuit reached its budget of {PerCircuit} searches per {Window} s")]
    private partial void LogBudgetSpent(int perCircuit, double window);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search cancelled: the query ran longer than {QueryTimeout} s")]
    private partial void LogTimedOut(double queryTimeout);
}
