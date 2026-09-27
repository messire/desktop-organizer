using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Builds and caches the <see cref="DesktopScaleModel"/> snapshot (release-hardening F §3.2, ADR-037).
/// </summary>
/// <remarks>
/// <para><b>Mode detection</b>, in order of trust:</para>
/// <list type="number">
/// <item>Fact: a reported panel window that is still alive. <c>GetAncestor(GA_ROOT)</c> different from the
/// panel → <see cref="DesktopLayerMode.Embedded"/> with the root's DPI as the layer scale; equal →
/// <see cref="DesktopLayerMode.TopLevel"/>. A fact that contradicts a previous prediction is logged as
/// <c>DesktopLayerModeMismatch</c>.</item>
/// <item>Prediction (no live panel yet): <see cref="IDesktopIntegrationService.FindDesktopLayerHost"/>
/// found a <c>WorkerW</c> → Embedded with that window's DPI; otherwise TopLevel.</item>
/// <item>Any failure keeps the previous snapshot; without one, a uniform top-level snapshot at the
/// system scale (the 0.6.1 equivalent). Nothing ever throws.</item>
/// </list>
/// <para>
/// All P/Invoke goes through <see cref="INativeApi"/>. UI-thread affine like its callers
/// (<c>MonitorService</c> WndProc, <c>WindowManager</c>, windows' <c>DpiChanged</c>); a lock only keeps the
/// snapshot/panel bookkeeping consistent, and <see cref="Changed"/> is raised outside it.
/// </para>
/// <para>
/// MANUAL VERIFY (Win10/Win11): mode = Embedded on Windows 10 22H2 / Windows 11 23H2, TopLevel on
/// Windows 11 24H2+; mixed-DPI monitor scales from <c>GetDpiForMonitor</c> (plan §10).
/// </para>
/// </remarks>
internal sealed class DesktopScaleProvider(
    INativeApi nativeApi,
    IDesktopIntegrationService desktopIntegration,
    IAppLog? appLog = null) : IDesktopScaleProvider
{
    private const string LogSource = "DesktopScaleProvider";

    /// <summary>
    /// Upper bound on remembered panel handles. Detection only needs one live panel; the list exists so
    /// closing the most recently reported panel does not throw detection back to prediction.
    /// </summary>
    private const int MaxReportedPanels = 64;

    private readonly object _gate = new();
    private readonly List<IntPtr> _reportedPanels = [];

    private DesktopScaleModel? _current;
    private ModeSource _currentSource;

    // M10 (release-hardening 1.0 review): see BeginBatch's own remarks. _batchDepth supports nested
    // scopes (only the outermost Dispose actually rebuilds); _batchDirty records whether ANY
    // ReportPanelWindow/Refresh call happened while a scope was open, so a batch that never actually
    // changed anything doesn't pay for a rebuild it doesn't need.
    private int _batchDepth;
    private bool _batchDirty;

    /// <inheritdoc />
    public event EventHandler<DesktopScaleChangedEventArgs>? Changed;

    /// <summary>How the mode of the current snapshot was determined.</summary>
    private enum ModeSource
    {
        /// <summary>No detection succeeded (fallback snapshot).</summary>
        Fallback,

        /// <summary>Predicted from the <c>WorkerW</c> lookup.</summary>
        Prediction,

        /// <summary>Measured on a live, reported panel window.</summary>
        Fact,
    }

    // SA1204 (static before instance members): kept here, ahead of every instance member, rather than
    // next to its only caller (Rebuild) further down.
    private static DesktopScaleModel CreateFallback(double referenceScale)
        => DesktopScaleModel.Create(DesktopLayerMode.TopLevel, referenceScale, referenceScale, []);

    /// <inheritdoc />
    public DesktopScaleModel Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null) return _current;
            }

            // First access: build without raising Changed (there is nothing to compare with).
            Rebuild();
            lock (_gate)
            {
                return _current ?? CreateFallback(1d);
            }
        }
    }

    /// <inheritdoc />
    public DesktopLayerMode Mode => Current.Mode;

    /// <summary>
    /// The log this instance writes to: the injected one, else the process-wide ambient log (tests).
    /// </summary>
    private IAppLog Log => appLog ?? AppLog.Current;

    /// <inheritdoc />
    public void ReportPanelWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        lock (_gate)
        {
            _reportedPanels.Remove(hwnd);
            _reportedPanels.Insert(0, hwnd);
            if (_reportedPanels.Count > MaxReportedPanels)
                _reportedPanels.RemoveRange(MaxReportedPanels, _reportedPanels.Count - MaxReportedPanels);
        }

        // M10: the panel-handle bookkeeping above always runs immediately (it's cheap, and DetectMode's
        // "Fact" lookup should see the newest reported panel the moment a batch closes) — only the
        // rebuild/Changed itself is deferred while a batch is open.
        if (TryMarkDirtyInsteadOfRebuilding()) return;
        Rebuild();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        if (TryMarkDirtyInsteadOfRebuilding()) return;
        Rebuild();
    }

    /// <inheritdoc />
    public IDisposable BeginBatch()
    {
        lock (_gate) _batchDepth++;
        return new BatchScope(this);
    }

    /// <summary>
    /// M10: while a batch is open, records that a rebuild is owed instead of performing it inline.
    /// </summary>
    /// <returns><see langword="true"/> if a batch is currently open (the caller must not rebuild itself); otherwise <see langword="false"/>.</returns>
    private bool TryMarkDirtyInsteadOfRebuilding()
    {
        lock (_gate)
        {
            if (_batchDepth <= 0) return false;
            _batchDirty = true;
            return true;
        }
    }

    /// <summary>
    /// Closes one level of batch nesting (<see cref="BatchScope.Dispose"/>); performs exactly one
    /// <see cref="Rebuild"/> if this was the OUTERMOST open scope and something was marked dirty while
    /// any scope was open.
    /// </summary>
    private void EndBatch()
    {
        bool shouldRebuild;
        lock (_gate)
        {
            if (_batchDepth > 0) _batchDepth--;
            shouldRebuild = _batchDepth == 0 && _batchDirty;
            if (shouldRebuild) _batchDirty = false;
        }

        if (shouldRebuild) Rebuild();
    }

    private void Rebuild()
    {
        DesktopScaleModel? previous;
        ModeSource previousSource;
        lock (_gate)
        {
            previous = _current;
            previousSource = _currentSource;
        }

        DesktopScaleModel next;
        ModeSource nextSource;
        try
        {
            var referenceScale = nativeApi.GetSystemDpiScale();
            var natives = nativeApi.GetMonitorInfos();
            if (natives.Count == 0 && previous is not null)
            {
                // EnumDisplayMonitors answers an empty list only on failure (or mid-reconfiguration):
                // a snapshot without monitors would be worse than the last good one.
                Log.Warn(LogSource, "NoMonitorsKeptPreviousSnapshot");
                return;
            }

            var monitors = natives
                .Select(m => new MonitorScale(
                    m.DeviceName,
                    m.Bounds,
                    m.WorkArea,
                    m.IsPrimary,
                    DesktopScaleModel.ScaleFromDpi(m.EffectiveDpi, referenceScale)))
                .ToList();

            var (mode, layerScale, source) = DetectMode(referenceScale);
            next = DesktopScaleModel.Create(mode, layerScale, referenceScale, monitors);
            nextSource = source;

            if (source == ModeSource.Fact && previousSource == ModeSource.Prediction && previous is not null && previous.Mode != mode)
            {
                Log.Warn(
                    LogSource,
                    "DesktopLayerModeMismatch",
                    new KeyValuePair<string, object?>("predicted", previous.Mode.ToString()),
                    new KeyValuePair<string, object?>("actual", mode.ToString()));
            }

            if (next.IsAnchorOverlapFallback && previous?.IsAnchorOverlapFallback != true)
            {
                Log.Warn(
                    LogSource,
                    "DesktopScaleAnchorOverlap",
                    new KeyValuePair<string, object?>("monitors", next.Monitors.Count),
                    new KeyValuePair<string, object?>("referenceScale", next.ReferenceScale));
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "RefreshFailed", ex);
            if (previous is not null) return;

            next = CreateFallback(SafeSystemScale());
            nextSource = ModeSource.Fallback;
        }

        lock (_gate)
        {
            _current = next;
            _currentSource = nextSource;
        }

        if (previous is null || next.IsEquivalentTo(previous)) return;

        if (previous.Mode != next.Mode)
        {
            Log.Info(
                LogSource,
                "DesktopLayerModeChanged",
                new KeyValuePair<string, object?>("from", previous.Mode.ToString()),
                new KeyValuePair<string, object?>("to", next.Mode.ToString()));
        }

        RaiseChanged(previous, next);
    }

    private (DesktopLayerMode Mode, double LayerScale, ModeSource Source) DetectMode(double referenceScale)
    {
        // 1. Fact: the newest reported panel that is still alive.
        foreach (var panel in SnapshotAlivePanels())
        {
            var root = nativeApi.GetRootWindow(panel);
            if (root == IntPtr.Zero) continue;

            return root != panel
                ? (DesktopLayerMode.Embedded, nativeApi.GetDpiScaleForWindow(root), ModeSource.Fact)
                : (DesktopLayerMode.TopLevel, referenceScale, ModeSource.Fact);
        }

        // 2. Prediction: is there a WorkerW the panels would be embedded into?
        var host = desktopIntegration.FindDesktopLayerHost();
        return host != IntPtr.Zero
            ? (DesktopLayerMode.Embedded, nativeApi.GetDpiScaleForWindow(host), ModeSource.Prediction)
            : (DesktopLayerMode.TopLevel, referenceScale, ModeSource.Prediction);
    }

    /// <summary>
    /// Returns the reported panels that are still alive, newest first, and forgets the dead ones.
    /// </summary>
    private List<IntPtr> SnapshotAlivePanels()
    {
        IntPtr[] candidates;
        lock (_gate)
        {
            candidates = [.. _reportedPanels];
        }

        var alive = candidates.Where(nativeApi.IsWindow).ToList();
        if (alive.Count == candidates.Length) return alive;

        lock (_gate)
        {
            _reportedPanels.RemoveAll(h => !alive.Contains(h));
        }

        return alive;
    }

    private double SafeSystemScale()
    {
        try
        {
            return nativeApi.GetSystemDpiScale();
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "SystemDpiFailed", ex);
            return 1d;
        }
    }

    private void RaiseChanged(DesktopScaleModel previous, DesktopScaleModel next)
    {
        try
        {
            Changed?.Invoke(this, new DesktopScaleChangedEventArgs(previous, next));
        }
        catch (Exception ex)
        {
            // A subscriber's failure must not escape into the WndProc/DpiChanged path that refreshed us.
            Log.Error(LogSource, "ChangedHandlerFailed", ex);
        }
    }

    /// <summary>The disposable handle <see cref="BeginBatch"/> returns (M10) — idempotent Dispose.</summary>
    private sealed class BatchScope(DesktopScaleProvider owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.EndBatch();
        }
    }
}
