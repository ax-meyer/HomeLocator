using Microsoft.Extensions.Logging;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>
/// Keeps every log line an operation writes, formatted as it would be rendered, so a test can
/// assert on what was reported — for lines that are the feature, like an import's summary.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IEnumerable<string> MessagesAt(LogLevel level) =>
        _entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Add((logLevel, formatter(state, exception)));
}
