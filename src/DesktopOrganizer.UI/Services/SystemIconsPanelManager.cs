using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages the lifecycle, persistence, Z-order and virtual-desktop placement of the system icons
/// panel. The window itself is a plain top-level window (ADR-019) — this class applies the
/// desktop-window styles right after showing it and, depending on
/// <see cref="SystemIconsPanelModel.ShowOnAllVirtualDesktops"/>, either follows the active virtual
/// desktop or pins the panel to its anchor desktop.
/// <para>
/// Two independent <see cref="DispatcherTimer"/>s run here and must not be conflated. The Z-order
/// timer keeps pushing the panel behind ordinary application windows (the panel is not
/// <c>Topmost</c>; see <c>SystemIconsPanelWindow.xaml</c>) and runs whenever the panel is switched on
/// and a window exists — in <b>both</b> states of the "show on all virtual desktops" checkbox. The
/// virtual desktop timer only runs while that checkbox is on.
/// </para>
/// </summary>
internal sealed class SystemIconsPanelManager : ISystemIconsPanelManager, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Poll interval for the "follow the active virtual desktop" feature. One second is the same
    /// order of magnitude as the Recycle Bin poll (ADR-007) and keeps the worst-case lag after a
    /// desktop switch to a single tick. A WinEvent hook was rejected on purpose: a second global
    /// hook in a process that already owns WH_MOUSE_LL is not worth one second of latency.
    /// </summary>
    private static readonly TimeSpan VirtualDesktopPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Interval at which the panel is pushed back to the bottom of the Z-order. Shorter than
    /// <see cref="VirtualDesktopPollInterval"/> on purpose: this tick is what the user actually sees.
    /// Windows re-shuffles the Z-order of ordinary top-level windows on every activation, so between
    /// two ticks the panel can sit visibly on top of the window the user just switched to; a full
    /// second of that reads as a flash of misplaced UI, ~300 ms reads as "it was never there".
    /// <para>
    /// Affordable because the work per tick is two read-only window lookups
    /// (<c>FindWindow("Progman")</c> + <c>GetWindow(GW_HWNDPREV)</c>, both cheap in-kernel reads) and,
    /// only when the panel has actually drifted, one <c>SetWindowPos</c> on our own window — in the
    /// steady state <see cref="IDesktopIntegrationService.SendToBackOfZOrder"/> finds the panel
    /// already in place and writes nothing. No <c>0x052C</c> message, no <c>EnumWindows</c>, no COM,
    /// nothing that blocks on Explorer the way <c>EmbedInDesktop</c>'s <c>SendMessage 0x052C</c> does.
    /// As with the virtual desktop poll, a <c>SetWinEventHook</c> on foreground changes was rejected
    /// on purpose: a second global hook in a process that already owns <c>WH_MOUSE_LL</c> is not worth
    /// saving three cheap calls a second.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ZOrderReassertInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Circuit breaker threshold for <see cref="PollVirtualDesktopOnce"/> (ON, "show on all virtual
    /// desktops"). <c>MoveWindowToDesktop</c> answers <c>S_OK</c> even for windows the shell does not
    /// track, where it is a silent no-op (measured, ADR-016 §E0.2). After this many consecutive "the
    /// call succeeded but the window still is not on the active desktop" rounds the feature is
    /// declared inapplicable to this window, the poll timer is stopped, and
    /// <see cref="SystemIconsPanelModel.VirtualDesktopFeatureIneffective"/> is raised — otherwise the
    /// app would ask the shell to do the impossible once a second, forever.
    /// <see cref="EnforceAnchorDesktopOnce"/> (OFF) shares the same <see cref="_ineffectiveMoveCount"/>
    /// field but is event-driven, not timer-driven, and never raises that flag (M1) — it uses
    /// <see cref="SelfHealMinConsecutiveFailures"/> instead, a lower threshold, to gate its own
    /// self-heal (see the self-heal comment there).
    /// </summary>
    private const int MaxIneffectiveMoves = 3;

    /// <summary>
    /// Minimum number of consecutive anchor-move failures required before
    /// <see cref="EnforceAnchorDesktopOnce"/>'s self-heal (§3.3) is allowed to overwrite
    /// <see cref="SystemIconsPanelModel.AnchorVirtualDesktopId"/> (M3). Deliberately lower than
    /// <see cref="MaxIneffectiveMoves"/> and requires more than a single failure: a lone refusal or
    /// no-op is not enough evidence that the anchor desktop was deleted (vs. a transient shell hiccup)
    /// to justify silently discarding the user's real anchor.
    /// </summary>
    private const int SelfHealMinConsecutiveFailures = 2;

    private readonly IAppSettings _appSettings;
    private readonly IDesktopIntegrationService _desktopIntegration;
    private readonly IMonitorService _monitorService;
    private readonly IVirtualDesktopService _virtualDesktopService;
    private readonly Func<SystemIconsPanelWindow> _windowFactory;

    private SystemIconsPanelWindow? _window;
    private DispatcherTimer? _virtualDesktopTimer;
    private DispatcherTimer? _zOrderTimer;
    private int _ineffectiveMoveCount;

    /// <summary>
    /// Initializes a new instance of <see cref="SystemIconsPanelManager"/>.
    /// </summary>
    public SystemIconsPanelManager(
        IAppSettings appSettings,
        IDesktopIntegrationService desktopIntegration,
        IMonitorService monitorService,
        IVirtualDesktopService virtualDesktopService,
        Func<SystemIconsPanelWindow> windowFactory)
    {
        _appSettings = appSettings;
        _desktopIntegration = desktopIntegration;
        _monitorService = monitorService;
        _virtualDesktopService = virtualDesktopService;
        _windowFactory = windowFactory;

        _monitorService.MonitorsChanged += OnMonitorsChanged;
    }

    /// <summary>
    /// Exposed for tests: whether the virtual desktop poll timer is currently running.
    /// The timer must only run while the setting is on, the panel window exists and the
    /// virtual desktop manager is available.
    /// </summary>
    internal bool IsVirtualDesktopTimerRunning => _virtualDesktopTimer is { IsEnabled: true };

    /// <summary>
    /// Exposed for tests: whether the Z-order timer is currently running. Unlike
    /// <see cref="IsVirtualDesktopTimerRunning"/> this one is independent of
    /// <see cref="SystemIconsPanelModel.ShowOnAllVirtualDesktops"/> — it only needs the panel to be
    /// switched on and a live window to push back.
    /// </summary>
    internal bool IsZOrderTimerRunning => _zOrderTimer is { IsEnabled: true };

    /// <summary>
    /// Exposed for tests: number of consecutive ineffective virtual-desktop placement attempts.
    /// Shared by both placement seams — incremented by <see cref="PollVirtualDesktopOnce"/> (ON,
    /// "show on all virtual desktops": the shell accepted the move but the panel still was not
    /// reported on the active desktop) and by <see cref="EnforceAnchorDesktopOnce"/> (OFF: the panel
    /// could not be placed back on its anchor desktop, whether refused outright or accepted as a
    /// silent no-op) — reset to zero by either seam's next success.
    /// </summary>
    internal int IneffectiveMoveCount => _ineffectiveMoveCount;

    /// <inheritdoc />
    public SystemIconsPanelModel Model { get; } = new();

    /// <inheritdoc />
    public void Load()
    {
        var json = _appSettings.SystemIconsPanel;
        if (string.IsNullOrWhiteSpace(json))
        {
            // Use built-in defaults already set in Model
            return;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PanelDto>(json, JsonOptions);
            if (dto is null) return;

            Model.IsEnabled = dto.IsEnabled;

            // Key absent in settings written by older builds -> stays false. No migration needed.
            Model.ShowOnAllVirtualDesktops = dto.ShowOnAllVirtualDesktops;

            if (Enum.TryParse<SystemIconSize>(dto.IconSize, out var size))
            {
                Model.IconSize = size;
            }

            // Parsed separately from the surrounding try/catch's implicit fallback: Load() wraps the
            // whole deserialisation in one catch, so a corrupt anchor typed as Guid? would have
            // discarded EVERY panel setting. TryParse never throws, so a garbage or absent string
            // degrades to "no anchor" without touching anything else that was read above.
            Model.AnchorVirtualDesktopId =
                Guid.TryParse(dto.AnchorVirtualDesktopId, out var anchor) ? anchor : null;

            if (dto.Items is { Count: > 0 })
            {
                foreach (var itemDto in dto.Items)
                {
                    var existing = Model.Items.FirstOrDefault(i => i.Kind == itemDto.Kind);
                    if (existing is null) continue;
                    existing.IsVisible = itemDto.IsVisible;
                    existing.Order = itemDto.Order;
                }

                // Re-sort by persisted order
                var sorted = Model.Items.OrderBy(i => i.Order).ToList();
                Model.Items.Clear();
                foreach (var item in sorted)
                {
                    Model.Items.Add(item);
                }
            }
        }
        catch
        {
            // Malformed JSON — silently fall back to current (default) state
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        var dto = new PanelDto
        {
            IsEnabled = Model.IsEnabled,
            ShowOnAllVirtualDesktops = Model.ShowOnAllVirtualDesktops,
            IconSize = Model.IconSize.ToString(),
            AnchorVirtualDesktopId = Model.AnchorVirtualDesktopId?.ToString(),
            Items = Model.Items.Select((item, _) => new ItemDto
            {
                Kind = item.Kind,
                IsVisible = item.IsVisible,
                Order = item.Order,
            }).ToList(),
        };

        _appSettings.SystemIconsPanel = JsonSerializer.Serialize(dto, JsonOptions);
        _appSettings.Save();
    }

    /// <inheritdoc />
    public void Apply()
    {
        // Reset the breaker before applying the new state (m6), matching ApplyVirtualDesktopSetting
        // below: OpenPanel() below can itself run the ON/OFF placement policy once (via
        // ApplyVirtualDesktopPlacementOnce) and increment the very counter being reset, so resetting
        // first is the only order where "an explicit user action is the retry point" (see
        // ResetVirtualDesktopCircuitBreaker) actually holds for that first attempt too.
        ResetVirtualDesktopCircuitBreaker();

        if (Model.IsEnabled)
        {
            OpenPanel();
        }
        else
        {
            ClosePanel();
        }

        UpdateVirtualDesktopTimer();
        UpdateZOrderTimer();
    }

    /// <inheritdoc />
    public void ApplyVirtualDesktopSetting()
    {
        // The "show on all virtual desktops" toggle only governs the poll timer / anchor placement.
        // Routing it through Apply() would tear the panel window down and build it again (ClosePanel
        // + OpenPanel), costing a visible flicker for a setting that never touches the window itself.
        if (_window is null)
        {
            // No live window: whether one is needed at all is Apply()'s decision, not ours.
            Apply();
            return;
        }

        // An explicit user action is the retry point for a feature that was previously found
        // inapplicable on this system.
        ResetVirtualDesktopCircuitBreaker();
        UpdateVirtualDesktopTimer();

        // The Z-order timer is deliberately NOT touched here: neither of its two inputs (the panel
        // being switched on, a live window existing) can change on this path — it only ever runs with
        // `_window is not null`, which is exactly the branch this one is in. Z-order and virtual
        // desktop placement are orthogonal concerns (see UpdateZOrderTimer).

        // ON -> OFF: pull the panel back onto its anchor desktop instead of waiting for the user to
        // notice it drifted. OFF -> ON: put it on the active desktop immediately instead of waiting
        // up to a second for the first poll tick. Both seams degrade silently on any failure.
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ApplyVirtualDesktopPlacementOnce(hwnd);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _monitorService.MonitorsChanged -= OnMonitorsChanged;
        ClosePanel();
        DisposeVirtualDesktopTimer();
        DisposeZOrderTimer();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void OpenPanel()
    {
        // Close any stale instance first (e.g. on monitor change)
        ClosePanel();

        try
        {
            _window = _windowFactory();
            PositionWindow(_window);
            _window.Show();

            // ADR-019 §R0.5: the desktop-window styles must be applied strictly AFTER Show(), never
            // from OnSourceInitialized. A top-level window that already carries WS_EX_TOOLWINDOW or
            // WS_EX_NOACTIVATE at the moment it is first shown is excluded from the shell's virtual
            // desktop tracking forever (GetWindowDesktopId -> S_OK + GUID_NULL), which would silently
            // resurrect the original "setting does nothing" bug. Applying the same bits one
            // instruction after Show() keeps the window tracked and movable.
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                // No handle to style or place on a desktop — leaving a shown-but-unstyled window
                // around would surface without TOOLWINDOW/NOACTIVATE (m4), so tear it down instead
                // of returning with a live, half-configured `_window`.
                try { _window.Close(); } catch { }
                _window = null;
                return;
            }

            _desktopIntegration.ApplyDesktopWindowStyles(hwnd);

            // Sink the freshly shown window immediately instead of leaving it wherever Show() put it
            // for up to one Z-order tick. The timer started by Apply() keeps it there afterwards.
            SendPanelToBackOnce(hwnd);

            // Run the ON/OFF virtual-desktop policy once immediately, rather than waiting up to a
            // second for the first poll tick (§4.4 "мелочь-улучшение") — before this point the
            // window was not yet tracked, so calling either seam earlier would have been a no-op.
            ApplyVirtualDesktopPlacementOnce(hwnd);
        }
        catch
        {
            // Window creation/style failure — ensure VM is disposed before nulling field.
            try { _window?.Close(); } catch { }
            _window = null;
        }
    }

    /// <summary>
    /// Runs the ON/OFF virtual-desktop placement policy (§5) once, against the given panel window
    /// handle: <see cref="PollVirtualDesktopOnce"/> while "show on all virtual desktops" is on,
    /// <see cref="EnforceAnchorDesktopOnce"/> while it is off. Both seams degrade silently, so this
    /// never throws.
    /// </summary>
    private void ApplyVirtualDesktopPlacementOnce(IntPtr hwnd)
    {
        if (Model.ShowOnAllVirtualDesktops)
        {
            PollVirtualDesktopOnce(hwnd);
        }
        else
        {
            EnforceAnchorDesktopOnce(hwnd);
        }
    }

    private void ClosePanel()
    {
        // Stopped before the window reference is dropped: the tick handlers need a live window,
        // and a timer left running against a closed panel is a leak (risk E-R4).
        StopVirtualDesktopTimer();
        StopZOrderTimer();

        if (_window is null) return;
        try { _window.Close(); }
        catch { /* ignore */ }
        _window = null;
    }

    // -------------------------------------------------------------------------
    // Z-order: keep the panel behind ordinary application windows
    // -------------------------------------------------------------------------

    /// <summary>
    /// The Z-order timer policy as a pure function, so its truth table can be unit-tested without a
    /// WPF window. Deliberately independent of
    /// <see cref="SystemIconsPanelModel.ShowOnAllVirtualDesktops"/> and of
    /// <see cref="IVirtualDesktopService.IsSupported"/>: "stay behind other windows" is a property of
    /// the panel itself, not of the virtual desktop feature, and must hold in both checkbox states
    /// and on systems with no virtual desktop manager at all.
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is switched on.</param>
    /// <param name="hasWindow">Whether a live panel window exists to push back.</param>
    /// <returns><see langword="true"/> when the Z-order timer should be running.</returns>
    internal static bool ShouldReassertZOrder(bool panelEnabled, bool hasWindow)
        => panelEnabled && hasWindow;

    /// <summary>
    /// One Z-order step, extracted from the timer so it can be unit-tested against a mocked
    /// <see cref="IDesktopIntegrationService"/>. The service itself already swallows native failures;
    /// the try/catch here covers the call boundary as well, because this runs on the UI thread from a
    /// <see cref="DispatcherTimer"/> where an escaping exception would take the app down.
    /// </summary>
    /// <param name="hwnd">Handle of the panel window; <see cref="IntPtr.Zero"/> is a no-op.</param>
    internal void SendPanelToBackOnce(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _desktopIntegration.SendToBackOfZOrder(hwnd);
        }
        catch
        {
            // Degrade silently — a panel that briefly sits too high is cosmetic, a crash is not.
        }
    }

    /// <summary>
    /// Starts or stops the Z-order timer according to <see cref="ShouldReassertZOrder"/>, reading the
    /// two inputs off the live state.
    /// </summary>
    private void UpdateZOrderTimer() => UpdateZOrderTimerCore(Model.IsEnabled, _window is not null);

    /// <summary>
    /// The timer's start/stop lifecycle with its two inputs passed in rather than read off
    /// <see cref="Model"/> and <c>_window</c>. Split out purely for testability: a unit test cannot
    /// produce a real <see cref="SystemIconsPanelWindow"/> (the factory has no WPF <c>Application</c>
    /// to build one against), so every test driving <see cref="Apply"/> hits <c>hasWindow == false</c>
    /// and the "timer is running" half of the truth table would never be exercised at all — the
    /// stop/dispose assertions would pass vacuously against a timer that was never created (M1).
    /// Production callers must go through <see cref="UpdateZOrderTimer"/>.
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is switched on.</param>
    /// <param name="hasWindow">Whether a live panel window exists to push back.</param>
    internal void UpdateZOrderTimerCore(bool panelEnabled, bool hasWindow)
    {
        if (!ShouldReassertZOrder(panelEnabled, hasWindow))
        {
            StopZOrderTimer();
            return;
        }

        if (_zOrderTimer is null)
        {
            _zOrderTimer = new DispatcherTimer { Interval = ZOrderReassertInterval };
            _zOrderTimer.Tick += OnZOrderTick;
        }

        _zOrderTimer.Start();
    }

    private void StopZOrderTimer() => _zOrderTimer?.Stop();

    private void DisposeZOrderTimer()
    {
        if (_zOrderTimer is null) return;

        _zOrderTimer.Stop();
        _zOrderTimer.Tick -= OnZOrderTick;
        _zOrderTimer = null;
    }

    private void OnZOrderTick(object? sender, EventArgs e)
    {
        if (_window is null || !Model.IsEnabled)
        {
            StopZOrderTimer();
            return;
        }

        SendPanelToBackOnce(new WindowInteropHelper(_window).Handle);
    }

    // -------------------------------------------------------------------------
    // Virtual desktop follow-me (Windows Task View, NOT physical monitors)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The poll timer policy as a pure function, so the truth table can be unit-tested without a
    /// WPF window: the timer runs only when the panel is on, the setting is on, a window exists and
    /// the shell exposes a virtual desktop manager.
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is switched on.</param>
    /// <param name="showOnAllVirtualDesktops">Whether the user asked the panel to follow them.</param>
    /// <param name="hasWindow">Whether a live panel window exists to follow.</param>
    /// <param name="virtualDesktopsSupported">Whether the virtual desktop COM object is available.</param>
    /// <returns><see langword="true"/> when the poll timer should be running.</returns>
    internal static bool ShouldPollVirtualDesktop(
        bool panelEnabled,
        bool showOnAllVirtualDesktops,
        bool hasWindow,
        bool virtualDesktopsSupported)
        => panelEnabled && showOnAllVirtualDesktops && hasWindow && virtualDesktopsSupported;

    /// <summary>
    /// Starts or stops the poll timer according to <see cref="ShouldPollVirtualDesktop"/>.
    /// Called on every <see cref="Apply"/> / <see cref="ApplyVirtualDesktopSetting"/>, which is what
    /// the settings UI triggers on each toggle.
    /// </summary>
    private void UpdateVirtualDesktopTimer()
    {
        var panelEnabled = Model.IsEnabled;
        var followSetting = Model.ShowOnAllVirtualDesktops;
        var hasWindow = _window is not null;

        // IsSupported activates the COM object on first use, so it is probed only once the three
        // cheap flags already allow polling.
        var supported = panelEnabled
                        && followSetting
                        && hasWindow
                        && _virtualDesktopService.IsSupported;

        var shouldRun = ShouldPollVirtualDesktop(panelEnabled, followSetting, hasWindow, supported);

        if (!shouldRun)
        {
            StopVirtualDesktopTimer();
            return;
        }

        if (_virtualDesktopTimer is null)
        {
            _virtualDesktopTimer = new DispatcherTimer { Interval = VirtualDesktopPollInterval };
            _virtualDesktopTimer.Tick += OnVirtualDesktopTick;
        }

        _virtualDesktopTimer.Start();
    }

    private void StopVirtualDesktopTimer() => _virtualDesktopTimer?.Stop();

    private void DisposeVirtualDesktopTimer()
    {
        if (_virtualDesktopTimer is null) return;

        _virtualDesktopTimer.Stop();
        _virtualDesktopTimer.Tick -= OnVirtualDesktopTick;
        _virtualDesktopTimer = null;
    }

    private void OnVirtualDesktopTick(object? sender, EventArgs e)
    {
        if (_window is null || !Model.IsEnabled || !Model.ShowOnAllVirtualDesktops)
        {
            StopVirtualDesktopTimer();
            return;
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        PollVirtualDesktopOnce(hwnd);
    }

    /// <summary>
    /// One poll step, extracted from the timer so it can be unit-tested against mocks:
    /// guard → move → verify the move had an effect → circuit breaker. Any failure degrades
    /// silently — this runs on the UI thread and must never surface an exception. This is the
    /// "show on all virtual desktops" ON-mode counterpart of <see cref="EnforceAnchorDesktopOnce"/>.
    /// </summary>
    /// <param name="hwnd">Handle of the panel window; <see cref="IntPtr.Zero"/> is a no-op.</param>
    internal void PollVirtualDesktopOnce(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (_virtualDesktopService.IsWindowOnCurrentDesktop(hwnd))
            {
                _ineffectiveMoveCount = 0;
                return;
            }

            if (!_virtualDesktopService.TryMoveWindowToCurrentDesktop(hwnd)) return;

            // A true result only means the shell accepted the call. On a window it does not track
            // MoveWindowToDesktop returns S_OK and does nothing at all (ADR-016 §E0.2), so the
            // effect has to be verified before anything expensive is done about it.
            if (!_virtualDesktopService.IsWindowOnCurrentDesktop(hwnd))
            {
                if (++_ineffectiveMoveCount >= MaxIneffectiveMoves)
                {
                    // Circuit breaker: on this system the documented API cannot move this window.
                    // Stop polling rather than hammering the shell once a second forever.
                    Model.VirtualDesktopFeatureIneffective = true;
                    StopVirtualDesktopTimer();
                }

                return;
            }

            _ineffectiveMoveCount = 0;

            // ADR-019: the panel is a plain top-level window, no longer re-parented into WorkerW, so
            // there is no desktop-layer parenting left to lose or restore here. A previous revision
            // of this method re-embedded the window whenever it looked "not embedded" — for a window
            // that is deliberately never embedded, that check is always true, so it re-embedded the
            // panel into WorkerW once a second and silently resurrected the original bug (V-R11).
            // Do not add an EmbedInDesktop/IsEmbedded call back here.
        }
        catch
        {
            // Degrade silently — the panel keeps working, it just stays on its own desktop.
        }
    }

    // -------------------------------------------------------------------------
    // Virtual desktop anchor ("Desktop 1"), used while ShowOnAllVirtualDesktops is off (ADR-019 §3.2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pure anchor-resolution policy (ADR-019 §3.2), unit-testable without any Win32/COM dependency.
    /// </summary>
    /// <param name="persisted">Last anchor written to settings, or <see langword="null"/>.</param>
    /// <param name="systemDefault">
    /// Result of <see cref="IVirtualDesktopService.TryGetDefaultDesktopId"/> — the authoritative
    /// "Desktop 1" probe (R2). When available it always overrides <paramref name="persisted"/>.
    /// </param>
    /// <param name="panelCurrent">
    /// The panel's own current desktop (R1 heuristic) — used only when neither of the above is
    /// available, i.e. on the very first capture with R2 unavailable.
    /// </param>
    /// <returns>
    /// The resolved anchor, or <see langword="null"/> when all three sources are
    /// <see langword="null"/> — callers must not guess in that case.
    /// </returns>
    internal static Guid? ResolveAnchorDesktopId(Guid? persisted, Guid? systemDefault, Guid? panelCurrent)
        => systemDefault ?? persisted ?? panelCurrent;

    /// <summary>
    /// Ensures the panel sits on its anchor desktop, resolving/persisting the anchor first if
    /// necessary. This is the "show on all virtual desktops" OFF-mode counterpart of
    /// <see cref="PollVirtualDesktopOnce"/>: called once per interesting event (panel opened,
    /// checkbox toggled to off, monitor configuration changed) rather than off a timer — an ordinary
    /// top-level window has no mechanism that silently pulls it to another desktop the way WorkerW
    /// parenting could, so there is nothing to continuously guard against (§5).
    /// </summary>
    /// <param name="hwnd">Handle of the panel window; <see cref="IntPtr.Zero"/> is a no-op.</param>
    internal void EnforceAnchorDesktopOnce(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Same guard PollVirtualDesktopOnce gets for free via UpdateVirtualDesktopTimer (M5): this
        // seam is invoked directly from OpenPanel/OnMonitorsChanged/ApplyVirtualDesktopSetting, none
        // of which check IsSupported themselves, so an unsupported system would otherwise still pay
        // for a registry read + COM probe on every one of those events.
        if (!_virtualDesktopService.IsSupported) return;

        try
        {
            var systemDefault = _virtualDesktopService.TryGetDefaultDesktopId();
            var panelCurrent = _virtualDesktopService.TryGetWindowDesktopId(hwnd);
            var anchor = ResolveAnchorDesktopId(Model.AnchorVirtualDesktopId, systemDefault, panelCurrent);

            // All three sources came back null (e.g. an unsupported system, or R2's cross-validation
            // rejected the registry layout) — never guess a desktop to pin the panel to.
            if (anchor is null) return;

            if (Model.AnchorVirtualDesktopId != anchor)
            {
                Model.AnchorVirtualDesktopId = anchor;
                Save();
            }

            if (panelCurrent == anchor)
            {
                // Already anchored: nothing to move, and asking the shell anyway would be a wasted
                // COM round-trip on every single call site (open panel, toggle, monitor change).
                // Reset the streak here too (M2): this method can be reached many times with any
                // number of successes and failures interleaved (open panel, toggle, monitor change),
                // and the breaker below only means something as a count of CONSECUTIVE failures on
                // this same anchor path.
                _ineffectiveMoveCount = 0;
                return;
            }

            var moved = _virtualDesktopService.TryMoveWindowToDesktop(hwnd, anchor.Value);

            // As with PollVirtualDesktopOnce, a true result only means the shell accepted the call —
            // MoveWindowToDesktop is a silent S_OK no-op for windows it does not track (ADR-016
            // §E0.2). Re-probe regardless of `moved`: an explicit refusal leaves the window wherever
            // it already was, which is exactly the input the self-heal check below needs (M3).
            var afterMove = _virtualDesktopService.TryGetWindowDesktopId(hwnd);
            if (moved && afterMove == anchor)
            {
                _ineffectiveMoveCount = 0;
                return;
            }

            _ineffectiveMoveCount++;

            // Self-heal (§3.3): an anchor that did NOT come from the authoritative R2 probe (i.e. it
            // came from a persisted value or the R1 heuristic) is the one case where "the shell will
            // not put this window on that desktop" plausibly means the anchor desktop itself was
            // deleted, not that the API refuses to move this window. (M3) This must also cover an
            // explicit refusal (`moved == false`) — the most likely shell response for a dead/deleted
            // anchor GUID — not just a silent S_OK no-op; the previous version returned before this
            // point on refusal, so the sole scenario this branch exists for could never reach it.
            // Guarded against a destructive overwrite two ways: `panelCurrent is not null` via
            // `afterMove` (the window must actually be tracked by the shell — an untracked window's
            // own "desktop" is meaningless noise) and requiring one prior failure
            // (`_ineffectiveMoveCount >= SelfHealMinConsecutiveFailures`) before trusting a single
            // result enough to silently discard the user's real anchor.
            if (systemDefault is null
                && afterMove is { } recaptured
                && _ineffectiveMoveCount >= SelfHealMinConsecutiveFailures
                && Model.AnchorVirtualDesktopId != recaptured)
            {
                // Defensive, not currently reachable-false: Model.AnchorVirtualDesktopId == anchor at
                // this point (set above, untouched since) and recaptured != anchor is guaranteed by
                // the `moved && afterMove == anchor` return above — so this is always true today.
                // Left explicit in case a future change reorders the checks above it.
                Model.AnchorVirtualDesktopId = recaptured;
                Save();
                _ineffectiveMoveCount = 0;
                return;
            }

            // Authoritative anchor (R2) still did not take, or self-heal did not apply yet (still
            // building its 2-failure streak). Unlike PollVirtualDesktopOnce (M1), this OFF-path never
            // raises `VirtualDesktopFeatureIneffective`: that banner is worded for the "follow me"
            // poll timer and would otherwise appear under a checkbox the user may have just switched
            // OFF, for a feature that keeps working fine in ON-mode. `_ineffectiveMoveCount` still
            // tracks the streak so a later consecutive success (either return path above) resets it.
        }
        catch
        {
            // Degrade silently — same policy as PollVirtualDesktopOnce; this can run from OpenPanel,
            // the settings UI toggle, or a monitor-change callback, none of which may throw.
        }
    }

    /// <summary>
    /// Clears the "this feature does nothing on this system" latch so an explicit user action
    /// (toggling the setting, re-enabling the panel) gets a fresh set of attempts.
    /// </summary>
    private void ResetVirtualDesktopCircuitBreaker()
    {
        _ineffectiveMoveCount = 0;
        Model.VirtualDesktopFeatureIneffective = false;
    }

    private void PositionWindow(Window window)
    {
        var primary = _monitorService.GetPrimaryMonitor();
        var workArea = primary.WorkArea;

        window.Left = workArea.Left;
        window.Top = workArea.Top;
        window.Height = workArea.Height;
    }

    private void OnMonitorsChanged()
    {
        if (_window is null) return;

        // Reposition for the new monitor geometry. The window's styles (WS_EX_TOOLWINDOW |
        // WS_EX_NOACTIVATE) are not affected by monitor changes — nothing re-parents it, so there is
        // nothing to restore here (ADR-019; previously this also called EmbedInDesktop, back when the
        // panel was a WorkerW child).
        try
        {
            PositionWindow(_window);

            // OFF-mode anchor (§5, row "OFF", point 3): a monitor change is one of the few events
            // that can plausibly coincide with the panel having drifted off its anchor desktop.
            if (!Model.ShowOnAllVirtualDesktops)
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    EnforceAnchorDesktopOnce(hwnd);
                }
            }
        }
        catch { /* degrade silently */ }
    }

    // -------------------------------------------------------------------------
    // JSON DTOs (private, internal to this class)
    // -------------------------------------------------------------------------

    private sealed class PanelDto
    {
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Added after the initial release. Absent in older settings files, which System.Text.Json
        /// leaves at the default <see langword="false"/> — backwards compatible by construction.
        /// </summary>
        public bool ShowOnAllVirtualDesktops { get; set; }

        public string IconSize { get; set; } = nameof(SystemIconSize.Medium);

        /// <summary>
        /// GUID of the virtual desktop the panel is pinned to while "show on all virtual desktops"
        /// is off. Stored as a string, not a <see cref="Guid"/>?: <see cref="Load"/> wraps the whole
        /// deserialisation in one catch, so a corrupt value typed as <see cref="Guid"/>? would
        /// discard EVERY panel setting. As a string it degrades to "no anchor" instead (see the
        /// dedicated <see cref="Guid.TryParse(string?, out Guid)"/> call in <see cref="Load"/>).
        /// Absent in settings files written before ADR-019 -&gt; <see langword="null"/> -&gt;
        /// captured on first use.
        /// </summary>
        public string? AnchorVirtualDesktopId { get; set; }

        public List<ItemDto>? Items { get; set; }
    }

    private sealed class ItemDto
    {
        public SystemIconKind Kind { get; set; }
        public bool IsVisible { get; set; }
        public int Order { get; set; }
    }
}
