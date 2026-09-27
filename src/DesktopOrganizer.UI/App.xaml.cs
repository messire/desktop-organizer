using System.Diagnostics;
using System.Globalization;
using System.IO;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Localization;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI;

/// <summary>
/// Interaction logic for App.xaml.
/// </summary>
public partial class App : Application
{
    // Cast from GridMetrics.MinPanelSize (the single source of truth for this value — finalization
    // MAJOR M-1/m-2 — also backs ChildWindow.xaml's hard MinWidth="120" and ChildWindow.xaml.cs's own
    // MinPanelSize) rather than an independent literal: System.Drawing.Size's width/height are int,
    // while GridMetrics.MinPanelSize is a double shared with the (fractional-IconSize-aware) WPF-side
    // panel geometry math, so a narrowing cast is required here at the int/double boundary.
    private const int MinPanelSize = (int)GridMetrics.MinPanelSize;

    private readonly ServiceProvider _serviceProvider;
    private readonly ISingleInstanceCoordinator _coordinator;

    // Defense-in-depth (ADR-023 D-3): Program.Main only ever constructs App after a RunAsPrimary
    // decision (ADR-023 D-2), so this is always SingleInstanceRole.Primary on every reachable path
    // today. It exists so that if a future refactor of the entry point ever did construct App for a
    // non-Primary process, OnExit's early-return below still stops it from reaching
    // SaveWindowsStateNow()/SystemIconsPanel.Save() and stomping the real Primary's state with an
    // empty/unloaded model (ADR-023 R-1, phase-3 plan §0.1).
    private readonly SingleInstanceRole _role;
    private ITrayIconManager? _trayIconManager;
    private IWindowManager? _windowManager;
    private IDesktopIconsService? _desktopIcons;
    private IDesktopGestureService? _desktopGestures;
    private IMonitorService? _monitorService;
    private IAppSettings? _appSettings;
    private ISystemIconsPanelManager? _systemIconsPanel;
    private IGestureDiagnostics? _gestureDiagnostics;
    private IDiagnosticsLog? _diagnosticsLog;
    private IApplicationStateService? _applicationState;
    private IPageManager? _pageManager;
    private ILocalizationService? _localization;
    private IDesktopScaleProvider? _desktopScale;
    private ShutdownSequence? _shutdownSequence;
    private CrashCoordinator? _crashCoordinator;
    private ShellRecoveryCoordinator? _shellRecovery;

    /// <summary>The same <c>Logs</c> folder <see cref="JsonLinesDiagnosticsLog"/> writes its JSONL sessions to (see <c>DiConfig.DefaultDiagnosticsLogDirectory</c>).</summary>
    private static string CrashLogDirectory => Path.Combine(AppStorageRoot.Default, "Logs");

    private ITrayIconManager TrayIconManager
        => (_trayIconManager ??= _serviceProvider.GetService<ITrayIconManager>())!;

    private IPageManager PageManager
        => (_pageManager ??= _serviceProvider.GetService<IPageManager>())!;

    private IWindowManager WindowManager
        => (_windowManager ??= _serviceProvider.GetService<IWindowManager>())!;

    private IDesktopIconsService DesktopIcons
        => (_desktopIcons ??= _serviceProvider.GetService<IDesktopIconsService>())!;

    private IDesktopGestureService DesktopGestures
        => (_desktopGestures ??= _serviceProvider.GetService<IDesktopGestureService>())!;

    private IMonitorService MonitorService
        => (_monitorService ??= _serviceProvider.GetService<IMonitorService>())!;

    private IAppSettings AppSettings
        => (_appSettings ??= _serviceProvider.GetService<IAppSettings>())!;

    private ISystemIconsPanelManager SystemIconsPanel
        => (_systemIconsPanel ??= _serviceProvider.GetService<ISystemIconsPanelManager>())!;

    private IApplicationStateService ApplicationState
        => (_applicationState ??= _serviceProvider.GetService<IApplicationStateService>())!;

    private IGestureDiagnostics GestureDiagnostics
        => (_gestureDiagnostics ??= _serviceProvider.GetService<IGestureDiagnostics>())!;

    private IDiagnosticsLog DiagnosticsLog
        => (_diagnosticsLog ??= _serviceProvider.GetService<IDiagnosticsLog>())!;

    private ILocalizationService Localization
        => (_localization ??= _serviceProvider.GetService<ILocalizationService>())!;

    /// <summary>The per-monitor DPI snapshot provider (release-hardening F, ADR-037).</summary>
    private IDesktopScaleProvider DesktopScale
        => (_desktopScale ??= _serviceProvider.GetService<IDesktopScaleProvider>())!;

    /// <summary>
    /// The application's one idempotent shutdown sequence (ADR-033 part 2) — see <see cref="ShutdownSequence"/>'s
    /// own remarks. Built lazily, on first access, from properties that are themselves already lazily
    /// resolved from DI — by the time anything can actually reach this (<see cref="OnExit"/>, a
    /// <c>SessionEnding</c> handler, or a crash on the UI thread), <see cref="StartApp"/> has long since
    /// resolved every one of them for its own ordinary use, so this never triggers a first-time DI
    /// resolve mid-shutdown/mid-crash.
    /// </summary>
    /// <remarks>
    /// H3 (release-hardening 1.0 review): <c>DiagnosticsLog.Flush</c> is the verbose gesture/lifecycle
    /// tracer — a <c>NullDiagnosticsLog</c> no-op in every Release build (off by default) — so it flushing
    /// is not enough to guarantee the ALWAYS-ON app log's own buffered events (every Info/Warn/Error this
    /// session wrote through <see cref="AppLog.Current"/>) reach their sink before
    /// <c>_serviceProvider.Dispose()</c> tears down whatever that sink depends on — <see cref="AppLog.Current"/>'s
    /// own <c>Flush</c> is added right alongside it for exactly that reason.
    /// </remarks>
    private ShutdownSequence ShutdownSequenceInstance => _shutdownSequence ??= new ShutdownSequence(
        _coordinator.EnterShutdown,
        RestoreDesktopIcons,
        ApplicationState.SaveNow,
        GestureDiagnostics.SessionExiting,
        _coordinator.CompleteShutdown,
        DiagnosticsLog.Flush,
        AppLog.Current.Flush, // H3: also flush the always-on app log — see this property's own remarks.
        _serviceProvider.Dispose);

    /// <summary>
    /// The application's one fatal-crash sequence (ADR-033 part 2). Built lazily so every dependency is
    /// captured once, the first time a crash actually happens — <see cref="DesktopIcons"/> in particular
    /// is forced to resolve here rather than staying unresolved until this point, which is exactly the
    /// "no DI resolve during the crash itself" invariant <see cref="CrashCoordinator"/>'s own remarks
    /// describe; this property is also touched eagerly from the constructor (see <see cref="CrashRouting"/>)
    /// so the very first background-thread crash handler installed by <c>Program.Main</c> already has
    /// somewhere to route to.
    /// </summary>
    /// <remarks>
    /// Two H3 fixes (release-hardening 1.0 review), both below: the <c>flushAppLog</c> argument is
    /// <see cref="AppLog.Current"/>'s own <c>Flush</c> — not <c>IDiagnosticsLog.Flush</c>, the verbose
    /// gesture/lifecycle tracer that is a <c>NullDiagnosticsLog</c> no-op in every Release build (off by
    /// default) — so the crash record <see cref="CrashCoordinator.HandleFatal"/> just wrote via
    /// <c>_appLog.Error</c> cannot be lost if the process dies immediately after that step. The
    /// <c>saveStateForUiThreadCrash</c> argument goes through <see cref="RunShutdownSequence"/> (not a
    /// bare <c>ShutdownSequenceInstance.Run()</c>) so a pending Explorer-restart recovery (ADR-034) is
    /// stopped and <c>_shellRecovery</c> disposed BEFORE that step's <c>ApplicationState.SaveNow</c>/
    /// <c>_serviceProvider.Dispose</c> run — see <see cref="RunShutdownSequence"/>'s own remarks for the
    /// hazard this closes (a recovery rebuilding panels while, or after, the container that owns them is
    /// being torn down).
    /// </remarks>
    private CrashCoordinator CrashCoordinatorInstance => _crashCoordinator ??= new CrashCoordinator(
        DesktopIcons,
        (exception, origin) => new CrashReportWriter(CrashLogDirectory).Write(exception, origin),
        AppLog.Current,
        AppLog.Current.Flush, // H3: flush the always-on app log, not IDiagnosticsLog — see this property's own remarks.
        RunShutdownSequence, // H3: via RunShutdownSequence, not a bare ShutdownSequenceInstance.Run() — see this property's own remarks.
        ShowCrashDialog,
        OpenLogsFolder,
        code =>
        {
            // Only an origin running on THIS (UI) thread can safely drive an orderly WPF shutdown —
            // CrashCoordinator's own remarks on CrashOrigin.Background explain why a genuine
            // background-thread crash must not attempt this at all (the CLR is already terminating the
            // process by the time AppDomain.UnhandledException's handler returns).
            if (Dispatcher.CheckAccess())
                Shutdown(code);
        });

    private static string GetAppVersion() => AppVersionInfo.Display;

    /// <summary>
    /// The system/reference DPI scale (1.0 at 96 DPI) for diagnostics only (release-hardening F §5 #12,
    /// ADR-037) — read from <see cref="DesktopScale"/>'s current snapshot instead of the pre-F
    /// <c>System.Drawing.Graphics.FromHwnd(IntPtr.Zero)</c> GDI probe, so this diagnostics call no longer
    /// needs a fourth, independent DPI source next to <see cref="IDesktopScaleProvider"/>. Deliberately
    /// the single scalar <see cref="IGestureDiagnostics.SessionStarted"/> already accepts — the richer
    /// per-monitor/mode/layer-scale picture <see cref="DesktopScale"/>'s <see cref="DesktopScaleModel"/>
    /// snapshot now carries is available to a future diagnostics schema change, not added here (that
    /// contract is out of this package's scope).
    /// </summary>
    private double GetPrimaryDpiScale() => DesktopScale.Current.ReferenceScale;

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class. Only ever called from
    /// <see cref="Program.Main"/>, after the single-instance handshake has already resolved to
    /// <see cref="SingleInstanceStartupAction.RunAsPrimary"/> (ADR-023 D-2) — there is no parameterless
    /// constructor, so nothing can accidentally construct an <see cref="App"/> without first going
    /// through that handshake.
    /// </summary>
    /// <param name="serviceProvider">
    /// The DI container built by <see cref="Program.Main"/> — the same instance
    /// <see cref="ISingleInstanceCoordinator"/> was itself resolved from, so it must not be rebuilt
    /// here (that would construct a second coordinator instance holding its own, separate handles).
    /// </param>
    /// <param name="coordinator">
    /// The already-started coordinator (ADR-023). Used here only for <see cref="ISingleInstanceCoordinator.EnterShutdown"/>
    /// (<see cref="OnExit"/>) and to subscribe <see cref="SingleInstanceActivationHandler"/> to
    /// <see cref="ISingleInstanceCoordinator.ActivationRequested"/> (<see cref="StartApp"/>) — disposing
    /// it (stopping the activation server, releasing both mutexes) happens implicitly when
    /// <paramref name="serviceProvider"/> is disposed, since it is itself a registered singleton there.
    /// </param>
    /// <param name="role">See the remarks on <see cref="_role"/>.</param>
    internal App(ServiceProvider serviceProvider, ISingleInstanceCoordinator coordinator, SingleInstanceRole role)
    {
        _serviceProvider = serviceProvider;
        _coordinator = coordinator;
        _role = role;

        // ADR-033 part 2 (crash safety) — installed here, in the constructor, rather than lazily on
        // first use: DispatcherUnhandledException must be wired before Run() ever pumps a single
        // message, and CrashRouting.Handler must point somewhere real before Program.Main's own
        // AppDomain/WinForms handlers (installed even earlier, before this constructor runs) can ever
        // actually fire for real.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        CrashRouting.Handler = CrashCoordinatorInstance.HandleFatal;

        // Q2 (plan §2 default): both routed through the very same idempotent ShutdownSequenceInstance
        // OnExit itself uses — whichever of SessionEnding/OnExit reaches it first does the real work,
        // the other is a no-op. Application.SessionEnding is the primary signal (WPF's own top-level
        // window already participates in WM_QUERYENDSESSION/WM_ENDSESSION); Microsoft.Win32.SystemEvents
        // .SessionEnding is the "insurance" fallback the plan calls for, raised independently of any WPF
        // window and marshalled onto the UI thread (it can arrive on a different thread) via
        // UiDispatch — a dropped signal here just means OnExit's own, ordinary path still runs at real
        // process exit.
        SessionEnding += (_, _) => RunShutdownSequence();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
            UiDispatch.TryBeginInvoke(Dispatcher, RunShutdownSequence);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        // Defense-in-depth (ADR-023 D-3) — see the remarks on _role: on every reachable path today this
        // process is always Primary, but if it somehow weren't, this must be the very first thing
        // OnExit does, before any teardown below ever runs (ADR-023 R-1, phase-3 plan §0.1).
        if (_role != SingleInstanceRole.Primary)
        {
            base.OnExit(e);
            return;
        }

        // ADR-033 part 2 (plan §2 Q2): the exact same idempotent sequence a SessionEnding handler (see
        // the constructor) or a fatal UI-thread crash's own "save state" step (CrashCoordinator) may
        // already have run — see ShutdownSequenceInstance's own remarks for why this is always safe to
        // call again here regardless of which of those already fired.
        RunShutdownSequence();
        base.OnExit(e);
    }

    /// <summary>
    /// Shows the crash dialog (<c>Crash_Dialog_Title</c>/<c>Crash_Dialog_Message</c>, ADR-033 part 2 plan
    /// §2 step 4) and reports whether the user asked to open the logs folder.
    /// </summary>
    /// <param name="crashReportPath">Unused by the dialog text itself (plan: no path shown to the user) — accepted only to match <see cref="CrashCoordinator"/>'s delegate shape.</param>
    private static bool ShowCrashDialog(string crashReportPath)
    {
        _ = crashReportPath;

        var result = MessageBox.Show(
            LocalizationResources.GetString("Crash_Dialog_Message", CultureInfo.CurrentUICulture),
            LocalizationResources.GetString("Crash_Dialog_Title", CultureInfo.CurrentUICulture),
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Opens the crash log's containing folder (falling back to the whole <c>Logs</c> folder if
    /// <paramref name="crashReportPath"/> is blank — e.g. the report itself failed to write) in Explorer.
    /// </summary>
    /// <param name="crashReportPath">The crash report's full path, or <see cref="string.Empty"/>.</param>
    private static void OpenLogsFolder(string crashReportPath)
    {
        var directory = string.IsNullOrEmpty(crashReportPath)
            ? CrashLogDirectory
            : Path.GetDirectoryName(crashReportPath) is { Length: > 0 } parent ? parent : CrashLogDirectory;

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    /// <summary>
    /// WPF's own UI-thread crash entry point (ADR-033 part 2). Marks the exception handled — WPF's
    /// default of tearing down the process with no report at all is exactly what this replaces — then
    /// routes it through the same <see cref="CrashCoordinator"/> a background-thread/WinForms crash uses.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashCoordinatorInstance.HandleFatal(e.Exception, CrashOrigin.Dispatcher);
    }

    /// <summary>
    /// Stops Explorer-restart recovery (ADR-034) — a pending recovery attempt must never rebuild
    /// panels while, or after, the shutdown sequence saves state and disposes the container — then runs
    /// the idempotent <see cref="ShutdownSequence"/>. Both steps are idempotent, so every entry point
    /// (<see cref="OnExit"/>, both <c>SessionEnding</c> handlers) can call this unconditionally.
    /// </summary>
    private void RunShutdownSequence()
    {
        try
        {
            _shellRecovery?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Current.Error("App", "ShellRecoveryDisposeFailed", ex);
        }

        ShutdownSequenceInstance.Run();
    }

    /// <summary>
    /// Wires Explorer-restart recovery (ADR-034). Runs last in <see cref="StartApp"/>: panels, sidebar
    /// and tray must all exist before a recovery could touch them. Failure only disables automatic
    /// recovery (the pre-ADR-034 behaviour) — it never blocks startup.
    /// </summary>
    private void InitShellRecovery()
    {
        try
        {
            _shellRecovery = new ShellRecoveryCoordinator(
                _serviceProvider.GetRequiredService<IExplorerRestartMonitor>(),
                WindowManager,
                _serviceProvider.GetRequiredService<IVirtualDesktopService>(),
                SystemIconsPanel,
                DesktopIcons,
                TrayIconManager,
                _serviceProvider.GetService<IAppLog>() ?? AppLog.Current,
                DesktopScale);
            _shellRecovery.Start();
        }
        catch (Exception ex)
        {
            AppLog.Current.Error("App", "ShellRecoveryInitFailed", ex);
        }
    }

    /// <summary>
    /// Re-shows the real desktop icons on shutdown. The app can hide them (desktop double-click
    /// gesture, settings checkbox), so without this the user is left with an empty desktop
    /// after closing the app. Failures are swallowed — shutdown must not be blocked.
    /// </summary>
    private void RestoreDesktopIcons()
    {
        try
        {
            if (!DesktopIcons.AreIconsVisible)
                DesktopIcons.SetIconsVisible(true);
        }
        catch (Exception ex)
        {
            AppLog.Current.Error("App", "RestoreDesktopIconsOnExitFailed", ex);
        }
    }

    /// <summary>
    /// Builds the Ambient Accent Visual System's <see cref="Application.Resources"/> merge chain in
    /// code (ADR-025 D-1/D-12, docs/PLANS/phase-5-ambient-accent-ui.md §1/§3.2) — see App.xaml's own
    /// remarks for WHY this can't be a static <c>&lt;ResourceDictionary.MergedDictionaries&gt;</c> list
    /// declared in XAML any more. Precisely: <c>Brushes.xaml</c>'s entries are plain
    /// <c>&lt;SolidColorBrush ... po:Freeze="True"/&gt;</c> objects, not deferred content the way
    /// <c>Style</c>/<c>ControlTemplate</c>/<c>DataTemplate</c> resources are (WPF only defers THOSE
    /// types' parsing until first lookup) — so each brush, and the <c>{StaticResource Color.*}</c>
    /// reference inside it, is constructed and resolved as soon as <c>Brushes.xaml</c> itself is parsed/
    /// merged, not lazily on first consumer access. Once <c>po:Freeze="True"</c> then locks that
    /// resolved value in as an immutable <see cref="System.Windows.Media.Brush"/>, no later dictionary
    /// merge can ever change it. So <c>Colors.HighContrast.xaml</c> — when Windows High Contrast is
    /// active — has to be merged in BEFORE <c>Brushes.xaml</c> parses, not appended afterwards (a later
    /// merge only overwrites the <c>Color.*</c> dictionary keys; it can never reach brushes already
    /// frozen from the old values).
    ///
    /// <see cref="SystemParameters.HighContrast"/> is read exactly once here, synchronously, before any
    /// <see cref="Window"/> is constructed (see the one call site, <see cref="StartApp"/>) — "minimum at
    /// startup" per D-12; live re-theming while the app is running is explicitly out of scope for this
    /// fix and would require re-running this whole method plus re-freezing/re-fetching every consumer
    /// that already resolved a Brush from the old dictionary state.
    ///
    /// Order is otherwise EXACTLY what App.xaml used to declare statically — only the conditional
    /// Colors.HighContrast.xaml insertion between Colors.xaml and Spacing.xaml is new:
    ///
    ///     Colors -> [Colors.HighContrast, only if SystemParameters.HighContrast] -> Spacing -> Brushes
    ///     -> Typography -> Icons -> Controls -> Panels -> Templates.
    /// </summary>
    private void LoadResourceDictionaries()
    {
        var highContrast = SystemParameters.HighContrast;
        HighContrastState.IsActive = highContrast;

        // docs/PLANS/workspace-grid-07-visual.md "reduced motion": same "read once at startup" contract
        // as HighContrastState.IsActive directly above — see MotionState's own remarks. Read here (not
        // e.g. lazily from RollUpBehavior itself) purely to keep every SystemParameters snapshot this app
        // takes at launch in one place, next to the flag it already reads this way.
        MotionState.AnimationsEnabled = SystemParameters.ClientAreaAnimation;

        var merged = Resources.MergedDictionaries;
        merged.Add(LoadResourceDictionary("/Resources/Styles/Colors.xaml"));

        if (highContrast)
            merged.Add(LoadResourceDictionary("/Resources/Styles/Colors.HighContrast.xaml"));

        merged.Add(LoadResourceDictionary("/Resources/Styles/Spacing.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Brushes.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Typography.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Icons.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Controls.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Settings.xaml"));   // settings window + section UserControls (ADR-031)
        merged.Add(LoadResourceDictionary("/Resources/Styles/Panels.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Templates.xaml"));
    }

    // Instance method (not static) purely to satisfy StyleCop's static-members-before-instance-members
    // ordering rule (SA1204) without relocating this helper away from its only caller, immediately
    // above — LoadComponent itself is Application's own instance API either way.
    //
    // Names the assembly explicitly ("/DesktopOrganizer.UI;component/...") rather than passing
    // relativePackUri straight through with no assembly segment at all: Application.LoadComponent(Uri)
    // REQUIRES a relative URI — it throws ArgumentException ("AbsoluteUriNotAllowed") on an absolute
    // "pack://application:,,,/..." form, so UriKind.Relative can't be dropped here — but a relative pack
    // URI with no leading "/AssemblyName;component" segment resolves against whatever assembly
    // PresentationFramework treats as the application's entry/resource assembly, not necessarily THIS
    // (DesktopOrganizer.UI) assembly specifically. They are the same assembly on every reachable path
    // today (Program.Main lives in DesktopOrganizer.UI), so the unqualified form happened to resolve
    // correctly, but only incidentally — adding the explicit assembly segment removes that ambiguity
    // without changing the (still-required) relative URI kind.
    private ResourceDictionary LoadResourceDictionary(string relativePackUri)
        => (ResourceDictionary)LoadComponent(new Uri($"/DesktopOrganizer.UI;component{relativePackUri}", UriKind.Relative));

    private void StartApp(object? sender, StartupEventArgs e)
    {
        // Localization bootstrap (docs/PLANS/localization.md §2.5 steps a-c) — must run before
        // EVERYTHING else in StartApp, including LoadResourceDictionaries() below: it is the first
        // thing that can construct a Window (ChildWindow/MainWindow/SystemIconsPanelWindow all resolve
        // {loc:Loc}/{loc:LocBinding} bindings against LocalizationSource.Current), and
        // FrameworkElement.LanguageProperty's default metadata override (CultureSync.Attach) is only
        // honoured if it runs before the first FrameworkElement is ever constructed.
        //
        // a. Resolve IAppSettings.Language (the persisted preference — a single additive user.config
        //    scalar, D-1) against the ILocalizationService singleton DiConfig already registered with
        //    SystemCulture.OriginalUiCulture (captured by Program.Main, before any override).
        Localization.ApplyLanguage(AppSettings.Language);

        // b. Publish this exact instance as the one sanctioned static access point (L-5) for every
        //    markup extension/converter/static helper XAML and CLR code construct outside of DI. Called
        //    exactly once — see LocalizationSource.Initialize's own remarks for why calling it twice
        //    with a different instance would be a bug, not a no-op.
        LocalizationSource.Initialize(Localization);

        // c. Override FrameworkElement.LanguageProperty's default metadata and start keeping every
        //    open Window's Language / the UI thread's own culture in sync with future language switches.
        CultureSync.Attach(Localization, this);

        // Must run before anything else that can construct a Window: InitTrayIcon()/LoadPanels() below
        // are the first things that can construct one (ChildWindow/MainWindow/SystemIconsPanelWindow),
        // and every one of those XAML files resolves StaticResource keys this builds.
        LoadResourceDictionaries();

        // D-12 (docs/PLANS/phase-6-edit-layout-ux.md §1): published as an application resource, not a
        // ChildWindowViewModel property (DO-002 §0 — zero changed lines there), so a panel's
        // HeaderContextMenu — which lives in its own visual tree and does not inherit attached DPs — can
        // still bind to it via {StaticResource LayoutEditMode}. Must be set before InitTrayIcon()/
        // LoadPanels() below construct any Window whose XAML might reference it.
        Resources["LayoutEditMode"] = _serviceProvider.GetRequiredService<ILayoutEditModeService>();

        // ADR-033 part 2 (crash safety, plan §2 startup self-heal): must run before InitTrayIcon() below
        // reads DesktopIcons.AreIconsVisible to set the tray checkmark's initial state, so a previous
        // process that died with icons hidden (crash that never reached RestoreDesktopIcons, taskkill,
        // power loss) never leaves this session starting with a stale-hidden desktop.
        StartupIconsSelfHeal.Apply(DesktopIcons);

        InitTrayIcon();
        MonitorService.Start(); // begin listening for display changes before panels load
        LoadPanels();

        // Recorded here — after LoadPanels() — so panelCount reflects the panels actually restored
        // this session, not zero.
        //
        // Guarded by DiagnosticsLog.IsEnabled here (not just inside GestureDiagnostics.SessionStarted)
        // so the arguments themselves are never eagerly computed when diagnostics is off — the default
        // for Release builds without --diag/DESKTOPORGANIZER_DIAG=1 (finalization MINOR-4). Also skips
        // MonitorService.GetMonitors() (a native monitor enumeration) when diagnostics is disabled.
        if (DiagnosticsLog.IsEnabled)
        {
            GestureDiagnostics.SessionStarted(
                GetAppVersion(),
                Environment.OSVersion.VersionString,
                GetPrimaryDpiScale(),
                MonitorService.GetMonitors().Count,
                WindowManager.GetAllChildWindows().Count());
        }

        InitDesktopGestures();

        SystemIconsPanel.Load();
        SystemIconsPanel.Apply();

        // Second half of the write barrier (ADR-024 D-11 / finalization MAJOR M-1): every one of the
        // four state-owning consumers has now had the chance to read its own slice of the document
        // ApplicationState.Load() returned in Program.Main — WindowManager via LoadPanels() above,
        // PageManager and LayoutSnapshotService transitively via LoadPanels()'s MainWindowViewModel
        // resolution (whose constructor requires a LayoutSnapshotsViewModel, whose own constructor
        // calls ILayoutSnapshotService.Load()), and SystemIconsPanelManager via the Load() immediately
        // above — the last of the four to run. Any RequestSave()/SaveNow() before this line is now a
        // guaranteed no-op (ApplicationStateService.PersistNow) instead of risking a write that pulls
        // an empty/default section from a consumer that hasn't loaded yet and persists it over real
        // on-disk data.
        ApplicationState.MarkConsumersReady();

        // ADR-034: after the write barrier above, so a recovery that has to rebuild a panel can never
        // run while a consumer is still unloaded.
        InitShellRecovery();

        // Wired last, deliberately after every other startup step above: subscribing is the "attach
        // sink" moment that immediately replays anything ActivationRequestRelay buffered since
        // Program.Main started the activation server, well before this App/StartApp existed (ADR-023
        // §2.2). Handle() itself only ever calls IWindowManager.ShowMainWindow() (ADR-023 D-8) — it
        // never touches panels, pages, or SystemIconsPanel, so it is safe to wire regardless of what
        // else StartApp did or didn't finish.
        // UiDispatch.TryBeginInvoke (fire-and-forget), not a bare Current.Dispatcher.Invoke/
        // UiDispatch.TryInvoke: ActivationRequested is raised on NamedPipeActivationServer's own
        // background accept thread, so an activation arriving while the dispatcher is already shutting
        // down would otherwise rethrow TaskCanceledException on that thread — an unhandled exception off
        // the UI thread, i.e. a hard process kill during an otherwise clean exit. Dropping a "show the
        // settings window" request from a process that is closing is the correct degrade. See
        // UiDispatch's remarks.
        //
        // MAJOR M-3 fix (P08 fix cycle 2): a BLOCKING Invoke here used to run afoul of
        // ISingleInstanceCoordinator.CompleteShutdown's bounded NamedPipeActivationServer.Stop().Join(2s)
        // (ADR-023 D-9) — a plain Thread.Join that does NOT pump the dispatcher. An activation arriving
        // on the accept thread while App.OnExit is inside that join blocked the accept thread until the
        // join gave up, needlessly delaying application exit by up to its full 2-second budget (and, at
        // startup, held ActivationRequestRelay's internal lock for that same span — see UiDispatch.
        // TryBeginInvoke's remarks). ShowMainWindow() has no result the accept thread needs back — the
        // DO/1 response is already written and drained before ActivationRequestRelay ever hands the
        // request off (ADR-023 D-8, NamedPipeActivationServer.HandleConnection: "answer first, hand off
        // after") — so fire-and-forget changes nothing about the wire protocol, only removes the
        // accept-thread block.
        var activationHandler = new SingleInstanceActivationHandler(WindowManager, action => UiDispatch.TryBeginInvoke(Current.Dispatcher, action));
        _coordinator.ActivationRequested += (_, request) => activationHandler.Handle(request);
    }

    // NIT (P08 fix cycle 2 UiDispatch review): the raw Current.Dispatcher.BeginInvoke/Invoke calls below
    // are deliberately NOT migrated to UiDispatch. UiDispatch exists to guard a marshal FROM a background
    // thread onto the UI thread; none of these three call sites cross threads at all. WH_MOUSE_LL hook
    // callbacks (DesktopGestures.DesktopDoubleClicked/LassoCompleted) run on whichever thread called
    // SetWindowsHookEx — the UI thread, per DesktopGestureService — and TrayIconManager's requested
    // events likewise fire from the tray's own hidden-window message loop, pumped on the UI thread. A
    // same-thread Dispatcher.Invoke/BeginInvoke uses WPF's own re-entrant fast path
    // (Dispatcher.CheckAccess() == true) regardless of shutdown state, so there is no dead-dispatcher
    // hazard here for UiDispatch to guard against.
    private void InitDesktopGestures()
    {
        // Heavy work is deferred off the hook callback via the dispatcher. D-6: the double-click gesture
        // is a second entry point (besides the tray's own "Show desktop icons" click) that changes
        // IDesktopIconsService's visibility, so it must refresh the tray checkmark too — see
        // ITrayIconManager.SetDesktopIconsVisible's remarks — otherwise the checkmark goes stale the
        // moment a user double-clicks the desktop instead of using the tray menu item.
        DesktopGestures.DesktopDoubleClicked += (_, _) => Current.Dispatcher.BeginInvoke(ToggleDesktopIcons);
        DesktopGestures.LassoCompleted += (_, args) => Current.Dispatcher.BeginInvoke(() => CreateLassoPanel(args));
        DesktopGestures.Start();
    }

    /// <summary>
    /// Toggles real desktop icon visibility and keeps the tray's "Show desktop icons" checkmark in sync.
    /// The one place both the double-click gesture (<see cref="InitDesktopGestures"/>) and the tray menu
    /// item's own click (<see cref="InitTrayIcon"/>) route through, so the checkmark can never go stale
    /// relative to whichever entry point the user actually used (D-6).
    /// </summary>
    private void ToggleDesktopIcons()
    {
        DesktopIcons.Toggle();
        TrayIconManager.SetDesktopIconsVisible(DesktopIcons.AreIconsVisible);
    }

    private void LoadPanels()
    {
        WindowManager.LoadWindowsState();

        // Sync the settings VM after panels are loaded
        var settingsVm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        settingsVm.SyncPanels();
    }

    private void ShowSettingsWindow(object? sender, EventArgs e)
        => Current.Dispatcher.Invoke(() => WindowManager.ShowMainWindow());

    private void ToggleSettingsWindow(object? sender, EventArgs e)
        => Current.Dispatcher.Invoke(() => WindowManager.StartMainWindow());

    private void InitTrayIcon()
    {
        TrayIconManager.Initialize();
        TrayIconManager.OnShowMainWindowRequested += ShowSettingsWindow;
        TrayIconManager.OnToggleMainWindowVisibilityRequested += ToggleSettingsWindow;
        TrayIconManager.OnExitRequested += (_, _) => Shutdown();

        // Installer-initiated graceful shutdown (installer/DesktopOrganizer.iss): resolved and started
        // here — never in ISingleInstanceCoordinator's own setup — because a listener must exist only
        // for the process that actually is Primary, and StartApp itself only ever runs for Primary
        // (ADR-023 D-2/D-3, same invariant _role documents at the top of this file). Disposed implicitly
        // when _serviceProvider.Dispose() runs at the end of OnExit, same as every other IDisposable
        // singleton.
        //
        // ShutdownRequested fires on a ThreadPool wait-callback thread (see
        // InstallerShutdownRequestListener), so it must be marshalled onto the UI thread exactly like the
        // named-pipe activation handler above — UiDispatch.TryBeginInvoke performs that marshal and is
        // itself the guard against a shutdown-timing race: if the dispatcher's HasShutdownStarted/
        // HasShutdownFinished is already set by the time the signal arrives, BeginInvoke is skipped
        // entirely (returns false, no-op); if it arrives just before that flag flips, the queued Shutdown
        // simply gets dropped along with the rest of the dispatcher's queue when it stops pumping. Either
        // way this can never throw, so no extra try/catch is needed here.
        //
        // `Dispatcher` (this Application's own dispatcher property) is read HERE, on the UI thread where
        // InitTrayIcon always runs, and captured by the closure below — not re-read as `Current.Dispatcher`
        // from inside the closure, which would instead run on the listener's thread-pool callback thread
        // at fire time. Application.Current is safe to read cross-thread today, but capturing the
        // dispatcher reference once, up front, removes any dependency on that remaining true.
        var dispatcher = Dispatcher;
        var shutdownRequestListener = _serviceProvider.GetRequiredService<IShutdownRequestListener>();
        shutdownRequestListener.ShutdownRequested += (_, _) => UiDispatch.TryBeginInvoke(dispatcher, Shutdown);
        shutdownRequestListener.Start();

        // D-9 #1 — the tray is the one toggle point for the global layout edit mode that never depends
        // on WS_EX_NOACTIVATE. ITrayIconManager (DesktopOrganizer.Services) has no visibility into
        // ILayoutEditModeService (DesktopOrganizer.UI sits above it in the layering — see that
        // interface's remarks), so this is the translation point: tray clicks in, service calls out.
        var layoutEditMode = _serviceProvider.GetRequiredService<ILayoutEditModeService>();

        TrayIconManager.OnEditLayoutToggleRequested += (_, _) => layoutEditMode.Toggle(LayoutModeChangeSource.Tray);
        TrayIconManager.OnLockLayoutToggleRequested += (_, _) => layoutEditMode.IsLayoutLocked = !layoutEditMode.IsLayoutLocked;

        // D-6: routes through the same ToggleDesktopIcons() helper the desktop double-click gesture uses
        // (InitDesktopGestures), so the checkmark is refreshed identically regardless of which entry
        // point the user clicked.
        TrayIconManager.ToggleDesktopIconsRequested += (_, _) => ToggleDesktopIcons();

        // Reflects whatever IDesktopIconsService's visibility already is at startup (restored, if
        // necessary, from whatever RestoreDesktopIcons left it at on the previous exit) — mirrors
        // RefreshTrayLayoutMenu() below doing the same for the layout-edit items.
        TrayIconManager.SetDesktopIconsVisible(DesktopIcons.AreIconsVisible);

        // UpdateContextMenu takes a caller-supplied snapshot, not live state (see its remarks) — refresh
        // it every time anything it depends on changes, not just once at startup, so the tray checkmarks
        // never go stale relative to a change made from another entry point (panel menu, settings
        // window, Escape).
        // DO-006: isMainWindowVisible is a known dead flag, not wired to real main-window visibility — see docs/Refactoring/BACKLOG.md
        void RefreshTrayLayoutMenu() => TrayIconManager.UpdateContextMenu(
            isMainWindowVisible: false,
            isEditLayoutChecked: layoutEditMode.IsEditing,
            canEnterEditLayout: layoutEditMode.CanEnterEditLayout,
            isLockLayoutChecked: layoutEditMode.IsLayoutLocked);

        layoutEditMode.ModeChanged += (_, _) => RefreshTrayLayoutMenu();
        layoutEditMode.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ILayoutEditModeService.IsLayoutLocked) or nameof(ILayoutEditModeService.CanEnterEditLayout))
                RefreshTrayLayoutMenu();
        };

        // Initialize() above built the menu from hardcoded defaults, before IAppSettings.IsLayoutLocked
        // (persisted, D-8) had a chance to be reflected — replace it now with the real snapshot.
        RefreshTrayLayoutMenu();
    }

    /// <summary>
    /// Creates a panel covering the lasso rectangle drawn on the desktop.
    /// </summary>
    private void CreateLassoPanel(DesktopLassoEventArgs args)
    {
        var model = NewPanelWithDefaults();

        // F-7 (P03-L1, ADR-028 «Lasso-создание панели: px vs DIP»): DesktopLassoEventArgs is in PHYSICAL
        // SCREEN PIXELS by contract, while ChildWindowModel.Position/Size are desktop-DIP — writing one
        // into the other made every lassoed panel oversized and displaced at any scale other than 100%.
        // release-hardening F (ADR-037): the conversion now goes through IDesktopScaleProvider's current
        // snapshot — the monitor the lasso rectangle intersects most (tie → primary) — rather than a
        // single system-wide coefficient, so a lasso drawn on a secondary monitor at a different scale
        // (mixed-DPI TopLevel mode) converts correctly; the minimum panel size is applied after it.
        var dipBounds = LassoPanelGeometry.ToDip(
            new Rect(args.X, args.Y, args.Width, args.Height), DesktopScale.Current, MinPanelSize);

        var monitors = new List<(string DeviceName, System.Windows.Rect Bounds, System.Windows.Rect WorkArea)>();
        foreach (var monitor in MonitorService.GetMonitors())
            monitors.Add((monitor.DeviceName, monitor.Bounds, monitor.WorkArea));

        // Writes Position/Size, aligning the position to the workspace grid of the monitor the lasso was
        // drawn on when the new panel is GridAligned (closes the TODO(P03) in
        // PanelDefaults.ReconcileGridAlignedSpans, which cannot resolve a monitor origin itself).
        LassoPanelGeometry.ApplyTo(model, dipBounds, monitors);

        // MAJOR-1(b) fix-cycle 1 (docs/PLANS/workspace-grid-migration.md §10.5): must run after
        // Position/Size above, not inside NewPanelWithDefaults — see PanelDefaults.ReconcileGridAlignedSpans's
        // remarks for why.
        PanelDefaults.ReconcileGridAlignedSpans(model);

        WindowManager.CreateChildWindow(model);

        // Structural (new panel created via lasso gesture) — write synchronously.
        WindowManager.SaveWindowsStateNow();
    }

    private ChildWindowModel NewPanelWithDefaults()
    {
        // D-7 fix: previously left at ChildWindowModel's own "default" literal, so every lasso-created
        // panel landed on the "default" page regardless of which page the user was actually viewing when
        // they drew the lasso — invisible (and, worse, silently reappearing later) the moment they
        // switched away. PanelDefaults.CreateNew (fix-cycle finding 11) is the single source of truth for
        // this page-assignment/defaults pairing, shared with MainWindowViewModel.AddPanel's "Add panel"
        // button — both panel-creation entry points can no longer independently drift apart on it.
        return PanelDefaults.CreateNew(AppSettings, PageManager.CurrentPage);
    }
}
