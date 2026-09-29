using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="SystemThemeProvider"/> (design system 1.x, phase 6.1, plan §5 T-8) against a
/// mocked <see cref="INativeApi"/>. No real window or message loop: the <see cref="NativeWindowProc"/>
/// handed to <see cref="INativeApi.CreateMessageWindow"/> is captured and invoked directly (same technique
/// as <see cref="ExplorerRestartMonitorTests"/>), and the debounce <c>WM_TIMER</c> is delivered by hand.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SystemThemeProviderTests
{
    private static readonly IntPtr ListenerHwnd = new(0x2345);

    private readonly Mock<INativeApi> _nativeApi = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly SystemThemeProvider _sut;
    private readonly List<SystemThemeSnapshot> _raised = [];

    // COLOR_* index -> COLORREF; starts as a plausible light palette (every value distinct).
    private readonly Dictionary<int, int> _colors = new()
    {
        [5] = 0xFFFFFF, // COLOR_WINDOW
        [8] = 0x000000, // COLOR_WINDOWTEXT
        [13] = 0xD77800, // COLOR_HIGHLIGHT
        [14] = 0xFFFFFE, // COLOR_HIGHLIGHTTEXT
        [15] = 0xF0F0F0, // COLOR_BTNFACE
        [18] = 0x000001, // COLOR_BTNTEXT
        [17] = 0x6D6D6D, // COLOR_GRAYTEXT
        [4] = 0xF0F0F1, // COLOR_MENU
        [22] = 0xE3E3E3, // COLOR_3DLIGHT
    };

    private NativeWindowProc? _wndProc;
    private int? _apps = 1;
    private int? _system = 1;
    private bool _highContrast;
    private double _messageFontSize = 12;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemThemeProviderTests"/> class with a happy-path
    /// native layer: light/light, no high contrast, window created, timers start.
    /// </summary>
    public SystemThemeProviderTests()
    {
        _nativeApi
            .Setup(api => api.ReadCurrentUserDword(SystemThemeProvider.PersonalizeKeyPath, SystemThemeProvider.AppsUseLightThemeValueName))
            .Returns(() => _apps);
        _nativeApi
            .Setup(api => api.ReadCurrentUserDword(SystemThemeProvider.PersonalizeKeyPath, SystemThemeProvider.SystemUsesLightThemeValueName))
            .Returns(() => _system);
        _nativeApi.Setup(api => api.IsHighContrastOn()).Returns(() => _highContrast);
        _nativeApi.Setup(api => api.GetSystemColor(It.IsAny<int>())).Returns((int index) => _colors.GetValueOrDefault(index));
        _nativeApi.Setup(api => api.GetMessageFontSize()).Returns(() => _messageFontSize);
        _nativeApi
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Callback<string, NativeWindowProc>((_, proc) => _wndProc = proc)
            .Returns(new NativeMessageWindow(ListenerHwnd, 43, 0));
        _nativeApi.Setup(api => api.SetTimer(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<uint>())).Returns(true);

        _sut = new SystemThemeProvider(_nativeApi.Object, _log.Object);
        _sut.Changed += (_, snapshot) => _raised.Add(snapshot);
    }

    /// <summary>Registry values map to the snapshot: 0 = dark, non-zero = light, absent = light; an absent Windows mode follows the app mode.</summary>
    /// <param name="apps">Raw <c>AppsUseLightTheme</c> (null = absent).</param>
    /// <param name="system">Raw <c>SystemUsesLightTheme</c> (null = absent).</param>
    /// <param name="expectedApps">Expected <see cref="SystemThemeSnapshot.AppsUseLightTheme"/>.</param>
    /// <param name="expectedSystem">Expected <see cref="SystemThemeSnapshot.SystemUsesLightTheme"/>.</param>
    [Theory]
    [InlineData(1, 1, true, true)]
    [InlineData(0, 0, false, false)]
    [InlineData(0, 1, false, true)]
    [InlineData(1, 0, true, false)]
    [InlineData(null, null, true, true)]
    [InlineData(null, 0, true, false)]
    [InlineData(0, null, false, false)]
    [InlineData(1, null, true, true)]
    [InlineData(2, -1, true, true)]
    public void Current_MapsRegistryValues(int? apps, int? system, bool expectedApps, bool expectedSystem)
    {
        _apps = apps;
        _system = system;

        var current = _sut.Current;

        Assert.Equal(expectedApps, current.AppsUseLightTheme);
        Assert.Equal(expectedSystem, current.SystemUsesLightTheme);
        Assert.False(current.HighContrast);
    }

    /// <summary>The high-contrast flag comes from the live SPI query.</summary>
    [Fact]
    public void Current_ReportsHighContrast()
    {
        _highContrast = true;

        Assert.True(_sut.Current.HighContrast);
    }

    /// <summary>Before Start the snapshot is read live on each access (no listener to keep a cache fresh).</summary>
    [Fact]
    public void Current_BeforeStart_ReadsLive()
    {
        Assert.True(_sut.Current.AppsUseLightTheme);

        _apps = 0;

        Assert.False(_sut.Current.AppsUseLightTheme);
        Assert.Empty(_raised);
    }

    /// <summary>Start reads the baseline, creates its own listener window and logs the initial theme.</summary>
    [Fact]
    public void Start_CreatesListenerWindow_AndLogsBaseline()
    {
        _sut.Start();

        Assert.True(_sut.IsListening);
        _nativeApi.Verify(
            api => api.CreateMessageWindow(It.Is<string>(name => name.StartsWith("DesktopOrganizer.ThemeWatcher.", StringComparison.Ordinal)), It.IsAny<NativeWindowProc>()),
            Times.Once);
        _log.Verify(l => l.Info("SystemThemeProvider", "SystemThemeInitial", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        Assert.Empty(_raised);
    }

    /// <summary>A second Start is a no-op — one listener window per instance.</summary>
    [Fact]
    public void Start_IsIdempotent()
    {
        _sut.Start();
        _sut.Start();

        _nativeApi.Verify(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()), Times.Once);
    }

    /// <summary>
    /// A settings broadcast only (re)starts the debounce timer; the snapshot is re-read and the event raised
    /// when the timer fires — once, with the new snapshot, and the timer is killed.
    /// </summary>
    [Fact]
    public void SettingChange_Debounced_RaisesOnceOnTimer()
    {
        _sut.Start();
        _apps = 0;

        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);
        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);
        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSysColorChange, IntPtr.Zero, IntPtr.Zero);

        Assert.Empty(_raised);
        _nativeApi.Verify(
            api => api.SetTimer(ListenerHwnd, SystemThemeProvider.DebounceTimerId, SystemThemeProvider.DebounceIntervalMs),
            Times.Exactly(3));

        FireDebounceTimer();

        var snapshot = Assert.Single(_raised);
        Assert.Equal(Expected(appsUseLightTheme: false, systemUsesLightTheme: true, highContrast: false), snapshot);
        Assert.Equal(snapshot, _sut.Current);
        _nativeApi.Verify(api => api.KillTimer(ListenerHwnd, SystemThemeProvider.DebounceTimerId), Times.Once);
        _log.Verify(l => l.Info("SystemThemeProvider", "SystemThemeChanged", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>An unrelated broadcast (nothing actually changed) raises nothing.</summary>
    [Fact]
    public void SettingChange_NoActualChange_DoesNotRaise()
    {
        _sut.Start();

        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);
        FireDebounceTimer();

        Assert.Empty(_raised);
    }

    /// <summary>Each real change raises exactly once; a repeated tick with the same values stays silent.</summary>
    [Fact]
    public void Refresh_RaisesOnlyOnRealChange()
    {
        _sut.Start();

        _system = 0;
        Assert.True(_sut.Refresh());
        Assert.False(_sut.Refresh());

        _system = 1;
        Assert.True(_sut.Refresh());

        Assert.Equal(2, _raised.Count);
        Assert.False(_raised[0].SystemUsesLightTheme);
        Assert.True(_raised[1].SystemUsesLightTheme);
    }

    /// <summary>A high-contrast switch alone is a change.</summary>
    [Fact]
    public void HighContrastSwitch_RaisesChanged()
    {
        _sut.Start();
        _highContrast = true;

        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSysColorChange, IntPtr.Zero, IntPtr.Zero);
        FireDebounceTimer();

        Assert.True(Assert.Single(_raised).HighContrast);
    }

    /// <summary>A theme (visual style) change also schedules a re-read.</summary>
    [Fact]
    public void ThemeChanged_SchedulesRefresh()
    {
        _sut.Start();
        _apps = 0;
        _system = 0;

        _wndProc!(ListenerHwnd, SystemThemeProvider.WmThemeChanged, IntPtr.Zero, IntPtr.Zero);
        FireDebounceTimer();

        Assert.Equal(Expected(appsUseLightTheme: false, systemUsesLightTheme: false, highContrast: false), Assert.Single(_raised));
    }

    /// <summary>When the debounce timer cannot be started the change is not lost — it is read immediately.</summary>
    [Fact]
    public void SettingChange_TimerUnavailable_RefreshesImmediately()
    {
        _nativeApi.Setup(api => api.SetTimer(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<uint>())).Returns(false);
        _sut.Start();
        _apps = 0;

        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);

        Assert.False(Assert.Single(_raised).AppsUseLightTheme);
    }

    /// <summary>Unrelated messages and other timer ids neither schedule nor raise; every message falls through to DefWindowProc.</summary>
    [Fact]
    public void OtherMessages_Ignored_AndFallThroughToDefWindowProc()
    {
        _nativeApi.Setup(api => api.DefaultWindowProc(ListenerHwnd, 0x007E, IntPtr.Zero, IntPtr.Zero)).Returns(new IntPtr(5));
        _sut.Start();
        _apps = 0;

        var result = _wndProc!(ListenerHwnd, 0x007E, IntPtr.Zero, IntPtr.Zero); // WM_DISPLAYCHANGE
        _wndProc!(ListenerHwnd, SystemThemeProvider.WmTimer, new IntPtr(99), IntPtr.Zero); // someone else's timer

        Assert.Equal(new IntPtr(5), result);
        Assert.Empty(_raised);
        _nativeApi.Verify(api => api.SetTimer(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<uint>()), Times.Never);
    }

    /// <summary>A registry failure inside the window procedure keeps the last snapshot, logs, and never throws.</summary>
    [Fact]
    public void RegistryThrows_KeepsLastSnapshot_LogsAndDoesNotThrow()
    {
        _sut.Start();
        _nativeApi
            .Setup(api => api.ReadCurrentUserDword(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new UnauthorizedAccessException("denied"));

        var ex = Record.Exception(() =>
        {
            _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);
            FireDebounceTimer();
        });

        Assert.Null(ex);
        Assert.Empty(_raised);
        Assert.Equal(Expected(appsUseLightTheme: true, systemUsesLightTheme: true, highContrast: false), _sut.Current);
        _log.Verify(
            l => l.Error("SystemThemeProvider", "ReadFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>Without any readable value (and before Start) the Windows default — light, no high contrast — is reported.</summary>
    [Fact]
    public void Current_RegistryThrows_BeforeStart_ReturnsDefault()
    {
        _nativeApi.Setup(api => api.IsHighContrastOn()).Throws(new InvalidOperationException("spi"));

        Assert.Equal(SystemThemeSnapshot.Default, _sut.Current);
    }

    /// <summary>A throwing subscriber is logged and does not stop the next subscriber nor escape the window procedure.</summary>
    [Fact]
    public void ThrowingSubscriber_IsIsolated()
    {
        var second = 0;
        _sut.Changed += (_, _) => throw new InvalidOperationException("subscriber");
        _sut.Changed += (_, _) => second++;
        _sut.Start();
        _apps = 0;

        var ex = Record.Exception(() =>
        {
            _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);
            FireDebounceTimer();
        });

        Assert.Null(ex);
        Assert.Single(_raised);
        Assert.Equal(1, second);
        _log.Verify(
            l => l.Error("SystemThemeProvider", "ChangedHandlerFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>Listener window creation failure degrades to "read live, no events", logs a warning, never throws.</summary>
    [Fact]
    public void Start_WindowCreationFails_DegradesAndLogs()
    {
        _nativeApi
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(IntPtr.Zero, 0, 1410));

        _sut.Start();
        _apps = 0;

        Assert.False(_sut.IsListening);
        Assert.False(_sut.Current.AppsUseLightTheme);
        _log.Verify(l => l.Warn("SystemThemeProvider", "ListenerWindowCreateFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A native exception during Start is logged, not thrown.</summary>
    [Fact]
    public void Start_NativeThrows_LogsError_DoesNotThrow()
    {
        _nativeApi
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Throws(new InvalidOperationException("boom"));

        var ex = Record.Exception(_sut.Start);

        Assert.Null(ex);
        Assert.False(_sut.IsListening);
        _log.Verify(
            l => l.Error("SystemThemeProvider", "StartFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>Dispose kills the timer, destroys the window, drops subscribers, and is idempotent; Start after Dispose is inert.</summary>
    [Fact]
    public void Dispose_ReleasesWindow_Idempotent_AndBlocksRestart()
    {
        _sut.Start();

        _sut.Dispose();
        _sut.Dispose();
        _sut.Start();

        Assert.False(_sut.IsListening);
        _nativeApi.Verify(api => api.KillTimer(ListenerHwnd, SystemThemeProvider.DebounceTimerId), Times.Once);
        _nativeApi.Verify(api => api.DestroyMessageWindow(It.Is<NativeMessageWindow>(w => w.Handle == ListenerHwnd)), Times.Once);
        _nativeApi.Verify(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()), Times.Once);
    }

    /// <summary>The pure mapping helper follows the documented fallback chain.</summary>
    [Fact]
    public void MapSnapshot_FallbackChain()
    {
        Assert.Equal(new SystemThemeSnapshot(true, true, true), SystemThemeProvider.MapSnapshot(null, null, highContrast: true));
        Assert.Equal(new SystemThemeSnapshot(false, false), SystemThemeProvider.MapSnapshot(0, null, highContrast: false));
    }

    /// <summary>
    /// A switch from one high-contrast theme to another (Aquatic → Desert) leaves every flag untouched and
    /// changes only the system colours — the fingerprint makes it a change (review 6.1, M-1).
    /// </summary>
    [Fact]
    public void HighContrastToHighContrast_ColoursOnly_RaisesChanged()
    {
        _highContrast = true;
        _sut.Start();
        var before = _sut.Current;

        _colors[5] = 0x1A1A1A; // COLOR_WINDOW
        _colors[8] = 0x00FFFF; // COLOR_WINDOWTEXT
        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSysColorChange, IntPtr.Zero, IntPtr.Zero);
        FireDebounceTimer();

        var after = Assert.Single(_raised);
        Assert.True(after.HighContrast);
        Assert.Equal(before with { SystemColorsFingerprint = after.SystemColorsFingerprint }, after);
        Assert.NotEqual(before.SystemColorsFingerprint, after.SystemColorsFingerprint);
    }

    /// <summary>A system text-size change (<c>SPI_SETNONCLIENTMETRICS</c>) alone is a change (review 6.1, M-1/M-2).</summary>
    [Fact]
    public void MessageFontSizeChange_RaisesChanged_WithNewSize()
    {
        _sut.Start();

        _messageFontSize = 15;
        _wndProc!(ListenerHwnd, SystemThemeProvider.WmSettingChange, IntPtr.Zero, IntPtr.Zero);
        FireDebounceTimer();

        Assert.Equal(15d, Assert.Single(_raised).MessageFontSize);
    }

    /// <summary>A colour outside the fingerprinted set changing does not raise anything.</summary>
    [Fact]
    public void UnfingerprintedColourChange_DoesNotRaise()
    {
        _sut.Start();

        _colors[1] = 0x123456; // COLOR_BACKGROUND — not part of the fingerprint

        Assert.False(_sut.Refresh());
        Assert.Empty(_raised);
    }

    /// <summary>The snapshot reads every fingerprinted colour live, plus the message font size.</summary>
    [Fact]
    public void Current_ReadsFingerprintColoursAndMessageFont()
    {
        var current = _sut.Current;

        foreach (var index in SystemThemeProvider.FingerprintColorIndices)
            _nativeApi.Verify(api => api.GetSystemColor(index), Times.Once);

        Assert.Equal(12d, current.MessageFontSize);
        Assert.Equal(
            SystemThemeProvider.ComputeColorsFingerprint(SystemThemeProvider.FingerprintColorIndices.Select(i => _colors[i])),
            current.SystemColorsFingerprint);
    }

    /// <summary>The fingerprint is deterministic, order-sensitive and 0 for an all-zero (unread) palette.</summary>
    [Fact]
    public void ComputeColorsFingerprint_DeterministicAndOrderSensitive()
    {
        Assert.Equal(0, SystemThemeProvider.ComputeColorsFingerprint([0, 0, 0]));
        Assert.Equal(
            SystemThemeProvider.ComputeColorsFingerprint([0xFFFFFF, 0x000000]),
            SystemThemeProvider.ComputeColorsFingerprint([0xFFFFFF, 0x000000]));
        Assert.NotEqual(
            SystemThemeProvider.ComputeColorsFingerprint([0xFFFFFF, 0x000000]),
            SystemThemeProvider.ComputeColorsFingerprint([0x000000, 0xFFFFFF]));
    }

    /// <summary>
    /// Source compatibility of the contract: the 2- and 3-argument constructions keep working and default
    /// the new members to "unknown" (0), so they still equal <see cref="SystemThemeSnapshot.Default"/>.
    /// </summary>
    [Fact]
    public void Snapshot_LegacyConstructions_DefaultNewMembersToZero()
    {
        var twoArgs = new SystemThemeSnapshot(true, true);
        var threeArgs = new SystemThemeSnapshot(true, true, HighContrast: false);

        Assert.Equal(0, twoArgs.SystemColorsFingerprint);
        Assert.Equal(0d, twoArgs.MessageFontSize);
        Assert.Equal(SystemThemeSnapshot.Default, twoArgs);
        Assert.Equal(SystemThemeSnapshot.Default, threeArgs);
        Assert.NotEqual(twoArgs, twoArgs with { SystemColorsFingerprint = 1 });
        Assert.NotEqual(twoArgs, twoArgs with { MessageFontSize = 12 });
    }

    /// <summary>The expected snapshot for the given flags with the fixture's current colours and font size.</summary>
    private SystemThemeSnapshot Expected(bool appsUseLightTheme, bool systemUsesLightTheme, bool highContrast)
        => new(
            appsUseLightTheme,
            systemUsesLightTheme,
            highContrast,
            SystemThemeProvider.ComputeColorsFingerprint(SystemThemeProvider.FingerprintColorIndices.Select(i => _colors.GetValueOrDefault(i))),
            _messageFontSize);

    private void FireDebounceTimer()
        => _wndProc!(ListenerHwnd, SystemThemeProvider.WmTimer, SystemThemeProvider.DebounceTimerId, IntPtr.Zero);
}
