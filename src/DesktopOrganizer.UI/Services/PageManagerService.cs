using System.Text.Json;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation of <see cref="IPageManager"/>.
/// Depends on <see cref="IWindowManager"/> to show/hide/remove panels and
/// on <see cref="IAppSettings"/> for persistence.
/// </summary>
public class PageManagerService : IPageManager
{
    private readonly IWindowManager _windowManager;
    private readonly IAppSettings _appSettings;
    private readonly List<string> _pages;
    private string _currentPage;

    /// <inheritdoc />
    public event Action<string>? PageChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> Pages => _pages;

    /// <inheritdoc />
    public string CurrentPage => _currentPage;

    /// <summary>
    /// Initializes a new instance of <see cref="PageManagerService"/>.
    /// Pages and the active page are loaded from <see cref="IAppSettings"/>.
    /// </summary>
    public PageManagerService(IWindowManager windowManager, IAppSettings appSettings)
    {
        _windowManager = windowManager;
        _appSettings = appSettings;

        _pages = LoadPages(appSettings.Pages);
        _currentPage = string.IsNullOrWhiteSpace(appSettings.CurrentPage)
            ? "default"
            : appSettings.CurrentPage;

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
        _appSettings.CurrentPage = pageId;
        _appSettings.Save();

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
    public void RemovePage(string pageId)
    {
        if (pageId == "default") return;
        if (!_pages.Contains(pageId)) return;

        // Close all panels belonging to this page
        var toRemove = _windowManager.GetAllChildWindows()
            .Where(m => m.PageId == pageId)
            .Select(m => m.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            // Deleting a page permanently destroys every panel on it, so release their app-managed
            // item copies too (see RemoveChildWindow remarks in WindowManager). deferSave: true avoids
            // one synchronous full-state write per removed panel (Phase 7 / Block C, M4) —
            // SaveWindowsStateNow() below flushes the debounced writes once for the whole page.
            _windowManager.RemoveChildWindow(id, releaseManagedItems: true, deferSave: true);
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
    }

    /// <inheritdoc />
    public void Save()
    {
        _appSettings.Pages = JsonSerializer.Serialize(_pages);
        _appSettings.CurrentPage = _currentPage;
        _appSettings.Save();
    }

    private void ApplyVisibility()
    {
        foreach (var model in _windowManager.GetAllChildWindows())
        {
            _windowManager.ApplyPanelVisibility(model);
        }
    }

    private static List<string> LoadPages(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ["default"];

        try
        {
            var pages = JsonSerializer.Deserialize<List<string>>(json);
            return pages is { Count: > 0 } ? pages : ["default"];
        }
        catch (JsonException)
        {
            return ["default"];
        }
    }

    private void EnsureDefaultPage()
    {
        if (!_pages.Contains("default"))
            _pages.Insert(0, "default");
    }
}
