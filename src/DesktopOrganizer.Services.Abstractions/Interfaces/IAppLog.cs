namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Always-on structured application-event log (ADR-033 part 1) — the facade that replaces every former
/// <c>Console.Error.WriteLine</c> call site across the solution. Unlike <see cref="IDiagnosticsLog"/>
/// (verbose-only gesture/lifecycle tracing, off by default in Release), an <see cref="IAppLog"/>
/// implementation always writes to its sink in every build configuration — the concrete DI registration
/// decides which physical sink that is (see <c>DiagnosticsAppLog</c>/<c>DiConfig</c>).
/// </summary>
/// <remarks>
/// Implementations must never throw back into the caller regardless of what the underlying sink does — a
/// diagnostics/logging failure must never turn into an application crash or mask a real error. Every
/// string value passed in <c>data</c>, plus <see cref="Exception.Message"/>/<see cref="Exception.StackTrace"/>
/// for <see cref="Error"/>, is scrubbed for absolute/UNC paths and the current user name before being
/// written (see <c>LogScrubber</c>) — every event is English-only and must never otherwise embed a panel
/// title or a raw file path (same privacy invariant as <see cref="DiagnosticEvent"/>).
/// </remarks>
public interface IAppLog
{
    /// <summary>Records an informational lifecycle event.</summary>
    /// <param name="source">Short, stable identifier of the component raising the event (e.g. a class name).</param>
    /// <param name="evt">The specific event name, e.g. <c>"StorageLockUnavailable"</c>.</param>
    /// <param name="data">Additional structured fields; string values are scrubbed before being written.</param>
    void Info(string source, string evt, params KeyValuePair<string, object?>[] data);

    /// <summary>Records a recoverable-but-notable condition.</summary>
    /// <param name="source">Component raising the warning (same convention as <see cref="Info"/>).</param>
    /// <param name="evt">Name of the warning event (same convention as <see cref="Info"/>).</param>
    /// <param name="data">Structured fields of the warning (scrubbed like those of <see cref="Info"/>).</param>
    void Warn(string source, string evt, params KeyValuePair<string, object?>[] data);

    /// <summary>Records a failure, optionally carrying the exception that caused it.</summary>
    /// <param name="source">Component reporting the failure (same convention as <see cref="Info"/>).</param>
    /// <param name="evt">Name of the failure event (same convention as <see cref="Info"/>).</param>
    /// <param name="ex">
    /// The exception that caused this failure, or <see langword="null"/> if there isn't one. When
    /// non-null, its type name, <see cref="Exception.HResult"/>, scrubbed message, and scrubbed stack
    /// trace are all included in the written event.
    /// </param>
    /// <param name="data">Structured fields of the failure (scrubbed like those of <see cref="Info"/>).</param>
    void Error(string source, string evt, Exception? ex, params KeyValuePair<string, object?>[] data);

    /// <summary>
    /// Blocks until every event written so far has reached the physical sink (bounded by the sink's own
    /// shutdown budget). Used on the crash and shutdown paths so the last entries are not lost when the
    /// process ends right after the handler returns. Must never throw.
    /// </summary>
    void Flush();
}
