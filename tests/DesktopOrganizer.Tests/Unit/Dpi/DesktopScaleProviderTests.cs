using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;
using Rectangle = System.Drawing.Rectangle;

namespace DesktopOrganizer.Tests.Unit.Dpi;

/// <summary>
/// Unit tests for <see cref="DesktopScaleProvider"/> (release-hardening F1, plan §3.2/§7): mode detection
/// by fact vs prediction, effective-DPI fallback, failure tolerance and the <see cref="IDesktopScaleProvider.Changed"/>
/// contract — all against mocked <see cref="INativeApi"/>/<see cref="IDesktopIntegrationService"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopScaleProviderTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";
    private const string Source = "DesktopScaleProvider";

    private static readonly IntPtr Panel = new(0x1111);
    private static readonly IntPtr WorkerW = new(0x2222);

    private readonly Mock<INativeApi> _native = new();
    private readonly Mock<IDesktopIntegrationService> _integration = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly DesktopScaleProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopScaleProviderTests"/> class: one 100 % monitor,
    /// system scale 1.0, no desktop layer host.
    /// </summary>
    public DesktopScaleProviderTests()
    {
        _native.Setup(n => n.GetSystemDpiScale()).Returns(1d);
        SetMonitors(new NativeMonitorInfo(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 96));
        _provider = new DesktopScaleProvider(_native.Object, _integration.Object, _log.Object);
    }

    /// <summary>No panel yet and a WorkerW host found → Embedded, with the host's DPI as the layer scale.</summary>
    [Fact]
    public void NoPanel_HostFound_PredictsEmbeddedWithHostDpi()
    {
        _integration.Setup(i => i.FindDesktopLayerHost()).Returns(WorkerW);
        _native.Setup(n => n.GetDpiScaleForWindow(WorkerW)).Returns(1.25);

        var model = _provider.Current;

        Assert.Equal(DesktopLayerMode.Embedded, model.Mode);
        Assert.Equal(DesktopLayerMode.Embedded, _provider.Mode);
        Assert.Equal(1.25, model.LayerScale);
        Assert.True(model.IsUniform);
    }

    /// <summary>No panel and no host (Windows 11 24H2+ layout) → TopLevel.</summary>
    [Fact]
    public void NoPanel_NoHost_PredictsTopLevel()
    {
        Assert.Equal(DesktopLayerMode.TopLevel, _provider.Current.Mode);
    }

    /// <summary>A reported live panel whose root differs from itself → Embedded by fact, root's DPI; no prediction needed.</summary>
    [Fact]
    public void ReportedPanel_RootDiffers_IsEmbeddedByFact()
    {
        SetPanelRoot(WorkerW);
        _native.Setup(n => n.GetDpiScaleForWindow(WorkerW)).Returns(1.5);

        _provider.ReportPanelWindow(Panel);

        Assert.Equal(DesktopLayerMode.Embedded, _provider.Current.Mode);
        Assert.Equal(1.5, _provider.Current.LayerScale);
        _integration.Verify(i => i.FindDesktopLayerHost(), Times.Never);
    }

    /// <summary>A reported live panel that is its own root → TopLevel even when a host would be found.</summary>
    [Fact]
    public void ReportedPanel_RootIsSelf_IsTopLevelByFact()
    {
        _integration.Setup(i => i.FindDesktopLayerHost()).Returns(WorkerW);
        SetPanelRoot(Panel);

        _provider.ReportPanelWindow(Panel);

        Assert.Equal(DesktopLayerMode.TopLevel, _provider.Current.Mode);
    }

    /// <summary>When the fact contradicts the earlier prediction, the fact wins and a mismatch is logged.</summary>
    [Fact]
    public void Fact_ContradictingPrediction_WinsAndWarns()
    {
        _integration.Setup(i => i.FindDesktopLayerHost()).Returns(WorkerW);
        Assert.Equal(DesktopLayerMode.Embedded, _provider.Current.Mode);

        SetPanelRoot(Panel);
        _provider.ReportPanelWindow(Panel);

        Assert.Equal(DesktopLayerMode.TopLevel, _provider.Current.Mode);
        _log.Verify(l => l.Warn(Source, "DesktopLayerModeMismatch", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A monitor without an effective DPI (0) gets the system scale.</summary>
    [Fact]
    public void EffectiveDpiZero_FallsBackToSystemScale()
    {
        _native.Setup(n => n.GetSystemDpiScale()).Returns(1.5);
        SetMonitors(
            new NativeMonitorInfo(Display1, new Rectangle(0, 0, 2880, 1620), new Rectangle(0, 0, 2880, 1560), true),
            new NativeMonitorInfo(Display2, new Rectangle(2880, 0, 1920, 1080), new Rectangle(2880, 0, 1920, 1040), false, 96));

        var model = _provider.Current;

        Assert.Equal(1.5, model.Monitors.Single(m => m.DeviceName == Display1).Scale);
        Assert.Equal(1d, model.Monitors.Single(m => m.DeviceName == Display2).Scale);
        Assert.False(model.IsUniform);
    }

    /// <summary>A native failure during a refresh keeps the previous snapshot and raises nothing.</summary>
    [Fact]
    public void NativeFailure_KeepsPreviousSnapshot()
    {
        var before = _provider.Current;
        var raised = 0;
        _provider.Changed += (_, _) => raised++;
        _native.Setup(n => n.GetMonitorInfos()).Throws(new InvalidOperationException("boom"));

        _provider.Refresh();

        Assert.Same(before, _provider.Current);
        Assert.Equal(0, raised);
        _log.Verify(l => l.Error(Source, "RefreshFailed", It.IsAny<Exception>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A failure on the very first build yields the uniform top-level system-scale snapshot (0.6.1 equivalent).</summary>
    [Fact]
    public void FailureOnFirstBuild_FallsBackToUniformSystemScale()
    {
        _native.Setup(n => n.GetSystemDpiScale()).Returns(1.25);
        _native.Setup(n => n.GetMonitorInfos()).Throws(new InvalidOperationException("boom"));

        var model = _provider.Current;

        Assert.Equal(DesktopLayerMode.TopLevel, model.Mode);
        Assert.True(model.IsUniform);
        Assert.Equal(1.25, model.UniformScale);
    }

    /// <summary>An empty monitor enumeration after a good snapshot keeps the good one.</summary>
    [Fact]
    public void EmptyMonitorList_KeepsPreviousSnapshot()
    {
        var before = _provider.Current;
        SetMonitors();

        _provider.Refresh();

        Assert.Same(before, _provider.Current);
        _log.Verify(l => l.Warn(Source, "NoMonitorsKeptPreviousSnapshot", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary><see cref="IDesktopScaleProvider.Changed"/> is raised only when the snapshot really changed.</summary>
    [Fact]
    public void Changed_RaisedOnlyOnRealDifference()
    {
        _ = _provider.Current;
        var events = new List<DesktopScaleChangedEventArgs>();
        _provider.Changed += (_, e) => events.Add(e);

        _provider.Refresh();
        Assert.Empty(events);

        SetMonitors(new NativeMonitorInfo(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 144));
        _provider.Refresh();

        var e = Assert.Single(events);
        Assert.Equal(1d, e.Old.UniformScale);
        Assert.Equal(1.5, e.New.UniformScale);
        Assert.Same(_provider.Current, e.New);
        Assert.False(e.ModeChanged);
    }

    /// <summary>The first build (lazy <see cref="IDesktopScaleProvider.Current"/>) never raises Changed.</summary>
    [Fact]
    public void FirstBuild_DoesNotRaiseChanged()
    {
        var raised = 0;
        _provider.Changed += (_, _) => raised++;

        _provider.Refresh();

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Explorer restart: the embedded panel died and no WorkerW is found any more → the next refresh
    /// switches to TopLevel, raises Changed with <see cref="DesktopScaleChangedEventArgs.ModeChanged"/> and logs it.
    /// </summary>
    [Fact]
    public void ModeChange_AfterExplorerRestart_RaisesChangedAndLogs()
    {
        SetPanelRoot(WorkerW);
        _native.Setup(n => n.GetDpiScaleForWindow(WorkerW)).Returns(1d);
        _provider.ReportPanelWindow(Panel);
        Assert.Equal(DesktopLayerMode.Embedded, _provider.Mode);

        DesktopScaleChangedEventArgs? args = null;
        _provider.Changed += (_, e) => args = e;
        _native.Setup(n => n.IsWindow(Panel)).Returns(false);

        _provider.Refresh();

        Assert.Equal(DesktopLayerMode.TopLevel, _provider.Mode);
        Assert.NotNull(args);
        Assert.True(args.ModeChanged);
        _log.Verify(l => l.Info(Source, "DesktopLayerModeChanged", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>An older live panel still provides the fact after the newest reported one died.</summary>
    [Fact]
    public void OlderLivePanel_StillProvidesFact()
    {
        var older = new IntPtr(0x3333);
        _native.Setup(n => n.IsWindow(older)).Returns(true);
        _native.Setup(n => n.GetRootWindow(older)).Returns(WorkerW);
        SetPanelRoot(WorkerW);
        _provider.ReportPanelWindow(older);
        _provider.ReportPanelWindow(Panel);

        _native.Setup(n => n.IsWindow(Panel)).Returns(false);
        _provider.Refresh();

        Assert.Equal(DesktopLayerMode.Embedded, _provider.Mode);
        _integration.Verify(i => i.FindDesktopLayerHost(), Times.Never);
    }

    /// <summary><see cref="IntPtr.Zero"/> is ignored — no native call at all.</summary>
    [Fact]
    public void ReportPanelWindow_Zero_IsIgnored()
    {
        _provider.ReportPanelWindow(IntPtr.Zero);

        _native.Verify(n => n.GetMonitorInfos(), Times.Never);
    }

    /// <summary>A throwing Changed subscriber is contained and logged.</summary>
    [Fact]
    public void ThrowingSubscriber_IsContained()
    {
        _ = _provider.Current;
        _provider.Changed += (_, _) => throw new InvalidOperationException("subscriber");
        SetMonitors(new NativeMonitorInfo(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 120));

        var ex = Record.Exception(_provider.Refresh);

        Assert.Null(ex);
        _log.Verify(l => l.Error(Source, "ChangedHandlerFailed", It.IsAny<Exception>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>An anchoring overlap is logged once while it persists.</summary>
    [Fact]
    public void AnchorOverlap_IsWarnedOnce()
    {
        _native.Setup(n => n.GetSystemDpiScale()).Returns(1.5);
        SetMonitors(
            new NativeMonitorInfo(Display1, new Rectangle(0, 0, 2880, 1620), new Rectangle(0, 0, 2880, 1560), true, 144),
            new NativeMonitorInfo(Display2, new Rectangle(2880, 0, 1920, 1080), new Rectangle(2880, 0, 1920, 1040), false, 96),
            new NativeMonitorInfo(@"\\.\DISPLAY3", new Rectangle(4800, 0, 1920, 1080), new Rectangle(4800, 0, 1920, 1040), false, 144));

        Assert.True(_provider.Current.IsAnchorOverlapFallback);
        _provider.Refresh();

        _log.Verify(l => l.Warn(Source, "DesktopScaleAnchorOverlap", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    // ── BeginBatch (M10, release-hardening 1.0 review) ──────────────────────────────────────────────

    /// <summary>
    /// While a <see cref="IDesktopScaleProvider.BeginBatch"/> scope is open, neither
    /// <see cref="IDesktopScaleProvider.Refresh"/> nor <see cref="IDesktopScaleProvider.ReportPanelWindow"/>
    /// rebuilds the snapshot or raises <see cref="IDesktopScaleProvider.Changed"/> — disposing the scope
    /// performs exactly one rebuild (reflecting every change made during the batch) and raises
    /// <see cref="IDesktopScaleProvider.Changed"/> exactly once.
    /// </summary>
    [Fact]
    public void BeginBatch_SuppressesRebuildAndChanged_UntilDisposed_ThenExactlyOne()
    {
        _ = _provider.Current; // baseline build, outside any batch.
        var events = new List<DesktopScaleChangedEventArgs>();
        _provider.Changed += (_, e) => events.Add(e);

        using (_provider.BeginBatch())
        {
            SetMonitors(new NativeMonitorInfo(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 144));
            _provider.Refresh();
            Assert.Empty(events); // no rebuild/Changed yet — still batched.

            SetPanelRoot(WorkerW);
            _provider.ReportPanelWindow(Panel);
            Assert.Empty(events); // a second dirty-marking call inside the same batch changes nothing.
        }

        var e = Assert.Single(events); // exactly one rebuild+Changed, once the scope closed.
        Assert.Equal(DesktopLayerMode.Embedded, e.New.Mode); // reflects EVERY change made during the batch.
        Assert.Same(_provider.Current, e.New);
    }

    /// <summary>
    /// A batch scope that never actually marks anything dirty (no <see cref="IDesktopScaleProvider.Refresh"/>/
    /// <see cref="IDesktopScaleProvider.ReportPanelWindow"/> call inside it) costs no rebuild at all when
    /// disposed.
    /// </summary>
    [Fact]
    public void BeginBatch_NothingMarkedDirty_DisposeNeverRebuilds()
    {
        _ = _provider.Current;
        var raised = 0;
        _provider.Changed += (_, _) => raised++;

        using (_provider.BeginBatch())
        {
            // Nothing happens here — closes immediately.
        }

        Assert.Equal(0, raised);
        _native.Verify(n => n.GetMonitorInfos(), Times.Once); // only the baseline build above.
    }

    /// <summary>
    /// Nested/re-entrant batch scopes share one dirty flag: disposing an INNER scope never rebuilds —
    /// only disposing the OUTERMOST one does, and it performs exactly one rebuild reflecting every change
    /// made across both scopes.
    /// </summary>
    [Fact]
    public void BeginBatch_Nested_OnlyOutermostDisposeRebuilds()
    {
        _ = _provider.Current;
        var events = new List<DesktopScaleChangedEventArgs>();
        _provider.Changed += (_, e) => events.Add(e);

        using (_provider.BeginBatch())
        {
            using (_provider.BeginBatch())
            {
                SetMonitors(new NativeMonitorInfo(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 144));
                _provider.Refresh();
            }

            // The inner scope's Dispose above must not have rebuilt — still nothing observed.
            Assert.Empty(events);
        }

        Assert.Single(events); // only the outer scope's Dispose actually rebuilt.
    }

    private void SetMonitors(params NativeMonitorInfo[] monitors)
        => _native.Setup(n => n.GetMonitorInfos()).Returns(monitors);

    private void SetPanelRoot(IntPtr root)
    {
        _native.Setup(n => n.IsWindow(Panel)).Returns(true);
        _native.Setup(n => n.GetRootWindow(Panel)).Returns(root);
    }
}
