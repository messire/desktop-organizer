namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Lightweight, zero-dependency structured diagnostics sink for gesture lifecycle events
/// (resize/drag) and other troubleshooting instrumentation. Off by default in Release builds — see
/// the concrete registration in DI for the enable rules.
/// </summary>
/// <remarks>
/// Implementations must be safe to call from the UI thread on every frame of a drag/resize gesture:
/// <see cref="Write"/> must never block on I/O and must never throw. When <see cref="IsEnabled"/> is
/// <see langword="false"/>, callers should skip building the <c>payload</c> array entirely (a
/// zero-cost seam) rather than relying on the no-op implementation to discard it cheaply.
/// </remarks>
public interface IDiagnosticsLog : IDisposable
{
    /// <summary>
    /// Whether this log actually records events. Callers should check this before building a
    /// <c>payload</c> array so that disabled diagnostics cost nothing beyond this flag check.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Records a single structured event. Never blocks and never throws — implementations must
    /// degrade (e.g. drop the event) rather than let a diagnostics failure affect the caller.
    /// </summary>
    /// <param name="category">Coarse grouping, e.g. <c>"session"</c> or <c>"gesture"</c>.</param>
    /// <param name="name">The specific event name, e.g. <c>"ResizeStarted"</c>.</param>
    /// <param name="scope">
    /// An opaque panel-id prefix the event relates to, or <see langword="null"/> for session-level
    /// events. Never a title, file name, or path — see <see cref="DiagnosticEvent"/> remarks.
    /// </param>
    /// <param name="payload">Additional structured fields for this event.</param>
    void Write(string category, string name, string? scope, params KeyValuePair<string, object?>[] payload);

    /// <summary>
    /// Synchronously flushes any buffered events to their destination, waiting (with an internal
    /// bounded timeout) for the flush to complete. Intended to be called at the end of a gesture and
    /// during application shutdown so recent events aren't lost.
    /// </summary>
    void Flush();
}
