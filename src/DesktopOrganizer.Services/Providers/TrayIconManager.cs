using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
public class TrayIconManager : ITrayIconManager
{
    // MAJOR-1 fix (Phase 6 review): the ContextMenuStrip (and its ToolStripMenuItem/ToolStripSeparator
    // children) is now built exactly ONCE and handed to the adapter a single time — see EnsureContextMenuBuilt.
    // UpdateContextMenu, which after P6-3 is invoked on every ModeChanged/IsLayoutLocked/
    // CanEnterEditLayout change (i.e. on every Edit Layout toggle from any entry point), only MUTATES
    // the already-live Checked/Enabled properties of these fields from here on. Previously it
    // constructed a brand-new ContextMenuStrip on every call and handed it to
    // INotifyIconAdapter.SetContextMenu, which just overwrites NotifyIcon.ContextMenuStrip — the
    // previous strip (a ToolStripDropDown : Control, owning a native HWND + GDI resources once shown)
    // was never Dispose()'d, leaking one per toggle. Rebuilding also raced with its own Click handler:
    // a menu item's Click closure could still be dispatching (the toggle → ModeChanged → menu rebuild
    // chain runs synchronously from inside that same Click) while the strip that raised it was being
    // replaced out from under it. Mutating fixed fields in place removes both problems.
    //
    // docs/PLANS/localization.md L-7/§2.7: the same "mutate in place, never rebuild" rule extends to
    // the hot language switch — ApplyTexts (below) only ever re-labels these same five fields'
    // Text/ToolTipText in response to ILocalizationService.CultureChanged; it never touches
    // EnsureContextMenuBuilt's one-time construction. Every item also gets a stable Name (below) so
    // tests — and any future caller — can find them independently of the currently-displayed,
    // language-dependent Text.

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Pages" submenu item.</summary>
    internal const string PagesItemName = "Tray.Pages";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Pages" submenu's "Next page" item.</summary>
    internal const string NextPageItemName = "Tray.NextPage";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> prefix of a per-page item in the "Pages" submenu — followed by the page's own id.</summary>
    internal const string PageItemNamePrefix = "Tray.Page:";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Settings" menu item.</summary>
    internal const string SettingsItemName = "Tray.Settings";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Edit layout" menu item.</summary>
    internal const string EditLayoutItemName = "Tray.EditLayout";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Lock layout" menu item.</summary>
    internal const string LockLayoutItemName = "Tray.LockLayout";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Show desktop icons" menu item.</summary>
    internal const string ShowDesktopIconsItemName = "Tray.ShowDesktopIcons";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Exit" menu item.</summary>
    internal const string ExitItemName = "Tray.Exit";

    /// <summary>The product name shown as the tray icon tooltip (and its read-only variant).</summary>
    internal const string ToolTipProductName = "DesktopOrganizer";

    private readonly INotifyIconAdapter _notifyIconAdapter;
    private readonly ILocalizationService _localization;
    private readonly ITrayMenuWindowStyler? _windowStyler;
    private readonly List<ToolStripMenuItem> _pageMenuItems = [];
    private ContextMenuStrip? _contextMenuStrip;
    private ToolStripMenuItem? _pagesItem;
    private ToolStripMenuItem? _nextPageItem;
    private ToolStripMenuItem? _settingsItem;
    private ToolStripMenuItem? _editLayoutItem;
    private ToolStripMenuItem? _lockLayoutItem;
    private ToolStripMenuItem? _showDesktopIconsItem;
    private ToolStripMenuItem? _exitItem;

    // The (Id, DisplayName) pairs SetPages built the current _pageMenuItems from, kept only to decide
    // whether the NEXT SetPages call can mutate Checked in place or must dispose-and-rebuild — see
    // SetPages' remarks. Deliberately not the full IsCurrent-bearing TrayPageEntry list: comparing on it
    // too would make a mere active-page switch (the common case, on every SwitchTo) look like a
    // composition change and rebuild the items for no reason.
    private IReadOnlyList<TrayPageEntry> _lastPageComposition = [];

    // The last canEnterEditLayout snapshot UpdateContextMenu received. ApplyTexts needs it to decide
    // whether "Edit layout"'s ToolTipText should be the localized Tray_LayoutLockedTooltip text or empty
    // (docs/PLANS/localization.md §2.7) — a language switch alone must re-render the tooltip in the new
    // language without requiring the caller to invoke UpdateContextMenu again just because the language
    // changed.
    private bool _canEnterEditLayout = true;

    // ADR-audit WP-B (F5): the last SetReadOnlySession snapshot — decides whether the tray tooltip carries
    // the localized "(read-only)" suffix. Re-applied on a language switch (OnCultureChanged).
    private bool _isReadOnlySession;

    /// <summary>
    /// LOW-2 fix (code review, panel clone to page, backlog 1.x wave 3 review): set by <see cref="Dispose"/>.
    /// Guards <see cref="SetPages"/>/<see cref="UpdateContextMenu"/> against running after disposal — both
    /// call <see cref="EnsureContextMenuBuilt"/>, which would otherwise resurrect a brand-new
    /// <see cref="ContextMenuStrip"/> (and hand it to the adapter) right after <see cref="Dispose"/> tore
    /// the previous one down.
    /// </summary>
    private bool _disposed;

    // Design system 1.x §2.9 variant A (ADR-047 D-10) — the menu style state. SetMenuPalette only records
    // the request (_requestedPalette/_hasPendingPalette); ApplyPendingPalette turns it into _menuRenderer and
    // assigns that renderer to every strip — immediately when the menu is closed, otherwise from the next
    // Opening. _hasAppliedPalette distinguishes "never styled" (legacy stock look, no metrics) from an
    // applied null palette (high contrast → ToolStripSystemRenderer).
    private ToolStripRenderer? _menuRenderer;
    private TrayMenuPalette? _appliedPalette;
    private bool _hasAppliedPalette;
    private TrayMenuPalette? _requestedPalette;
    private bool _hasPendingPalette;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManager"/> class without DWM popup styling
    /// (square menu corners) — see the three-argument overload for the production shape.
    /// </summary>
    /// <param name="notifyIconAdapter">The tray icon/context-menu adapter.</param>
    /// <param name="localization">
    /// The hot-switchable localization service (docs/PLANS/localization.md §2.7). Subscribed to for the
    /// lifetime of this instance — see <see cref="Dispose"/> for the matching unsubscribe.
    /// </param>
    public TrayIconManager(INotifyIconAdapter notifyIconAdapter, ILocalizationService localization)
        : this(notifyIconAdapter, localization, windowStyler: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManager"/> class — the overload the DI container
    /// selects (the longest satisfiable one).
    /// </summary>
    /// <param name="notifyIconAdapter">The tray icon/context-menu adapter.</param>
    /// <param name="localization">
    /// The hot-switchable localization service (docs/PLANS/localization.md §2.7). Subscribed to for the
    /// lifetime of this instance — see <see cref="Dispose"/> for the matching unsubscribe.
    /// </param>
    /// <param name="windowStyler">
    /// Applies the Windows 11 rounded corners/border colour to each menu popup window once it has a handle
    /// (design system 1.x §2.9); <see langword="null"/> keeps the square system popups.
    /// </param>
    public TrayIconManager(INotifyIconAdapter notifyIconAdapter, ILocalizationService localization, ITrayMenuWindowStyler? windowStyler)
    {
        _notifyIconAdapter = notifyIconAdapter;
        _localization = localization;
        _windowStyler = windowStyler;
        _localization.CultureChanged += OnCultureChanged;
    }

    /// <inheritdoc />
    public event EventHandler? OnExitRequested;

    /// <inheritdoc />
    public event EventHandler? OnShowMainWindowRequested;

    /// <inheritdoc />
    public event EventHandler? OnNextPageRequested;

    /// <inheritdoc />
    public event EventHandler<string>? OnPageSelected;

    /// <inheritdoc />
    public event EventHandler? OnEditLayoutToggleRequested;

    /// <inheritdoc />
    public event EventHandler? OnLockLayoutToggleRequested;

    /// <inheritdoc />
    public event EventHandler? ToggleDesktopIconsRequested;

    /// <summary>
    /// Absolute path of the tray icon, anchored at the application's own install directory
    /// (<see cref="AppContext.BaseDirectory"/>). It used to be the relative <c>Resources/app.ico</c>,
    /// which .NET resolves against the process's <em>current working directory</em> — correct only
    /// when the app is started from its install folder (installer's post-install launch, Start-menu
    /// shortcut). Started from anywhere else — the HKCU <c>Run</c> autostart entry at logon, a
    /// terminal, Win+R — the file was "not found" and startup aborted with
    /// "DesktopOrganizer failed to start: Icon file not found".
    /// </summary>
    internal static string TrayIconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");

    /// <summary>
    /// Gets or sets the "is the menu currently shown" probe <see cref="SetMenuPalette"/> consults before
    /// restyling in place (default: the root strip's <see cref="Control.Visible"/>). Test seam only — a
    /// headless test cannot open a real tray menu.
    /// </summary>
    internal Func<ToolStrip, bool> IsMenuOpen { get; set; } = static strip => strip.Visible;

    /// <summary>
    /// Gets the renderer currently assigned to every menu strip by <see cref="SetMenuPalette"/>;
    /// <see langword="null"/> until the first call.
    /// </summary>
    internal ToolStripRenderer? MenuRenderer => _menuRenderer;

    /// <inheritdoc />
    public void Initialize()
    {
        _notifyIconAdapter.SetIcon(TrayIconPath);
        ApplyToolTip();

        // Settings window starts hidden (app lives in the tray) — see ADR-002. Edit layout/Lock layout
        // start unchecked/enabled — the caller (App.StartApp) immediately refreshes this with the real
        // ILayoutEditModeService snapshot right after wiring the new events (P6-3), same as this method
        // has always been re-invoked to reflect state changes rather than only ever called once.
        UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        _notifyIconAdapter.DoubleClick += (_, _) => NextPageRequested();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _localization.CultureChanged -= OnCultureChanged;
        _notifyIconAdapter.Dispose();
        foreach (var item in _pageMenuItems)
        {
            item.Click -= PageItemClicked;
        }

        _pageMenuItems.Clear();
        _lastPageComposition = [];
        _contextMenuStrip?.Dispose();
        _contextMenuStrip = null;

        // Review L-7: the renderer caches glyph fonts — release them with the strips that used it.
        (_menuRenderer as IDisposable)?.Dispose();
        _menuRenderer = null;
        _appliedPalette = null;
        _requestedPalette = null;
        _hasAppliedPalette = false;
        _hasPendingPalette = false;
        _pagesItem = null;
        _nextPageItem = null;
        _settingsItem = null;
        _editLayoutItem = null;
        _lockLayoutItem = null;
        _showDesktopIconsItem = null;
        _exitItem = null;
    }

    /// <inheritdoc />
    public void UpdateContextMenu(bool isMainWindowVisible, bool isEditLayoutChecked, bool canEnterEditLayout, bool isLockLayoutChecked)
    {
        // LOW-2 fix: a stray call reaching this instance after Dispose() (e.g. a lifecycle-ordering race
        // during shutdown) must not resurrect the menu strip — see _disposed's own remarks.
        if (_disposed) return;

        // Lazily builds+hands off the strip on the very first call (from either Initialize or a caller
        // that invokes UpdateContextMenu directly), and is a no-op on every call after that — see the
        // MAJOR-1 remarks on the fields above.
        EnsureContextMenuBuilt();

        // D-9 #1 — the guaranteed, WS_EX_NOACTIVATE-independent entry point for the global layout edit
        // mode. CheckOnClick is deliberately false: this layer doesn't own the checked state (see
        // OnEditLayoutToggleRequested's remarks) — the item's Checked/Enabled are refreshed from a fresh
        // snapshot on every real state change instead of the ToolStripMenuItem flipping its own Checked
        // locally.
        _editLayoutItem!.Checked = isEditLayoutChecked;

        // D-3: entering Edit Layout is refused while the layout is locked — disable the item rather
        // than let a click silently no-op. D-9 #1 (plan D-9 §1, MINOR-8 fix-cycle round 2): explain
        // WHY it's disabled via a tooltip rather than leaving a mysteriously-greyed-out item.
        _canEnterEditLayout = canEnterEditLayout;
        _editLayoutItem.Enabled = canEnterEditLayout;
        _editLayoutItem.ToolTipText = canEnterEditLayout ? string.Empty : _localization.GetString("Tray_LayoutLockedTooltip");

        // D-3: "Lock layout" itself must stay clickable at all times — it's the only way to unlock again.
        _lockLayoutItem!.Checked = isLockLayoutChecked;
    }

    /// <inheritdoc />
    public void SetDesktopIconsVisible(bool visible)
    {
        // Review L-6 (LOW-2 fix shape): see _disposed's own remarks.
        if (_disposed) return;

        EnsureContextMenuBuilt();
        _showDesktopIconsItem!.Checked = visible;
    }

    /// <inheritdoc />
    public void SetReadOnlySession(bool isReadOnly)
    {
        if (_disposed) return;

        _isReadOnlySession = isReadOnly;
        ApplyToolTip();
    }

    /// <inheritdoc />
    public void SetPages(IReadOnlyList<TrayPageEntry> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        // LOW-2 fix: see _disposed's own remarks.
        if (_disposed) return;

        EnsureContextMenuBuilt();

        if (!HasSameComposition(_lastPageComposition, pages))
        {
            // Composition (Id/DisplayName sequence) actually changed — dispose the stale per-page items
            // (MAJOR-1: this layer never lets a rebuilt collection of ToolStripItems leak) and build fresh
            // ones. _nextPageItem and the separator preceding them are untouched — they were built once by
            // EnsureContextMenuBuilt and outlive every SetPages call.
            foreach (var item in _pageMenuItems)
            {
                _pagesItem!.DropDownItems.Remove(item);
                item.Click -= PageItemClicked;
                item.Dispose();
            }

            _pageMenuItems.Clear();

            foreach (var page in pages)
            {
                var item = new ToolStripMenuItem
                {
                    Name = PageItemNamePrefix + page.Id,

                    // M-F fix (code review, panel clone to page, backlog 1.x wave 3 review): WinForms
                    // treats a single "&" in ToolStripItem.Text as a mnemonic-accelerator prefix (the
                    // following character gets underlined and Alt+that-key activates the item) — a page
                    // named e.g. "R&D" would otherwise silently lose its "&" and gain an unintended
                    // accelerator. Doubling it ("&&") renders a literal "&", the standard WinForms escape.
                    Text = EscapeAmpersand(page.DisplayName),
                    Tag = page.Id,
                    Checked = page.IsCurrent,
                    CheckOnClick = false,
                };
                item.Click += PageItemClicked;
                _pagesItem!.DropDownItems.Add(item);
                _pageMenuItems.Add(item);
            }
        }
        else
        {
            // Same pages, same order, same names — only the checkmark (which page is current) can have
            // changed. Mutate the existing instances in place instead of rebuilding them, same rule
            // UpdateContextMenu follows for the layout-edit items (see the MAJOR-1 remarks at the top of
            // this file).
            for (var i = 0; i < pages.Count; i++)
            {
                _pageMenuItems[i].Checked = pages[i].IsCurrent;
            }
        }

        // LOW-1 fix (code review, panel clone to page, backlog 1.x wave 3 review): a defensive copy —
        // _lastPageComposition is compared against on every FUTURE call (HasSameComposition), so it must
        // never alias a caller-owned list that could be mutated in place after this call returns.
        _lastPageComposition = [.. pages];
        _nextPageItem!.Enabled = pages.Count > 1;

        // Design system 1.x §2.9: freshly built page items get the same row metrics as the rest of the menu
        // (the drop-down keeps its renderer across rebuilds — it is the same ToolStripDropDown instance —
        // but RefreshMenuStyle re-asserts it anyway, idempotently).
        RefreshMenuStyle(applyWindowStyle: false);
    }

    /// <inheritdoc />
    public void SetMenuPalette(TrayMenuPalette? palette)
    {
        // LOW-2 fix shape: see _disposed's own remarks.
        if (_disposed) return;

        EnsureContextMenuBuilt();

        _requestedPalette = palette;
        _hasPendingPalette = true;

        // Never swap the renderer (and relayout) under an open menu — the next Opening applies it.
        if (IsMenuOpen(_contextMenuStrip!)) return;

        ApplyPendingPalette();
    }

    /// <summary>
    /// Runs the root menu's <see cref="ToolStripDropDown.Opening"/> logic — applies a palette requested
    /// while the menu was open, then re-asserts the row metrics for the (possibly changed) DPI. Internal
    /// so tests can drive it without showing a real menu.
    /// </summary>
    internal void HandleMenuOpening()
    {
        if (_disposed || _contextMenuStrip is null) return;

        ApplyPendingPalette();
        if (_hasAppliedPalette)
        {
            TrayMenuRenderer.ApplyLayoutMetrics(_contextMenuStrip);
        }
    }

    /// <summary>
    /// Runs a strip's <see cref="ToolStripDropDown.Opened"/> logic — re-asserts the row metrics at the
    /// strip's <see cref="Control.DeviceDpi"/> once the popup is actually on screen. Internal so tests can
    /// drive it without showing a real menu.
    /// </summary>
    /// <remarks>
    /// Review 6.4 (per-monitor DPI): <see cref="HandleMenuOpening"/> runs before the popup is positioned, so
    /// when the taskbar/cursor moved to a monitor with a different DPI since the last show, the DPI it reads
    /// can still be the previous monitor's. The renderer itself reads the DPI at paint time (always current);
    /// only the row padding is layout-time. <see cref="TrayMenuRenderer.ApplyLayoutMetrics"/> is idempotent,
    /// so this is a no-op unless the DPI really changed. Manual verify (Win11, mixed-DPI monitors): open the
    /// tray menu on a 100 % monitor, then on a 150 % one — rows are 30 DIP tall on both, including "Pages".
    /// </remarks>
    /// <param name="strip">The strip that was just shown (root context menu or a submenu drop-down).</param>
    internal void HandleMenuStripOpened(ToolStrip strip)
    {
        ArgumentNullException.ThrowIfNull(strip);

        if (_disposed || !_hasAppliedPalette) return;

        TrayMenuRenderer.ApplyLayoutMetrics(strip);
    }

    /// <summary>
    /// Every strip of the menu: the root <see cref="ContextMenuStrip"/> and, recursively, the
    /// <see cref="ToolStripDropDown"/> of every item that has sub-items (today: "Pages").
    /// </summary>
    /// <returns>The strips, root first; empty before the menu is built.</returns>
    internal IEnumerable<ToolStrip> MenuStrips()
    {
        if (_contextMenuStrip is null) yield break;

        var pending = new Stack<ToolStrip>();
        pending.Push(_contextMenuStrip);
        while (pending.Count > 0)
        {
            var strip = pending.Pop();
            yield return strip;

            foreach (ToolStripItem item in strip.Items)
            {
                if (item is ToolStripDropDownItem { HasDropDownItems: true } dropDownItem)
                {
                    pending.Push(dropDownItem.DropDown);
                }
            }
        }
    }

    /// <summary>
    /// M-F fix (code review, panel clone to page, backlog 1.x wave 3 review): doubles every literal
    /// <c>&amp;</c> in <paramref name="text"/> so WinForms renders it as-is instead of treating it as a
    /// mnemonic-accelerator prefix. <see cref="HasSameComposition"/> and every other comparison against a
    /// <see cref="TrayPageEntry.DisplayName"/> deliberately keep using the RAW, unescaped value — only the
    /// rendered <see cref="ToolStripItem.Text"/> needs escaping.
    /// </summary>
    /// <param name="text">The raw display text.</param>
    private static string EscapeAmpersand(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private static bool HasSameComposition(IReadOnlyList<TrayPageEntry> previous, IReadOnlyList<TrayPageEntry> next)
    {
        if (previous.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < previous.Count; i++)
        {
            if (!string.Equals(previous[i].Id, next[i].Id, StringComparison.Ordinal) ||
                !string.Equals(previous[i].DisplayName, next[i].DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Turns a pending <see cref="SetMenuPalette"/> request into the renderer in effect — a no-op when the
    /// requested palette equals the one already applied (record value equality).
    /// </summary>
    private void ApplyPendingPalette()
    {
        if (!_hasPendingPalette) return;

        _hasPendingPalette = false;
        var palette = _requestedPalette;
        if (_hasAppliedPalette && Equals(_appliedPalette, palette)) return;

        _appliedPalette = palette;
        _hasAppliedPalette = true;

        // High contrast (null) → the stock system renderer, which draws in the live system colours.
        var previous = _menuRenderer;
        _menuRenderer = palette is null ? new ToolStripSystemRenderer() : new TrayMenuRenderer(palette);
        RefreshMenuStyle(applyWindowStyle: true);

        // Review L-7: every strip now uses the new renderer (a renderer never swaps under an open menu, see
        // SetMenuPalette), so the replaced one's cached glyph fonts can go.
        (previous as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Assigns <see cref="_menuRenderer"/> to every strip (WinForms never propagates a custom renderer to
    /// drop-downs), applies the row metrics and — when <paramref name="applyWindowStyle"/> — the DWM popup
    /// style to every strip that already has a window. Assigning a renderer invalidates the strip, which
    /// is the repaint. A no-op before the first <see cref="SetMenuPalette"/>.
    /// </summary>
    /// <param name="applyWindowStyle">Whether to (re)apply the DWM popup style to strips that have a window.</param>
    private void RefreshMenuStyle(bool applyWindowStyle)
    {
        if (!_hasAppliedPalette || _menuRenderer is null) return;

        foreach (var strip in MenuStrips())
        {
            if (!ReferenceEquals(strip.Renderer, _menuRenderer))
            {
                strip.Renderer = _menuRenderer;
            }

            TrayMenuRenderer.ApplyLayoutMetrics(strip);

            if (applyWindowStyle && strip.IsHandleCreated)
            {
                _windowStyler?.Apply(strip.Handle, _appliedPalette);
            }
        }
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e) => HandleMenuOpening();

    private void OnMenuStripOpened(object? sender, EventArgs e)
    {
        if (sender is ToolStrip strip)
        {
            HandleMenuStripOpened(strip);
        }
    }

    private void OnSubmenuOpening(object? sender, EventArgs e)
    {
        if (_hasAppliedPalette && sender is ToolStripDropDownItem { HasDropDownItems: true } item)
        {
            TrayMenuRenderer.ApplyLayoutMetrics(item.DropDown);
        }
    }

    private void OnMenuStripHandleCreated(object? sender, EventArgs e)
    {
        // A popup gets its window lazily on first show (and again after a handle recreation) — style it then.
        if (_hasAppliedPalette && sender is ToolStrip strip)
        {
            _windowStyler?.Apply(strip.Handle, _appliedPalette);
        }
    }

    private void EnsureContextMenuBuilt()
    {
        if (_contextMenuStrip is not null)
        {
            return;
        }

        var contextMenu = new ContextMenuStrip();

        // backlog-1x-master-plan.md §2 (ADR-044): "Pages" is the first item, built once here — its
        // "Next page" entry (raises the same OnNextPageRequested as the tray icon's double-click, see
        // NextPageRequested) and the separator beneath it never change; SetPages only (re)builds the
        // per-page entries appended after that separator.
        var pagesItem = new ToolStripMenuItem { Name = PagesItemName };
        var nextPageItem = new ToolStripMenuItem { Name = NextPageItemName, CheckOnClick = false, Enabled = false };
        nextPageItem.Click += (_, _) => NextPageRequested();
        pagesItem.DropDownItems.Add(nextPageItem);
        pagesItem.DropDownItems.Add(new ToolStripSeparator());
        contextMenu.Items.Add(pagesItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        // Always "Settings" — clicking it is an explicit "open the window" action, never a hide.
        // (Double-clicking the tray icon is the separate round-robin page switch — see NextPageRequested.)
        var settingsItem = new ToolStripMenuItem { Name = SettingsItemName };
        settingsItem.Click += ShowMainWindowRequested;
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        var editLayoutItem = new ToolStripMenuItem { Name = EditLayoutItemName, CheckOnClick = false };
        editLayoutItem.Click += EditLayoutToggleRequested;
        contextMenu.Items.Add(editLayoutItem);

        var lockLayoutItem = new ToolStripMenuItem { Name = LockLayoutItemName, CheckOnClick = false };
        lockLayoutItem.Click += LockLayoutToggleRequested;
        contextMenu.Items.Add(lockLayoutItem);

        // D-6: same event-plus-refresh shape as Edit layout/Lock layout above — CheckOnClick is
        // deliberately false because this item's checked state must also track the desktop double-click
        // gesture, a second entry point this menu item's own click can never see (see
        // ToggleDesktopIconsRequested's and SetDesktopIconsVisible's remarks).
        var showDesktopIconsItem = new ToolStripMenuItem { Name = ShowDesktopIconsItemName, CheckOnClick = false };
        showDesktopIconsItem.Click += DesktopIconsToggleRequested;
        contextMenu.Items.Add(showDesktopIconsItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem { Name = ExitItemName };
        exitItem.Click += ExitRequested;
        contextMenu.Items.Add(exitItem);

        _pagesItem = pagesItem;
        _nextPageItem = nextPageItem;
        _settingsItem = settingsItem;
        _editLayoutItem = editLayoutItem;
        _lockLayoutItem = lockLayoutItem;
        _showDesktopIconsItem = showDesktopIconsItem;
        _exitItem = exitItem;
        _contextMenuStrip = contextMenu;

        // Design system 1.x §2.9: palette/metrics re-assertion before every show, and DWM popup styling as
        // soon as each popup (root and "Pages") gets its window.
        contextMenu.Opening += OnMenuOpening;
        contextMenu.Opened += OnMenuStripOpened;
        contextMenu.HandleCreated += OnMenuStripHandleCreated;
        pagesItem.DropDownOpening += OnSubmenuOpening;
        pagesItem.DropDown.Opened += OnMenuStripOpened;
        pagesItem.DropDown.HandleCreated += OnMenuStripHandleCreated;

        // docs/PLANS/localization.md L-7/§2.7: set the initial (startup-culture) Text/ToolTipText right
        // after construction — ApplyTexts is otherwise only ever invoked in response to
        // ILocalizationService.CultureChanged, which never fires just from constructing the service.
        ApplyTexts();

        // Assigned to the adapter exactly once — subsequent UpdateContextMenu calls mutate these
        // items in place instead of building (and handing off) a new strip.
        _notifyIconAdapter.SetContextMenu(contextMenu);
    }

    /// <summary>
    /// Re-labels the seven tray menu items (and the disabled "Edit layout" tooltip, if currently shown)
    /// to <see cref="ILocalizationService.CurrentCulture"/>'s text — in place, on the very same
    /// <see cref="ToolStripMenuItem"/> instances <see cref="EnsureContextMenuBuilt"/> created once
    /// (docs/PLANS/localization.md L-7: the strip itself is never rebuilt or re-handed to the adapter).
    /// Per-page items' own <see cref="ToolStripItem.Text"/> is data, not chrome — <see cref="SetPages"/>
    /// sets it, this method never touches it (backlog-1x-master-plan.md §2.1).
    /// </summary>
    private void ApplyTexts()
    {
        _pagesItem!.Text = _localization.GetString("Tray_Pages");
        _nextPageItem!.Text = _localization.GetString("Tray_NextPage");
        _settingsItem!.Text = _localization.GetString("Tray_Settings");
        _editLayoutItem!.Text = _localization.GetString("Common_EditLayout");
        _editLayoutItem.ToolTipText = _canEnterEditLayout ? string.Empty : _localization.GetString("Tray_LayoutLockedTooltip");
        _lockLayoutItem!.Text = _localization.GetString("Common_LockLayout");
        _showDesktopIconsItem!.Text = _localization.GetString("Tray_ShowDesktopIcons");
        _exitItem!.Text = _localization.GetString("Tray_Exit");
    }

    /// <summary>
    /// Sets the tray icon tooltip: the product name, plus the localized "(read-only)" suffix while the
    /// session is read-only (<see cref="SetReadOnlySession"/>).
    /// </summary>
    private void ApplyToolTip()
    {
        var text = _isReadOnlySession
            ? _localization.Format("Tray_Tooltip_ReadOnly_Format", ToolTipProductName)
            : ToolTipProductName;
        _notifyIconAdapter.SetToolTip(text);
    }

    private void OnCultureChanged(object? sender, CultureChangedEventArgs e)
    {
        if (!_disposed) ApplyToolTip();

        // Guard rather than assert: EnsureContextMenuBuilt only runs lazily on the first
        // Initialize/UpdateContextMenu/SetDesktopIconsVisible call, so a culture change that lands
        // before any of those (unlikely in production — App.StartApp applies the language before
        // InitTrayIcon — but not impossible in a test double) must not NRE on the not-yet-built items.
        if (_contextMenuStrip is not null)
        {
            ApplyTexts();
        }
    }

    private void NextPageRequested()
        => OnNextPageRequested?.Invoke(this, EventArgs.Empty);

    private void PageItemClicked(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem { Tag: string id })
        {
            OnPageSelected?.Invoke(this, id);
        }
    }

    private void ShowMainWindowRequested(object? sender, EventArgs e)
        => OnShowMainWindowRequested?.Invoke(sender, e);

    private void ExitRequested(object? sender, EventArgs e)
        => OnExitRequested?.Invoke(sender, e);

    private void EditLayoutToggleRequested(object? sender, EventArgs e)
        => OnEditLayoutToggleRequested?.Invoke(sender, e);

    private void LockLayoutToggleRequested(object? sender, EventArgs e)
        => OnLockLayoutToggleRequested?.Invoke(sender, e);

    private void DesktopIconsToggleRequested(object? sender, EventArgs e)
        => ToggleDesktopIconsRequested?.Invoke(sender, e);
}
