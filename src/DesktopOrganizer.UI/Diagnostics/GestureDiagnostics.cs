using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Diagnostics;

/// <summary>
/// Default <see cref="IGestureDiagnostics"/> implementation. Writes structured events to the injected
/// <see cref="IDiagnosticsLog"/>; every method is a pure observer of state already computed by the
/// caller and never influences gesture geometry or control flow (docs/Refactoring/ §3.4 — this phase is
/// additive-only).
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero-cost when disabled:</b> every public method starts with a <see cref="IDiagnosticsLog.IsEnabled"/>
/// check and returns immediately (without touching any gesture-aggregation state or allocating a
/// <c>payload</c> array) when diagnostics logging is off — the default in Release builds.
/// </para>
/// <para>
/// <b>Delta throttling:</b> <see cref="ResizeDelta"/>/<see cref="DragDelta"/> always emit the first
/// delta of a gesture, then at most once per <see cref="DefaultDeltaSampleIntervalMs"/> (configurable
/// via the constructor) thereafter. The most recently observed delta/applied geometry — even when
/// throttled and not itself written to the log — is remembered and folded into the corresponding
/// <see cref="ResizeCompleted"/>/<see cref="DragCompleted"/> payload, so the final geometry right before
/// a gesture ends (or is canceled) is never lost to throttling.
/// </para>
/// <para>
/// <b>Not thread-safe:</b> the gesture-aggregation fields (accumulators, counters, throttle timestamps)
/// are mutated without any synchronization. This is intentional, not an oversight: one
/// <see cref="GestureDiagnostics"/> instance is created per panel (see <see cref="IGestureDiagnostics"/>'s
/// remarks) and a panel — and every gesture on it — only ever runs on the UI thread. Do not share a
/// single instance across panels or call its members from a background thread.
/// </para>
/// </remarks>
public sealed class GestureDiagnostics : IGestureDiagnostics
{
    /// <summary>Default minimum spacing, in milliseconds, between recorded delta events for a single gesture.</summary>
    public const int DefaultDeltaSampleIntervalMs = 50;

    private readonly IDiagnosticsLog _log;
    private readonly int _deltaSampleIntervalMs;
    private readonly Func<long> _nowTicks;

    // Resize-gesture aggregation state (reset in ResizeStarted).
    // Sentinel null (rather than 0) means "no ResizeStarted observed yet for the current gesture" —
    // lets ResizeCompleted tell a genuinely-missing ResizeStarted apart from one that started at tick 0
    // (finalization MINOR-6); see ResizeCompleted's durationMs computation.
    private long? _resizeGestureStartTicks;
    private long? _resizeLastEmittedTicks;
    private int _resizeDeltaCount;
    private int _resizeEmittedCount;
    private double _resizeLastRawWidth;
    private double _resizeLastRawHeight;
    private Rect _resizeLastApplied;

    // Drag-gesture aggregation state (reset in DragStarted). Sentinel null for the same reason as
    // _resizeGestureStartTicks above.
    private long? _dragGestureStartTicks;
    private long? _dragLastEmittedTicks;
    private int _dragDeltaCount;
    private int _dragEmittedCount;
    private Rect _dragLastApplied;

    /// <summary>
    /// Initializes a new instance of the <see cref="GestureDiagnostics"/> class.
    /// </summary>
    /// <param name="log">The underlying structured diagnostics sink.</param>
    /// <param name="deltaSampleIntervalMs">Minimum spacing between recorded delta events; see the type remarks.</param>
    /// <param name="nowTicks">
    /// Monotonic clock source, defaulting to <see cref="Environment.TickCount64"/>. Injectable so tests
    /// can drive gesture timing deterministically.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deltaSampleIntervalMs"/> is negative.</exception>
    public GestureDiagnostics(IDiagnosticsLog log, int deltaSampleIntervalMs = DefaultDeltaSampleIntervalMs, Func<long>? nowTicks = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfNegative(deltaSampleIntervalMs);

        _log = log;
        _deltaSampleIntervalMs = deltaSampleIntervalMs;
        _nowTicks = nowTicks ?? (() => Environment.TickCount64);
    }

    /// <inheritdoc />
    public void SessionStarted(string appVersion, string osVersion, double dpiScale, int monitorCount, int panelCount)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "session",
            "SessionStarted",
            null,
            new KeyValuePair<string, object?>("appVersion", appVersion),
            new KeyValuePair<string, object?>("osVersion", osVersion),
            new KeyValuePair<string, object?>("dpiScale", dpiScale),
            new KeyValuePair<string, object?>("monitorCount", monitorCount),
            new KeyValuePair<string, object?>("panelCount", panelCount));
    }

    /// <inheritdoc />
    public void SessionExiting()
    {
        if (!_log.IsEnabled) return;

        _log.Write("session", "SessionExiting", null);
    }

    /// <inheritdoc />
    public void ResizeStarted(
        Guid? panelId,
        string direction,
        Rect bounds,
        PanelViewMode viewMode,
        double iconSize,
        bool snapEnabled,
        double snapCellWidth,
        double snapCellHeight,
        string? captureOwner)
    {
        if (!_log.IsEnabled) return;

        _resizeGestureStartTicks = _nowTicks();
        _resizeLastEmittedTicks = null;
        _resizeDeltaCount = 0;
        _resizeEmittedCount = 0;
        _resizeLastRawWidth = bounds.Width;
        _resizeLastRawHeight = bounds.Height;
        _resizeLastApplied = bounds;

        _log.Write(
            "gesture",
            "ResizeStarted",
            Scope(panelId),
            new KeyValuePair<string, object?>("direction", direction),
            new KeyValuePair<string, object?>("boundsX", bounds.X),
            new KeyValuePair<string, object?>("boundsY", bounds.Y),
            new KeyValuePair<string, object?>("boundsWidth", bounds.Width),
            new KeyValuePair<string, object?>("boundsHeight", bounds.Height),
            new KeyValuePair<string, object?>("viewMode", viewMode.ToString()),
            new KeyValuePair<string, object?>("iconSize", iconSize),
            new KeyValuePair<string, object?>("snapEnabled", snapEnabled),
            new KeyValuePair<string, object?>("snapCellWidth", snapCellWidth),
            new KeyValuePair<string, object?>("snapCellHeight", snapCellHeight),
            new KeyValuePair<string, object?>("captureOwner", captureOwner));
    }

    /// <inheritdoc />
    public void ResizeDelta(
        Guid? panelId,
        string direction,
        double hChange,
        double vChange,
        double rawWidth,
        double rawHeight,
        Rect applied)
    {
        if (!_log.IsEnabled) return;

        _resizeDeltaCount++;
        _resizeLastRawWidth = rawWidth;
        _resizeLastRawHeight = rawHeight;
        _resizeLastApplied = applied;

        var now = _nowTicks();
        if (_resizeLastEmittedTicks is { } last && now - last < _deltaSampleIntervalMs)
            return; // Throttled — state above is still updated so ResizeCompleted sees the latest values.

        _resizeLastEmittedTicks = now;
        _resizeEmittedCount++;

        _log.Write(
            "gesture",
            "ResizeDelta",
            Scope(panelId),
            new KeyValuePair<string, object?>("direction", direction),
            new KeyValuePair<string, object?>("hChange", hChange),
            new KeyValuePair<string, object?>("vChange", vChange),
            new KeyValuePair<string, object?>("rawWidth", rawWidth),
            new KeyValuePair<string, object?>("rawHeight", rawHeight),
            new KeyValuePair<string, object?>("appliedX", applied.X),
            new KeyValuePair<string, object?>("appliedY", applied.Y),
            new KeyValuePair<string, object?>("appliedWidth", applied.Width),
            new KeyValuePair<string, object?>("appliedHeight", applied.Height));
    }

    /// <inheritdoc />
    public void ResizeCompleted(Guid? panelId, string direction, bool canceled, Rect bounds, string endReason, string? exceptionType)
    {
        if (!_log.IsEnabled) return;

        // -1 (never a real duration) rather than a garbage "time since process start" value if
        // ResizeCompleted is somehow observed without a preceding ResizeStarted — should not happen in
        // normal operation, but this is defensive, not load-bearing (finalization MINOR-6).
        var durationMs = _resizeGestureStartTicks is { } startTicks ? _nowTicks() - startTicks : -1L;

        _log.Write(
            "gesture",
            "ResizeCompleted",
            Scope(panelId),
            new KeyValuePair<string, object?>("direction", direction),
            new KeyValuePair<string, object?>("canceled", canceled),
            new KeyValuePair<string, object?>("boundsX", bounds.X),
            new KeyValuePair<string, object?>("boundsY", bounds.Y),
            new KeyValuePair<string, object?>("boundsWidth", bounds.Width),
            new KeyValuePair<string, object?>("boundsHeight", bounds.Height),
            new KeyValuePair<string, object?>("lastRawWidth", _resizeLastRawWidth),
            new KeyValuePair<string, object?>("lastRawHeight", _resizeLastRawHeight),
            new KeyValuePair<string, object?>("lastAppliedX", _resizeLastApplied.X),
            new KeyValuePair<string, object?>("lastAppliedY", _resizeLastApplied.Y),
            new KeyValuePair<string, object?>("lastAppliedWidth", _resizeLastApplied.Width),
            new KeyValuePair<string, object?>("lastAppliedHeight", _resizeLastApplied.Height),
            new KeyValuePair<string, object?>("deltaCount", _resizeDeltaCount),
            new KeyValuePair<string, object?>("emittedCount", _resizeEmittedCount),
            new KeyValuePair<string, object?>("durationMs", durationMs),
            new KeyValuePair<string, object?>("endReason", endReason),
            new KeyValuePair<string, object?>("exceptionType", exceptionType));
    }

    /// <inheritdoc />
    public void DragStarted(Guid? panelId, Point startCursor, Rect bounds)
    {
        if (!_log.IsEnabled) return;

        _dragGestureStartTicks = _nowTicks();
        _dragLastEmittedTicks = null;
        _dragDeltaCount = 0;
        _dragEmittedCount = 0;
        _dragLastApplied = bounds;

        _log.Write(
            "gesture",
            "DragStarted",
            Scope(panelId),
            new KeyValuePair<string, object?>("startCursorX", startCursor.X),
            new KeyValuePair<string, object?>("startCursorY", startCursor.Y),
            new KeyValuePair<string, object?>("boundsX", bounds.X),
            new KeyValuePair<string, object?>("boundsY", bounds.Y),
            new KeyValuePair<string, object?>("boundsWidth", bounds.Width),
            new KeyValuePair<string, object?>("boundsHeight", bounds.Height));
    }

    /// <inheritdoc />
    public void DragDelta(Guid? panelId, Rect applied)
    {
        if (!_log.IsEnabled) return;

        _dragDeltaCount++;
        _dragLastApplied = applied;

        var now = _nowTicks();
        if (_dragLastEmittedTicks is { } last && now - last < _deltaSampleIntervalMs)
            return; // Throttled — state above is still updated so DragCompleted sees the latest values.

        _dragLastEmittedTicks = now;
        _dragEmittedCount++;

        _log.Write(
            "gesture",
            "DragDelta",
            Scope(panelId),
            new KeyValuePair<string, object?>("appliedX", applied.X),
            new KeyValuePair<string, object?>("appliedY", applied.Y),
            new KeyValuePair<string, object?>("appliedWidth", applied.Width),
            new KeyValuePair<string, object?>("appliedHeight", applied.Height));
    }

    /// <inheritdoc />
    public void DragCompleted(Guid? panelId, Rect bounds)
    {
        if (!_log.IsEnabled) return;

        // -1 sentinel — see the matching comment in ResizeCompleted (finalization MINOR-6).
        var durationMs = _dragGestureStartTicks is { } startTicks ? _nowTicks() - startTicks : -1L;

        _log.Write(
            "gesture",
            "DragCompleted",
            Scope(panelId),
            new KeyValuePair<string, object?>("boundsX", bounds.X),
            new KeyValuePair<string, object?>("boundsY", bounds.Y),
            new KeyValuePair<string, object?>("boundsWidth", bounds.Width),
            new KeyValuePair<string, object?>("boundsHeight", bounds.Height),
            new KeyValuePair<string, object?>("lastAppliedX", _dragLastApplied.X),
            new KeyValuePair<string, object?>("lastAppliedY", _dragLastApplied.Y),
            new KeyValuePair<string, object?>("lastAppliedWidth", _dragLastApplied.Width),
            new KeyValuePair<string, object?>("lastAppliedHeight", _dragLastApplied.Height),
            new KeyValuePair<string, object?>("deltaCount", _dragDeltaCount),
            new KeyValuePair<string, object?>("emittedCount", _dragEmittedCount),
            new KeyValuePair<string, object?>("durationMs", durationMs));
    }

    /// <inheritdoc />
    public void PanelClosing(Guid? panelId, bool resizeActive)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "panel",
            "PanelClosing",
            Scope(panelId),
            new KeyValuePair<string, object?>("resizeActive", resizeActive));
    }

    /// <inheritdoc />
    public void ExistenceRefreshFailed(Guid? panelId, string exceptionType)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "panel",
            "ExistenceRefreshFailed",
            Scope(panelId),
            new KeyValuePair<string, object?>("exceptionType", exceptionType));
    }

    /// <inheritdoc />
    public void IconPipelineSynchronousFallbackUsed(Guid? panelId, string component)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "panel",
            "IconPipelineSynchronousFallbackUsed",
            Scope(panelId),
            new KeyValuePair<string, object?>("component", component));
    }

    /// <inheritdoc />
    public void AlignToWorkspaceGridRefused(Guid? panelId, string reason)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "panel",
            "AlignToWorkspaceGridRefused",
            Scope(panelId),
            new KeyValuePair<string, object?>("reason", reason));
    }

    /// <inheritdoc />
    public void SizeMoveEntered(Guid? panelId, long hwnd, string edge)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "gesture",
            "SizeMoveEntered",
            Scope(panelId),
            new KeyValuePair<string, object?>("hwnd", hwnd),
            new KeyValuePair<string, object?>("edge", edge));
    }

    /// <inheritdoc />
    public void SizeMoveExited(Guid? panelId, long hwnd, Rect finalBounds)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "gesture",
            "SizeMoveExited",
            Scope(panelId),
            new KeyValuePair<string, object?>("hwnd", hwnd),
            new KeyValuePair<string, object?>("finalX", finalBounds.X),
            new KeyValuePair<string, object?>("finalY", finalBounds.Y),
            new KeyValuePair<string, object?>("finalWidth", finalBounds.Width),
            new KeyValuePair<string, object?>("finalHeight", finalBounds.Height));
    }

    /// <inheritdoc />
    public void LayoutModeChanged(LayoutInteractionMode from, LayoutInteractionMode to, LayoutModeChangeSource source, bool isLocked)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "layout",
            "LayoutModeChanged",
            null,
            new KeyValuePair<string, object?>("from", from.ToString()),
            new KeyValuePair<string, object?>("to", to.ToString()),
            new KeyValuePair<string, object?>("source", source.ToString()),
            new KeyValuePair<string, object?>("isLocked", isLocked));
    }

    /// <inheritdoc />
    public void SizingModeChanged(Guid? panelId, PanelSizingMode from, PanelSizingMode to, string reason)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "layout",
            "SizingModeChanged",
            Scope(panelId),
            new KeyValuePair<string, object?>("from", from.ToString()),
            new KeyValuePair<string, object?>("to", to.ToString()),
            new KeyValuePair<string, object?>("reason", reason));
    }

    /// <inheritdoc />
    public void PanelItemsBuilt(Guid panelId, int count, long elapsedMicroseconds)
    {
        if (!_log.IsEnabled) return;

        _log.Write(
            "panel",
            "PanelItemsBuilt",
            Scope(panelId),
            new KeyValuePair<string, object?>("count", count),
            new KeyValuePair<string, object?>("elapsedMicroseconds", elapsedMicroseconds));
    }

    /// <summary>
    /// Reduces a panel id to the 8-character opaque prefix <see cref="DiagnosticEvent"/> requires —
    /// never the full GUID, and never anything derived from a panel title or file path (privacy
    /// invariant — see <see cref="DiagnosticEvent"/>'s remarks).
    /// </summary>
    private static string? Scope(Guid? panelId) => panelId?.ToString("N")[..8];
}
