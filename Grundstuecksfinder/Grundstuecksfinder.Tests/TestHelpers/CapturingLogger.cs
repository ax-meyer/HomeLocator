using Microsoft.Extensions.Logging;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>
/// Keeps every log line an operation writes, formatted as it would be rendered, so a test can
/// assert on what was reported — for lines that are the feature, like an import's summary.
/// Safe to read while a background service is still logging.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries) return [.. _entries];
        }
    }

    public IEnumerable<string> MessagesAt(LogLevel level) =>
        Entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_entries) _entries.Add((logLevel, message));
    }
}
