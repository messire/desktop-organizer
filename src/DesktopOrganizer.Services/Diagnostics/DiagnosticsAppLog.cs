using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// The production <see cref="IAppLog"/> implementation (ADR-033 part 1): writes every
/// <see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> call to the shared diagnostics sink under
/// category <c>"app"</c>, regardless of the current <see cref="DiagnosticsLevel"/> — unlike
/// <see cref="IDiagnosticsLog"/> itself (verbose-only gesture tracing), this is the facade that replaces
/// every former <c>Console.Error.WriteLine</c> call site.
/// </summary>
/// <remarks>
/// The sink is resolved lazily, exactly once per call, through a <see cref="Func{TResult}"/> constructor
/// parameter rather than an already-materialized <see cref="IDiagnosticsLog"/> — the same ADR-023 R-12
/// pattern used elsewhere in this project (e.g. <c>ISingleInstanceCoordinator</c>'s own <c>diagnostics</c>
/// constructor parameter): a process that never actually logs anything (an ordinary secondary launch, or
/// a primary session with nothing to report) must never materialize the JSONL writer thread just because
/// <see cref="IAppLog"/> itself was resolved from DI.
/// <para>
/// Never throws: every public member wraps its body in a <c>try/catch</c> around the sink call — a
/// diagnostics failure must never propagate into ordinary application control flow, which is the entire
/// point of replacing a bare <c>Console.Error.WriteLine</c> call with this facade.
/// </para>
/// </remarks>
public sealed class DiagnosticsAppLog : IAppLog
{
    private readonly Func<IDiagnosticsLog> _sink;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsAppLog"/> class.
    /// </summary>
    /// <param name="sink">
    /// Resolves the shared diagnostics sink on demand — see the type remarks for why this is a factory
    /// rather than an already-materialized <see cref="IDiagnosticsLog"/>.
    /// </param>
    public DiagnosticsAppLog(Func<IDiagnosticsLog> sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <inheritdoc />
    public void Info(string source, string evt, params KeyValuePair<string, object?>[] data) =>
        Write("info", source, evt, ex: null, data);

    /// <inheritdoc />
    public void Warn(string source, string evt, params KeyValuePair<string, object?>[] data) =>
        Write("warn", source, evt, ex: null, data);

    /// <inheritdoc />
    public void Error(string source, string evt, Exception? ex, params KeyValuePair<string, object?>[] data) =>
        Write("error", source, evt, ex, data);

    /// <inheritdoc />
    public void Flush()
    {
        try
        {
            _sink().Flush();
        }
        catch
        {
            // A diagnostics failure must never propagate into the crash/shutdown path (see IAppLog remarks).
        }
    }

    private static KeyValuePair<string, object?>[] BuildPayload(
        string level, string source, Exception? ex, KeyValuePair<string, object?>[] data)
    {
        var fields = new List<KeyValuePair<string, object?>>(4 + data.Length)
        {
            new("level", level),
            new("source", source),
        };

        foreach (var field in data)
            fields.Add(new KeyValuePair<string, object?>(field.Key, ScrubValue(field.Value)));

        if (ex is not null)
        {
            fields.Add(new KeyValuePair<string, object?>("exType", ex.GetType().Name));
            fields.Add(new KeyValuePair<string, object?>("hresult", ex.HResult));
            fields.Add(new KeyValuePair<string, object?>("msg", LogScrubber.Scrub(ex.Message)));
            fields.Add(new KeyValuePair<string, object?>("stack", LogScrubber.Scrub(ex.StackTrace ?? string.Empty)));
        }

        return fields.ToArray();
    }

    private static object? ScrubValue(object? value) => value is string text ? LogScrubber.Scrub(text) : value;

    private void Write(string level, string source, string evt, Exception? ex, KeyValuePair<string, object?>[] data)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(evt);

        try
        {
            var payload = BuildPayload(level, source, ex, data);
            _sink().Write("app", evt, null, payload);
        }
        catch
        {
            // Never throws back into the caller — see type remarks. A failure here (a misbehaving
            // custom sink, a disposed writer, etc.) must never turn a logging call into a crash.
        }
    }
}
