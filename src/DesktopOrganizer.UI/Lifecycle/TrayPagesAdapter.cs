using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Localization;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Translates <see cref="ITrayIconManager.OnNextPageRequested"/> (the tray icon's double-click gesture)
/// and the tray context menu's "Pages" submenu (<see cref="ITrayIconManager.SetPages"/>/
/// <see cref="ITrayIconManager.OnPageSelected"/>) into <see cref="IPageManager"/> calls — the UI-side
/// half of tray page navigation (backlog-1x-master-plan.md §1/§2, ADR-044). <see cref="ITrayIconManager"/>
/// itself has no visibility into <see cref="IPageManager"/> (that type lives in this project, above
/// DesktopOrganizer.Services in the layering — see <see cref="ITrayIconManager.OnNextPageRequested"/>'s
/// remarks), so this is the translation point: the tray raises events, this adapter decides and acts,
/// and pushes the page list back down as plain <see cref="TrayPageEntry"/> snapshots.
/// </summary>
/// <remarks>
/// A DI singleton, constructed once and started from <c>App.InitTrayIcon</c> right after
/// <see cref="ITrayIconManager.Initialize"/> — see <see cref="Start"/>.
/// </remarks>
public sealed class TrayPagesAdapter : IDisposable
{
    private readonly ITrayIconManager _trayIconManager;
    private readonly IPageManager _pageManager;
    private readonly ILocalizationService _localization;
    private readonly Action<Action> _defer;
    private readonly Action<Action> _deferPagesRefresh;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayPagesAdapter"/> class.
    /// </summary>
    /// <param name="trayIconManager">Raises <see cref="ITrayIconManager.OnNextPageRequested"/>/<see cref="ITrayIconManager.OnPageSelected"/>, subscribed to by <see cref="Start"/>.</param>
    /// <param name="pageManager">Supplies <see cref="IPageManager.PageDescriptors"/>/<see cref="IPageManager.CurrentPage"/> and performs the actual switch.</param>
    /// <param name="localization">
    /// Supplies <see cref="ILocalizationService.CurrentCulture"/> for <see cref="PageDisplayNames.Resolve"/>
    /// and its <see cref="ILocalizationService.CultureChanged"/> event, so an unnamed <c>"default"</c>
    /// page's displayed name is rebuilt on a language switch (backlog-1x-master-plan.md §2.1).
    /// </param>
    public TrayPagesAdapter(ITrayIconManager trayIconManager, IPageManager pageManager, ILocalizationService localization)
        : this(trayIconManager, pageManager, localization, DeferToUiThread, DeferPagesRefreshToUiThread)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayPagesAdapter"/> class — test-only overload that
    /// takes the UI-thread deferrals directly, so <c>TrayPagesAdapterTests</c> can supply a synchronous
    /// (immediate) defer instead of a live WPF <see cref="Dispatcher"/> (same seam as
    /// <c>SingleInstanceActivationHandler</c>/<c>PanelCustomizeService</c>).
    /// </summary>
    /// <param name="trayIconManager">The tray whose <see cref="ITrayIconManager.OnNextPageRequested"/>/<see cref="ITrayIconManager.OnPageSelected"/> this adapter listens to.</param>
    /// <param name="pageManager">The page manager whose current page is advanced on each request.</param>
    /// <param name="localization">The localization service backing <see cref="PageDisplayNames.Resolve"/>.</param>
    /// <param name="defer">Runs a page-switch callback — immediately, or marshalled onto some other thread/queue position.</param>
    /// <param name="deferPagesRefresh">Runs a "Pages" submenu rebuild callback — see <see cref="DeferPagesRefreshToUiThread"/>'s remarks for why this is a separate seam from <paramref name="defer"/>.</param>
    internal TrayPagesAdapter(
        ITrayIconManager trayIconManager,
        IPageManager pageManager,
        ILocalizationService localization,
        Action<Action> defer,
        Action<Action> deferPagesRefresh)
    {
        ArgumentNullException.ThrowIfNull(trayIconManager);
        ArgumentNullException.ThrowIfNull(pageManager);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(defer);
        ArgumentNullException.ThrowIfNull(deferPagesRefresh);

        _trayIconManager = trayIconManager;
        _pageManager = pageManager;
        _localization = localization;
        _defer = defer;
        _deferPagesRefresh = deferPagesRefresh;
    }

    /// <summary>
    /// Subscribes to <see cref="ITrayIconManager.OnNextPageRequested"/>/<see cref="ITrayIconManager.OnPageSelected"/>,
    /// <see cref="IPageManager.PageChanged"/>/<see cref="IPageManager.PagesChanged"/>, and
    /// <see cref="ILocalizationService.CultureChanged"/>, then pushes the initial "Pages" submenu
    /// snapshot. Idempotent — a second call is a no-op — and expected to be called exactly once, from
    /// <c>App.InitTrayIcon</c>, after <see cref="ITrayIconManager.Initialize"/>. A no-op after
    /// <see cref="Dispose"/> too (L-4 fix, backlog 1.x wave 1 review) — this type has no legitimate
    /// "restart after dispose" use case, and without this guard, a <see cref="Start"/> call arriving
    /// after <see cref="Dispose"/> would re-subscribe everything onto a torn-down instance with no
    /// matching <see cref="Dispose"/> ever coming to unsubscribe it again.
    /// </summary>
    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        _started = true;
        _trayIconManager.OnNextPageRequested += OnNextPageRequested;
        _trayIconManager.OnPageSelected += OnPageSelected;
        _pageManager.PageChanged += OnPageChanged;
        _pageManager.PagesChanged += OnPagesChanged;
        _localization.CultureChanged += OnCultureChanged;
        PushPages();
    }

    /// <summary>
    /// Unsubscribes from every event <see cref="Start"/> wired. A deferred switch/refresh already queued
    /// before this call still runs (see <see cref="DeferToUiThread"/>/<see cref="DeferPagesRefreshToUiThread"/>'s
    /// remarks), but re-checks <see cref="_disposed"/> first so it never touches an
    /// <see cref="IPageManager"/> or <see cref="ITrayIconManager"/> a torn-down process no longer wants
    /// driven.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            _trayIconManager.OnNextPageRequested -= OnNextPageRequested;
            _trayIconManager.OnPageSelected -= OnPageSelected;
            _pageManager.PageChanged -= OnPageChanged;
            _pageManager.PagesChanged -= OnPagesChanged;
            _localization.CultureChanged -= OnCultureChanged;
        }
    }

    /// <summary>
    /// Production UI-thread deferral for a page switch. The tray's <c>NotifyIcon.DoubleClick</c>/menu
    /// <c>Click</c> callbacks already run on the WPF UI thread (the WinForms message loop shares it —
    /// backlog-1x-master-plan.md §1.1), so this is the "same-thread, defer to later in the queue" case
    /// (docs/Refactoring/Testing/sta-test-conventions.md rule 4), not a cross-thread marshal — it exists
    /// only to let the tray's own Click dispatch finish unwinding before <see cref="IPageManager.SwitchTo"/>
    /// starts showing/hiding panels, mirroring <c>App.ShowSettingsWindow</c>'s same reasoning.
    /// </summary>
    private static void DeferToUiThread(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

    /// <summary>
    /// Production UI-thread deferral for a "Pages" submenu rebuild — <see cref="DispatcherPriority.Background"/>,
    /// deliberately lower than <see cref="DeferToUiThread"/>'s <see cref="DispatcherPriority.Normal"/>
    /// (backlog-1x-master-plan.md §2.1): a page-item click chain runs
    /// Click → <see cref="ITrayIconManager.OnPageSelected"/> → (deferred, Normal) <see cref="IPageManager.SwitchTo"/>
    /// → <see cref="IPageManager.PageChanged"/> → <see cref="PushPages"/>. Rebuilding the very submenu
    /// whose item is still dispatching that Click — even one dispatcher tick later — is the same class of
    /// race <c>TrayIconManager</c>'s own MAJOR-1 fix guards against; queuing the rebuild at Background
    /// priority guarantees it only runs once the Normal-priority switch (and anything else already
    /// queued) has fully unwound.
    /// </summary>
    private static void DeferPagesRefreshToUiThread(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    private void OnNextPageRequested(object? sender, EventArgs e) => _defer(SwitchToNextPage);

    private void OnPageSelected(object? sender, string pageId) => _defer(() => SwitchToPage(pageId));

    private void OnPageChanged(string pageId) => PushPages();

    private void OnPagesChanged() => PushPages();

    private void OnCultureChanged(object? sender, CultureChangedEventArgs e) => PushPages();

    private void SwitchToNextPage()
    {
        // Guards the one gap Dispose's own unsubscribe cannot close: a double-click that arrived and was
        // already queued onto the UI thread's dispatcher a moment before Dispose ran (see Dispose's
        // remarks) must not still act once this adapter considers itself torn down.
        if (_disposed)
        {
            return;
        }

        var next = PageCycling.Next(_pageManager.Pages, _pageManager.CurrentPage);
        if (next is not null)
        {
            _pageManager.SwitchTo(next);
        }
    }

    private void SwitchToPage(string pageId)
    {
        // Same disposed guard as SwitchToNextPage's, plus: the clicked item can be stale — the submenu's
        // own rebuild is itself deferred (see DeferPagesRefreshToUiThread's remarks), so a click that
        // landed on a page removed a moment earlier is silently ignored rather than handed to
        // IPageManager.SwitchTo with an id it no longer recognizes.
        if (_disposed || !_pageManager.Pages.Contains(pageId, StringComparer.Ordinal))
        {
            return;
        }

        _pageManager.SwitchTo(pageId);
    }

    /// <summary>
    /// Builds the current "Pages" submenu snapshot from <see cref="IPageManager.PageDescriptors"/> —
    /// resolving each page's effective display name via <see cref="PageDisplayNames.Resolve"/> under
    /// <see cref="ILocalizationService.CurrentCulture"/>, and marking <see cref="IPageManager.CurrentPage"/>
    /// as current — and hands it to <see cref="ITrayIconManager.SetPages"/> through
    /// <see cref="_deferPagesRefresh"/> (backlog-1x-master-plan.md §2.1). Called from <see cref="Start"/>
    /// and on every <see cref="IPageManager.PageChanged"/>/<see cref="IPageManager.PagesChanged"/>/
    /// <see cref="ILocalizationService.CultureChanged"/>.
    /// </summary>
    private void PushPages()
    {
        var currentPage = _pageManager.CurrentPage;
        var culture = _localization.CurrentCulture;
        var entries = _pageManager.PageDescriptors
            .Select(descriptor => new TrayPageEntry(
                descriptor.Id,
                PageDisplayNames.Resolve(descriptor.Id, descriptor.Name, culture),
                string.Equals(descriptor.Id, currentPage, StringComparison.Ordinal)))
            .ToList();

        _deferPagesRefresh(() =>
        {
            if (!_disposed)
            {
                _trayIconManager.SetPages(entries);
            }
        });
    }
}
