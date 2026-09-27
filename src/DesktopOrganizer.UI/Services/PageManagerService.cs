using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.State;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation of <see cref="IPageManager"/>.
/// Depends on <see cref="IWindowManager"/> to show/hide/remove panels and
/// on <see cref="IApplicationStateService"/> for persistence.
/// </summary>
public class PageManagerService : IPageManager
{
    private readonly IWindowManager _windowManager;
    private readonly IApplicationStateService _applicationStateService;
    private readonly IConfirmationService _confirmation;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly ILocalizationService _localization;
    private readonly List<string> _pages;
    private string _currentPage;

    /// <inheritdoc />
    public event Action<string>? PageChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> Pages => _pages;

    /// <inheritdoc />
    public string CurrentPage => _currentPage;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageManagerService"/> class.
    /// Pages and the active page are read from the already-loaded in-memory application-state
    /// document (<see cref="IApplicationStateService.PageIds"/>/<see cref="IApplicationStateService.CurrentPageId"/>,
    /// ADR-024 C6) rather than parsed from settings directly — <c>ApplicationStateService.Load()</c>
    /// has already run, once, in <c>Program.Main</c> before this constructor ever gets called.
    /// </summary>
    /// <param name="windowManager">Shows/hides/removes panels.</param>
    /// <param name="applicationStateService">Persists the pages list and current page.</param>
    /// <param name="confirmation">
    /// Gates <see cref="RemovePage"/> (P08 fix cycle 1, CRITICAL C1, 2026-09-22) — deleting a page
    /// permanently destroys every panel on it and moves any app-managed item copies to the Recycle Bin
    /// (ADR-035), mirroring <c>MainWindowViewModel.RemovePanel</c>'s own confirm gate (ADR-010 Decision 2).
    /// Also shows the "could not move to the Recycle Bin" message when that fails (the page is kept).
    /// </param>
    /// <param name="itemStorage">
    /// Used to decide whether any panel on the page being removed actually has something to lose (see
    /// <paramref name="confirmation"/>).
    /// </param>
    /// <param name="localization">Localizes <see cref="RemovePage"/>'s confirmation dialog text.</param>
    public PageManagerService(
        IWindowManager windowManager,
        IApplicationStateService applicationStateService,
        IConfirmationService confirmation,
        IPanelItemStorageService itemStorage,
        ILocalizationService localization)
    {
        _windowManager = windowManager;
        _applicationStateService = applicationStateService;
        _confirmation = confirmation;
        _itemStorage = itemStorage;
        _localization = localization;

        _pages = [.. applicationStateService.PageIds];
        _currentPage = string.IsNullOrWhiteSpace(applicationStateService.CurrentPageId)
            ? "default"
            : applicationStateService.CurrentPageId;

        EnsureDefaultPage();
    }

    /// <inheritdoc />
    public void SwitchTo(string pageId)
    {
        if (pageId == _currentPage) return;
        if (!_pages.Contains(pageId)) return;

        // Hide panels of the current page, show panels of the new page
        foreach (var model in _windowManager.GetAllChildWindows())
        {
            if (model.PageId == _currentPage)
            {
                model.IsVisible = false;
            }
            else if (model.PageId == pageId)
            {
                model.IsVisible = true;
            }
        }

        // Apply visibility to actual windows
        ApplyVisibility();

        _currentPage = pageId;
        Save();

        PageChanged?.Invoke(pageId);
    }

    /// <inheritdoc />
    public void AddPage(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        if (_pages.Contains(pageId)) return;

        _pages.Add(pageId);
        Save();
    }

    /// <inheritdoc />
    /// <remarks>
    /// CRITICAL C1 fix (P08 fix cycle 1, 2026-09-22): deleting a page permanently destroys every panel
    /// on it and moves their app-managed items to the Recycle Bin — the same removal
    /// <c>MainWindowViewModel.RemovePanel</c> already gates behind a confirmation (ADR-010 Decision 2).
    /// This mirrors that gate at the page level: every live panel is synced to its model first (so a
    /// stale-original-path remap from <c>ChildWindowViewModel.LoadFromModel</c>, ADR-009 Amendment,
    /// is already reflected in <see cref="Models.ChildWindowModel.FilePaths"/> before it's inspected —
    /// otherwise this check could see "nothing managed" while the per-panel <c>SyncToModel</c> inside
    /// recycle step picks up the by-then-remapped managed path anyway), then the confirmation text/icon
    /// depends on whether ANY panel
    /// on the page actually has a managed file to lose, same warning-vs-neutral split as
    /// <c>MainWindowViewModel.RemovePanel</c>.
    /// <para>
    /// CRITICAL-1 fix (P08 fix cycle 2, 2026-09-22): this method's <see langword="bool"/> return now
    /// reports whether the confirmation gate above was actually granted (or the page didn't exist /
    /// was <c>"default"</c> to begin with). Before this fix the method was <see langword="void"/> and
    /// <c>MainWindowViewModel.RemovePage</c> unconditionally removed <paramref name="pageId"/> from its
    /// own <c>Pages</c> collection right after calling this method — including when the user clicked
    /// Cancel on the dialog above, at which point this method returns here with NO panel removed and
    /// NO page removed from <see cref="_pages"/>, yet the page still vanished from the UI's page
    /// switcher (and its panels, still alive but no longer reachable via any page, effectively hidden)
    /// until the app was restarted.
    /// </para>
    /// </remarks>
    public bool RemovePage(string pageId)
    {
        if (pageId == "default") return false;
        if (!_pages.Contains(pageId)) return false;

        _windowManager.SyncAllWindowsToModels();

        var pagePanels = _windowManager.GetAllChildWindows()
            .Where(m => m.PageId == pageId)
            .ToList();

        var hasManagedFiles = pagePanels.Any(m => m.FilePaths.Any(_itemStorage.IsManaged));
        var title = _localization.GetString("Page_Confirm_Remove_Title");

        var confirmed = hasManagedFiles
            ? _confirmation.Confirm(
                _localization.GetString("Page_Confirm_Remove_ManagedFiles_Message"),
                title,
                MessageBoxImage.Warning)
            : _confirmation.Confirm(
                _localization.GetString("Page_Confirm_Remove_Message"),
                title);

        if (!confirmed) return false;

        // ADR-035: every managed item of every panel on the page goes to the Recycle Bin in ONE batch
        // (one shell operation, at most one "can't recycle, delete permanently?" prompt). If anything
        // is still on disk afterwards, the page and ALL its panels are kept — only the items that did
        // reach the Recycle Bin are taken out of their panels — and the user is told why.
        var managedPaths = pagePanels
            .SelectMany(m => m.FilePaths)
            .Where(_itemStorage.IsManaged)
            .ToList();

        if (managedPaths.Count > 0)
        {
            var outcome = _itemStorage.ReleaseToRecycleBin(managedPaths);
            if (!outcome.IsComplete)
            {
                _windowManager.RemoveItemsFromPanels(outcome.Recycled);
                _confirmation.Notify(
                    _localization.Format("Recycle_Failed_Message_Format", outcome.Remaining.Count),
                    _localization.GetString("Recycle_Failed_Title"),
                    MessageBoxImage.Warning);
                return false;
            }
        }

        // Close all panels belonging to this page
        var toRemove = pagePanels.Select(m => m.Id).ToList();

        foreach (var id in toRemove)
        {
            // The managed items are already in the Recycle Bin (above), so releaseManagedItems: false.
            // deferSave: true avoids one synchronous full-state write per removed panel (Phase 7 /
            // Block C, M4) — SaveWindowsStateNow() below flushes the debounced writes once for the page.
            _windowManager.RemoveChildWindow(id, releaseManagedItems: false, deferSave: true);
        }

        if (toRemove.Count > 0)
        {
            _windowManager.SaveWindowsStateNow();
        }

        _pages.Remove(pageId);

        // If we removed the current page, switch to default
        if (_currentPage == pageId)
        {
            SwitchTo("default");
        }

        Save();

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Persists synchronously via <see cref="IApplicationStateService.SaveNow"/> — pages/current-page
    /// changes are structural (ADR-024 D-12), matching the pre-ADR-024 contract of always calling
    /// <c>IAppSettings.Save()</c> right away rather than debouncing.
    /// </remarks>
    public void Save() => _applicationStateService.SaveNow();

    private void ApplyVisibility()
    {
        foreach (var model in _windowManager.GetAllChildWindows())
        {
            _windowManager.ApplyPanelVisibility(model);
        }
    }

    private void EnsureDefaultPage()
    {
        if (!_pages.Contains("default"))
            _pages.Insert(0, "default");
    }
}
