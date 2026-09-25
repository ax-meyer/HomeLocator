using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The one way a source talks to its servers: every request of every provider of one source —
/// parcel tiles, address pages, hit counts, a file download — goes through one instance, so
/// they share one pace (<see cref="InspireSourceOptions.MinRequestIntervalSeconds"/>) and one
/// circuit breaker, and a server that is down only trips its own state.
/// </summary>
/// <remarks>
/// Requests are issued strictly one at a time per source (the tile loop awaits each), which the
/// throttle relies on.
/// </remarks>
public sealed partial class InspireServiceClient(
    HttpClient http,
    InspireSourceOptions options,
    ILogger logger,
    TimeProvider time,
    ILoggerFactory? loggerFactory = null)
{
    private static readonly ResiliencePropertyKey<string> WhatKey = new("what");
    private static readonly ResiliencePropertyKey<string> WhereKey = new("where");
    private static readonly ResiliencePropertyKey<TimeSpan> TimeoutKey = new("timeout");

    /// <summary>Built lazily: nothing needs it before the first request.</summary>
    private ResiliencePipeline? _pipeline;

    /// <summary>
    /// Earliest instant the next request may go out. Requests are issued one at a time, so a
    /// single instant is enough and needs no lock.
    /// </summary>
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    private string Source => options.Source;

    /// <summary>
    /// Runs one logical request — typically a fetch plus its parsing, so a garbled body is retried
    /// like a failed connection — through retry, circuit breaker and a per-attempt timeout.
    /// </summary>
    /// <remarks>
    /// <c>what</c> ("cp:CadastralParcel") and <c>where</c> (a tile's bbox, a page offset) only
    /// name the request in the retry log. <c>timeout</c> is the per-attempt limit; it defaults to
    /// <see cref="InspireSourceOptions.RequestTimeoutSeconds"/>, which is sized for one WFS page,
    /// not for a whole file.
    /// </remarks>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action, string what, string where, CancellationToken ct, TimeSpan? timeout = null)
    {
        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(WhatKey, what);
        context.Properties.Set(WhereKey, where);
        context.Properties.Set(TimeoutKey, timeout ?? TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try
        {
            return await (_pipeline ??= BuildPipeline()).ExecuteAsync(
                static async (ctx, state) => await state(ctx.CancellationToken), context, action);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>One attempt at an XML document. Call inside <see cref="ExecuteAsync{T}"/>.</summary>
    public async Task<XDocument> ReadXmlAsync(string url, CancellationToken ct)
    {
        await using var body = await ReadBodyAsync(url, accept: null, ct);
        return await XDocument.LoadAsync(body, LoadOptions.None, ct);
    }

    /// <summary>
    /// One attempt at a JSON document. Call inside <see cref="ExecuteAsync{T}"/>. The media type
    /// is asked for explicitly: without it, Saarland's OGC API Features server answers its HTML
    /// viewer instead of GeoJSON.
    /// </summary>
    public async Task<JsonDocument> ReadJsonAsync(string url, string accept, CancellationToken ct)
    {
        await using var body = await ReadBodyAsync(url, accept, ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    /// <summary>
    /// One attempt at downloading a file to <paramref name="path"/>. Call inside
    /// <see cref="ExecuteAsync{T}"/> with a timeout sized for the file. There is no resume:
    /// every attempt starts the file over.
    /// </summary>
    /// <returns>The file's size in bytes.</returns>
    public async Task<long> DownloadOnceAsync(string url, string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, url, accept: null, ct);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, ct);
        // A body cut short without an error would only surface as a broken ZIP, which isn't
        // retried; a short file is.
        if (response.Content.Headers.ContentLength is { } expected && file.Length != expected)
            throw new IOException(FormattableString.Invariant(
                $"The download of {url} ended after {file.Length} of {expected} bytes."));
        return file.Length;
    }

    private async Task<MemoryStream> ReadBodyAsync(string url, string? accept, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, url, accept, ct);
        // Buffer first: CopyToAsync passes the token to every read, so the per-attempt timeout
        // can abort a body that stalls. XDocument.LoadAsync/JsonDocument.ParseAsync don't hand
        // their token to the stream, so parsing straight from the network could hang forever.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var body = new MemoryStream();
        await stream.CopyToAsync(body, ct);
        body.Position = 0;
        return body;
    }

    /// <summary>
    /// One paced request; throws <see cref="SourceHttpException"/> for a non-success status.
    /// The caller owns (and disposes) the response.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? accept, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        using var request = new HttpRequestMessage(method, url);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date - DateTimeOffset.UtcNow)
                : null;
            throw new SourceHttpException(response.StatusCode, retryAfter, url);
        }
    }

    /// <summary>
    /// Holds the source to at most one request per
    /// <see cref="InspireSourceOptions.MinRequestIntervalSeconds"/>. A whole state is tens of
    /// thousands of requests against one public download service, and nothing else in the fetch
    /// paces them: without this the loop runs as fast as the server answers. Retries are paced
    /// too, since every attempt goes through here.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.MinRequestIntervalSeconds);
        if (interval <= TimeSpan.Zero) return;

        var wait = _nextRequestAt - time.GetUtcNow();
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, time, ct);
        _nextRequestAt = time.GetUtcNow() + interval;
    }

    /// <summary>
    /// Exponential backoff with jitter, never below a server's Retry-After and never above
    /// <see cref="InspireSourceOptions.MaxRetryDelaySeconds"/>; then a circuit breaker so a server
    /// that is down fails the import in seconds instead of retrying every one of thousands of
    /// tiles; innermost a per-attempt timeout that also bounds reading and parsing the body,
    /// which HttpClient.Timeout stops covering once the headers have arrived.
    /// </summary>
    private ResiliencePipeline BuildPipeline()
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = time, Name = $"inspire:{Source}" };
        // Polly's own logs and metrics (meter "Polly", tagged with the pipeline name), next to
        // the app's meter in AppMetrics. Left off when no factory is available, e.g. in tests.
        if (loggerFactory is not null)
            builder.ConfigureTelemetry(loggerFactory);
        return builder
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),
                MaxRetryAttempts = options.MaxAttempts - 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(options.RetryBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.MaxRetryDelaySeconds),
                DelayGenerator = args =>
                {
                    // Polly's own delay is in args.Context; a server's Retry-After wins when longer.
                    var retryAfter = (args.Outcome.Exception as SourceHttpException)?.RetryAfter;
                    return ValueTask.FromResult(retryAfter is { } wait
                        ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, options.MaxRetryDelaySeconds))
                        : (TimeSpan?)null);
                },
                OnRetry = args =>
                {
                    LogRetrying(logger, args.Outcome.Exception!, Source,
                        args.Context.Properties.GetValue(WhatKey, "request"),
                        args.Context.Properties.GetValue(WhereKey, ""),
                        args.AttemptNumber + 1, options.MaxAttempts, args.RetryDelay);
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),
                FailureRatio = options.CircuitFailureRatio,
                MinimumThroughput = options.CircuitMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakSeconds),
                OnOpened = args =>
                {
                    LogCircuitOpened(logger, args.Outcome.Exception!, Source, args.BreakDuration);
                    return default;
                },
                OnClosed = args =>
                {
                    LogCircuitClosed(logger, Source);
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                TimeoutGenerator = args => ValueTask.FromResult(
                    args.Context.Properties.GetValue(TimeoutKey, TimeSpan.FromSeconds(options.RequestTimeoutSeconds))),
            })
            .Build();
    }

    /// <summary>
    /// <see cref="TransientErrors"/> plus what only a WFS/OGC API response can go wrong with: a
    /// body that is truncated, garbled or an error document instead of a feature collection.
    /// </summary>
    public static bool IsTransient(Exception? ex) => ex switch
    {
        SourceHttpException { StatusCode: { } status } => TransientErrors.IsTransientStatus(status),
        XmlException or JsonException or WfsResponseException => true,
        _ => TransientErrors.IsTransient(ex),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: {What} for {Where} failed (attempt {Attempt} of {MaxAttempts}), retrying in {Delay}")]
    private static partial void LogRetrying(ILogger logger, Exception exception, string source, string what, string where, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Source}: too many requests failed; pausing all requests for {BreakDuration}")]
    private static partial void LogCircuitOpened(ILogger logger, Exception exception, string source, TimeSpan breakDuration);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: requests are getting through again")]
    private static partial void LogCircuitClosed(ILogger logger, string source);
}
