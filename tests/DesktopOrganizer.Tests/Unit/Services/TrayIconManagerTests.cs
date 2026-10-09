using System.Globalization;
using System.IO;
using System.Windows.Forms;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Localization;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="TrayIconManager"/> — in particular the P6-3 "Edit layout"/"Lock layout"
/// checkable tray menu items (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #1) and the hot language switch
/// (docs/PLANS/localization.md L-7/§2.7). This layer has no visibility into <c>ILayoutEditModeService</c>
/// (it lives in DesktopOrganizer.UI, above this project in the layering) — everything checkable-state
/// related is a caller-supplied snapshot passed into <see cref="ITrayIconManager.UpdateContextMenu"/>,
/// and every user click is surfaced as an event for the caller (DesktopOrganizer.UI's App.xaml.cs) to
/// translate. <see cref="INotifyIconAdapter"/> is mocked throughout — the real
/// <c>NotifyIconAdapter</c>/<see cref="NotifyIcon"/> wrapper is a thin, headless-untestable shell around
/// the actual Win32 tray icon (manual verify only, see this task's report).
///
/// Items are located by their stable <see cref="ToolStripItem.Name"/> (<see cref="TrayIconManager.SettingsItemName"/>
/// and friends), never by <see cref="ToolStripItem.Text"/> — L-7's whole point is that Text changes on
/// every language switch while the strip itself, and each item's identity, never does.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class TrayIconManagerTests
{
    private readonly Mock<INotifyIconAdapter> _notifyIconAdapterMock = new();
    private readonly LocalizationService _localization = new(CultureInfo.GetCultureInfo("en"));
    private readonly TrayIconManager _sut;

    // MINOR-6 fix (fix-cycle round 2): the SetContextMenu callback is wired ONCE here, in the
    // constructor, and cached into this field — not re-Setup per call inside CaptureMenuFromUpdate
    // (below) as it was before. The old per-call Setup only ever populated a local `captured` variable
    // that was thrown away when the method returned, so a SECOND UpdateContextMenu call on the same
    // SUT (needed by the identity/Dispose tests below, which must call UpdateContextMenu at least
    // twice on one instance) reused the ORIGINAL callback closure and left the second call's local
    // `captured` stuck at null — `return captured!` on that second call was an NRE waiting to happen.
    // A field survives across calls, so every SetContextMenu invocation (there is at most one per SUT
    // instance now, per the MAJOR-1 fix — see TrayIconManager.EnsureContextMenuBuilt) updates the same
    // place every test can read from.
    private ContextMenuStrip? _capturedMenu;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManagerTests"/> class.
    /// </summary>
    public TrayIconManagerTests()
    {
        _notifyIconAdapterMock
            .Setup(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()))
            .Callback<ContextMenuStrip>(menu => _capturedMenu = menu);

        _sut = new TrayIconManager(_notifyIconAdapterMock.Object, _localization);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.Initialize"/> sets the tray icon from an absolute,
    /// working-directory-independent path and builds the context menu with "Edit layout" unchecked and
    /// enabled, and "Lock layout" unchecked.
    /// </summary>
    [Fact]
    public void Initialize_SetsIcon_AndBuildsMenuWithEditLayoutUncheckedEnabled_AndLockLayoutUnchecked()
    {
        ContextMenuStrip? built = null;
        _notifyIconAdapterMock
            .Setup(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()))
            .Callback<ContextMenuStrip>(menu => built = menu);

        _sut.Initialize();

        _notifyIconAdapterMock.Verify(a => a.SetIcon(TrayIconManager.TrayIconPath), Times.Once);
        Assert.True(Path.IsPathRooted(TrayIconManager.TrayIconPath), "Tray icon path must not depend on the process working directory.");
        Assert.StartsWith(AppContext.BaseDirectory, TrayIconManager.TrayIconPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Resources", "app.ico"), TrayIconManager.TrayIconPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(built);
        var editLayout = FindItem(built!, TrayIconManager.EditLayoutItemName);
        var lockLayout = FindItem(built!, TrayIconManager.LockLayoutItemName);
        Assert.False(editLayout.Checked);
        Assert.True(editLayout.Enabled);
        Assert.False(lockLayout.Checked);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.UpdateContextMenu"/> reflects the given
    /// checked/enabled snapshot onto both the "Edit layout" and "Lock layout" menu items.
    /// </summary>
    /// <param name="isEditLayoutChecked">Whether "Edit layout" should render checked.</param>
    /// <param name="canEnterEditLayout">Whether "Edit layout" should render enabled.</param>
    /// <param name="isLockLayoutChecked">Whether "Lock layout" should render checked.</param>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void UpdateContextMenu_ReflectsTheGivenSnapshot_ForBothCheckableItems(
        bool isEditLayoutChecked, bool canEnterEditLayout, bool isLockLayoutChecked)
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked, canEnterEditLayout, isLockLayoutChecked);

        var editLayout = FindItem(menu, TrayIconManager.EditLayoutItemName);
        var lockLayout = FindItem(menu, TrayIconManager.LockLayoutItemName);

        Assert.Equal(isEditLayoutChecked, editLayout.Checked);
        Assert.Equal(canEnterEditLayout, editLayout.Enabled);
        Assert.Equal(isLockLayoutChecked, lockLayout.Checked);
    }

    /// <summary>
    /// Verifies D-3: "Lock layout" stays enabled even when "Edit layout" is disabled — it is the only
    /// way to unlock again.
    /// </summary>
    [Fact]
    public void UpdateContextMenu_LockLayoutItem_AlwaysEnabled_RegardlessOfCanEnterEditLayout()
    {
        // D-3: "Lock layout" must stay clickable at all times — it's the only way to unlock again.
        var menuWhenDisabledEditLayout = CaptureMenuFromUpdate(
            isEditLayoutChecked: false, canEnterEditLayout: false, isLockLayoutChecked: true);

        var lockLayout = FindItem(menuWhenDisabledEditLayout, TrayIconManager.LockLayoutItemName);

        Assert.True(lockLayout.Enabled);
    }

    /// <summary>
    /// Verifies that the "Settings" and "Exit" menu items remain present alongside the checkable layout
    /// items after <see cref="TrayIconManager.UpdateContextMenu"/>.
    /// </summary>
    [Fact]
    public void UpdateContextMenu_KeepsSettingsAndExitItems_Present()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);

        Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Name == TrayIconManager.SettingsItemName);
        Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Name == TrayIconManager.ExitItemName);
    }

    /// <summary>
    /// D-6: verifies the "Show desktop icons" item is present, positioned between "Lock layout" and the
    /// separator preceding "Exit".
    /// </summary>
    [Fact]
    public void ContextMenu_ShowDesktopIconsItem_PresentBetweenLockLayoutAndExitSeparator()
    {
        _sut.Initialize();
        var items = _capturedMenu!.Items.Cast<ToolStripItem>().ToList();

        var lockLayoutIndex = items.FindIndex(i => i.Name == TrayIconManager.LockLayoutItemName);
        var showDesktopIconsIndex = items.FindIndex(i => i.Name == TrayIconManager.ShowDesktopIconsItemName);
        var exitIndex = items.FindIndex(i => i.Name == TrayIconManager.ExitItemName);

        Assert.True(lockLayoutIndex >= 0 && showDesktopIconsIndex >= 0 && exitIndex >= 0);
        Assert.True(showDesktopIconsIndex > lockLayoutIndex);
        Assert.True(showDesktopIconsIndex < exitIndex);
        Assert.IsType<ToolStripSeparator>(items[exitIndex - 1]);
    }

    /// <summary>
    /// Verifies that clicking the "Show desktop icons" menu item raises
    /// <see cref="TrayIconManager.ToggleDesktopIconsRequested"/>.
    /// </summary>
    [Fact]
    public void ShowDesktopIconsItem_Click_RaisesToggleDesktopIconsRequested()
    {
        _sut.Initialize();
        var raised = false;
        _sut.ToggleDesktopIconsRequested += (_, _) => raised = true;

        FindItem(_capturedMenu!, TrayIconManager.ShowDesktopIconsItemName).PerformClick();

        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.SetDesktopIconsVisible"/> reflects the given value onto
    /// the "Show desktop icons" item's checkmark, independently of the layout-edit snapshot
    /// <see cref="TrayIconManager.UpdateContextMenu"/> owns.
    /// </summary>
    /// <param name="visible">The visibility value to set and expect reflected on the checkmark.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetDesktopIconsVisible_ReflectsValue_OnShowDesktopIconsItem(bool visible)
    {
        _sut.Initialize();

        _sut.SetDesktopIconsVisible(visible);

        var item = FindItem(_capturedMenu!, TrayIconManager.ShowDesktopIconsItemName);
        Assert.Equal(visible, item.Checked);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.SetDesktopIconsVisible"/> lazily builds the context menu
    /// (mirrors <see cref="TrayIconManager.UpdateContextMenu"/>'s own contract) rather than requiring
    /// <see cref="TrayIconManager.Initialize"/> to have run first.
    /// </summary>
    [Fact]
    public void SetDesktopIconsVisible_BuildsContextMenu_WhenCalledBeforeInitialize()
    {
        _sut.SetDesktopIconsVisible(true);

        Assert.NotNull(_capturedMenu);
        Assert.True(FindItem(_capturedMenu!, TrayIconManager.ShowDesktopIconsItemName).Checked);
    }

    /// <summary>
    /// Verifies that clicking the "Edit layout" menu item raises
    /// <see cref="TrayIconManager.OnEditLayoutToggleRequested"/>.
    /// </summary>
    [Fact]
    public void EditLayoutItem_Click_RaisesOnEditLayoutToggleRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnEditLayoutToggleRequested += (_, _) => raised = true;

        FindItem(menu, TrayIconManager.EditLayoutItemName).PerformClick();

        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that clicking the "Lock layout" menu item raises
    /// <see cref="TrayIconManager.OnLockLayoutToggleRequested"/>.
    /// </summary>
    [Fact]
    public void LockLayoutItem_Click_RaisesOnLockLayoutToggleRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnLockLayoutToggleRequested += (_, _) => raised = true;

        FindItem(menu, TrayIconManager.LockLayoutItemName).PerformClick();

        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that clicking the "Settings" menu item still raises
    /// <see cref="TrayIconManager.OnShowMainWindowRequested"/>.
    /// </summary>
    [Fact]
    public void SettingsItem_Click_StillRaisesOnShowMainWindowRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnShowMainWindowRequested += (_, _) => raised = true;

        FindItem(menu, TrayIconManager.SettingsItemName).PerformClick();

        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that clicking the "Exit" menu item still raises
    /// <see cref="TrayIconManager.OnExitRequested"/>.
    /// </summary>
    [Fact]
    public void ExitItem_Click_StillRaisesOnExitRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnExitRequested += (_, _) => raised = true;

        FindItem(menu, TrayIconManager.ExitItemName).PerformClick();

        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.Initialize"/> wires the tray icon's double-click to
    /// <see cref="TrayIconManager.OnNextPageRequested"/> (backlog-1x-master-plan.md §1, ADR-044) — this
    /// layer no longer toggles the main window on double-click (that gesture is now round-robin page
    /// switching, decided by <c>DesktopOrganizer.UI.Lifecycle.TrayPagesAdapter</c>).
    /// </summary>
    [Fact]
    public void Initialize_Wires_DoubleClick_ToNextPageRequested()
    {
        _sut.Initialize();
        var raised = false;
        _sut.OnNextPageRequested += (_, _) => raised = true;

        _notifyIconAdapterMock.Raise(a => a.DoubleClick += null, _notifyIconAdapterMock.Object, EventArgs.Empty);

        Assert.True(raised);
    }

    /// <summary>
    /// backlog-1x-master-plan.md §2: verifies that <see cref="TrayIconManager.SetPages"/> builds one
    /// per-page item for each entry, checked only on the current one, and positioned in the "Pages"
    /// submenu after the built-in "Next page" item and its separator.
    /// </summary>
    [Fact]
    public void SetPages_BuildsOnePerPageItem_CheckedOnlyOnCurrent()
    {
        _sut.Initialize();

        _sut.SetPages([new("a", "Alpha", IsCurrent: false), new("b", "Beta", IsCurrent: true)]);

        var pagesItem = FindItem(_capturedMenu!, TrayIconManager.PagesItemName);
        var dropDownItems = pagesItem.DropDownItems.Cast<ToolStripItem>().ToList();
        Assert.IsType<ToolStripMenuItem>(dropDownItems[0]);
        Assert.Equal(TrayIconManager.NextPageItemName, dropDownItems[0].Name);
        Assert.IsType<ToolStripSeparator>(dropDownItems[1]);
        var alpha = Assert.IsType<ToolStripMenuItem>(dropDownItems[2]);
        var beta = Assert.IsType<ToolStripMenuItem>(dropDownItems[3]);
        Assert.Equal("Alpha", alpha.Text);
        Assert.False(alpha.Checked);
        Assert.Equal("Beta", beta.Text);
        Assert.True(beta.Checked);
    }

    /// <summary>
    /// A repeated <see cref="TrayIconManager.SetPages"/> call with the same (Id, DisplayName) sequence but
    /// a different current page mutates only <see cref="ToolStripMenuItem.Checked"/> in place — the same
    /// <see cref="ToolStripMenuItem"/> instances are reused (MAJOR-1's "mutate, never rebuild" rule), not
    /// recreated.
    /// </summary>
    [Fact]
    public void SetPages_SameComposition_DifferentCurrent_MutatesCheckedInPlace_DoesNotRecreateItems()
    {
        _sut.Initialize();
        _sut.SetPages([new("a", "Alpha", IsCurrent: true), new("b", "Beta", IsCurrent: false)]);
        var pagesItem = FindItem(_capturedMenu!, TrayIconManager.PagesItemName);
        var before = pagesItem.DropDownItems.Cast<ToolStripItem>().Skip(2).ToList();

        _sut.SetPages([new("a", "Alpha", IsCurrent: false), new("b", "Beta", IsCurrent: true)]);

        var after = pagesItem.DropDownItems.Cast<ToolStripItem>().Skip(2).ToList();
        Assert.Same(before[0], after[0]);
        Assert.Same(before[1], after[1]);
        Assert.False(((ToolStripMenuItem)after[0]).Checked);
        Assert.True(((ToolStripMenuItem)after[1]).Checked);
    }

    /// <summary>
    /// A composition change (a page added/removed/renamed — anything but the current page) disposes the
    /// stale per-page items and builds fresh ones. <see cref="TrayIconManager.NextPageItemName"/> and the
    /// separator preceding the per-page entries are untouched.
    /// </summary>
    /// <remarks>
    /// Disposal is observed through <see cref="System.ComponentModel.Component.Disposed"/>, not
    /// <see cref="ToolStripItem.IsDisposed"/>: WinForms only sets <c>IsDisposed</c> when an item is
    /// disposed while it still has an <see cref="ToolStripItem.Owner"/>, and <c>SetPages</c> detaches each
    /// stale item from the submenu before calling <see cref="System.ComponentModel.Component.Dispose()"/>
    /// on it — so <c>IsDisposed</c> stays <see langword="false"/> even though the item was released.
    /// </remarks>
    [Fact]
    public void SetPages_CompositionChanged_DisposesStaleItems_KeepsNextPageItemAndSeparator()
    {
        _sut.Initialize();
        _sut.SetPages([new("a", "Alpha", IsCurrent: true), new("b", "Beta", IsCurrent: false)]);
        var pagesItem = FindItem(_capturedMenu!, TrayIconManager.PagesItemName);
        var staleItems = pagesItem.DropDownItems.Cast<ToolStripItem>().Skip(2).ToList();
        var disposedCount = 0;
        staleItems.ForEach(item => item.Disposed += (_, _) => disposedCount++);

        _sut.SetPages([new("a", "Alpha", IsCurrent: true), new("c", "Gamma", IsCurrent: false)]);

        Assert.Equal(staleItems.Count, disposedCount);
        Assert.DoesNotContain(pagesItem.DropDownItems.Cast<ToolStripItem>(), staleItems.Contains);
        var dropDownItems = pagesItem.DropDownItems.Cast<ToolStripItem>().ToList();
        Assert.Equal(TrayIconManager.NextPageItemName, dropDownItems[0].Name);
        Assert.IsType<ToolStripSeparator>(dropDownItems[1]);
        Assert.Equal(["Alpha", "Gamma"], dropDownItems.Skip(2).Select(i => i.Text));
    }

    /// <summary>
    /// M-F fix (code review, panel clone to page, backlog 1.x wave 3 review): a page display name
    /// containing a single literal <c>&amp;</c> must render with a DOUBLED <c>&amp;&amp;</c> in the
    /// per-page item's own <see cref="ToolStripItem.Text"/> — WinForms treats a lone <c>&amp;</c> as a
    /// mnemonic-accelerator prefix (see <c>TrayIconManager.EscapeAmpersand</c>'s own remarks), which would
    /// otherwise silently drop the character from what the user actually sees.
    /// </summary>
    [Fact]
    public void SetPages_DisplayNameContainsAmpersand_EscapesToDoubleAmpersand()
    {
        _sut.Initialize();

        _sut.SetPages([new("a", "R&D", IsCurrent: false)]);

        var pagesItem = FindItem(_capturedMenu!, TrayIconManager.PagesItemName);
        var item = (ToolStripMenuItem)pagesItem.DropDownItems.Cast<ToolStripItem>().Skip(2).Single();
        Assert.Equal("R&&D", item.Text);
    }

    /// <summary>
    /// LOW-2 fix (code review, panel clone to page, backlog 1.x wave 3 review): once
    /// <see cref="TrayIconManager.Dispose"/> has run, <see cref="TrayIconManager.SetPages"/> must be a
    /// silent no-op — never resurrecting the disposed <see cref="ContextMenuStrip"/> (see <c>_disposed</c>'s
    /// own remarks in <c>TrayIconManager.cs</c>) and never throwing from touching disposed WinForms
    /// components.
    /// </summary>
    [Fact]
    public void SetPages_AfterDispose_IsNoOp_DoesNotThrow()
    {
        _sut.Initialize();
        _sut.Dispose();

        var exception = Record.Exception(() => _sut.SetPages([new("a", "Alpha", IsCurrent: true)]));

        Assert.Null(exception);
    }

    /// <summary>
    /// LOW-2 fix: same guarantee as <see cref="SetPages_AfterDispose_IsNoOp_DoesNotThrow"/>, for
    /// <see cref="TrayIconManager.UpdateContextMenu"/> — a lifecycle-ordering race during shutdown
    /// (e.g. a queued dispatcher callback) must not resurrect the menu strip after <c>Dispose</c>.
    /// </summary>
    [Fact]
    public void UpdateContextMenu_AfterDispose_IsNoOp_DoesNotThrow()
    {
        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        _sut.Dispose();

        var exception = Record.Exception(() => _sut.UpdateContextMenu(
            isMainWindowVisible: false, isEditLayoutChecked: true, canEnterEditLayout: false, isLockLayoutChecked: true));

        Assert.Null(exception);
    }

    /// <summary>
    /// Review L-6 (LOW-2 fix shape): <see cref="TrayIconManager.SetDesktopIconsVisible"/> after
    /// <see cref="TrayIconManager.Dispose"/> is a silent no-op — in particular it must not lazily build (and
    /// hand the adapter) a fresh strip when the menu had never been built before disposal.
    /// </summary>
    [Fact]
    public void SetDesktopIconsVisible_AfterDispose_IsNoOp_DoesNotResurrectStrip()
    {
        _sut.Dispose();

        var exception = Record.Exception(() => _sut.SetDesktopIconsVisible(true));

        Assert.Null(exception);
        Assert.Null(_capturedMenu);
        _notifyIconAdapterMock.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Never);
    }

    /// <summary>Clicking a per-page item raises <see cref="TrayIconManager.OnPageSelected"/> with its id.</summary>
    [Fact]
    public void PageItem_Click_RaisesOnPageSelected_WithItsId()
    {
        _sut.Initialize();
        _sut.SetPages([new("page-1", "Alpha", IsCurrent: false)]);
        string? selectedId = null;
        _sut.OnPageSelected += (_, id) => selectedId = id;

        var pagesItem = FindItem(_capturedMenu!, TrayIconManager.PagesItemName);
        var item = (ToolStripMenuItem)pagesItem.DropDownItems.Cast<ToolStripItem>().Skip(2).Single();
        item.PerformClick();

        Assert.Equal("page-1", selectedId);
    }

    /// <summary>Clicking the "Next page" submenu item raises the same <see cref="TrayIconManager.OnNextPageRequested"/> as the tray icon's double-click.</summary>
    [Fact]
    public void NextPageItem_Click_RaisesOnNextPageRequested()
    {
        _sut.Initialize();

        // "Next page" is enabled only with two or more pages, and a disabled ToolStripItem's
        // PerformClick is a no-op — so the menu needs a multi-page snapshot for the click to land.
        _sut.SetPages([new("a", "Alpha", IsCurrent: true), new("b", "Beta", IsCurrent: false)]);
        var raised = false;
        _sut.OnNextPageRequested += (_, _) => raised = true;

        FindPagesSubmenuItem(_capturedMenu!, TrayIconManager.NextPageItemName).PerformClick();

        Assert.True(raised);
    }

    /// <summary>The "Next page" item is disabled with fewer than two pages, and enabled with two or more.</summary>
    /// <param name="pageCount">Number of pages passed to <see cref="TrayIconManager.SetPages"/>.</param>
    /// <param name="expectedEnabled">Expected <see cref="ToolStripItem.Enabled"/> of the "Next page" item.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void SetPages_NextPageItemEnabled_OnlyWithMultiplePages(int pageCount, bool expectedEnabled)
    {
        _sut.Initialize();

        _sut.SetPages(Enumerable.Range(0, pageCount).Select(i => new TrayPageEntry($"p{i}", $"Page {i}", IsCurrent: i == 0)).ToList());

        Assert.Equal(expectedEnabled, FindPagesSubmenuItem(_capturedMenu!, TrayIconManager.NextPageItemName).Enabled);
    }

    /// <summary>
    /// docs/PLANS/localization.md L-7: a language switch re-labels the "Pages" and "Next page" items —
    /// never the per-page entries themselves, which are data supplied by <see cref="TrayIconManager.SetPages"/>.
    /// </summary>
    [Fact]
    public void CultureChanged_RelabelsPagesAndNextPageItems()
    {
        _sut.Initialize();
        var pagesItem = FindItem(_capturedMenu!, TrayIconManager.PagesItemName);
        var nextPageItem = FindPagesSubmenuItem(_capturedMenu!, TrayIconManager.NextPageItemName);
        Assert.Equal("Pages", pagesItem.Text);
        Assert.Equal("Next page", nextPageItem.Text);

        _localization.ApplyLanguage("ru");

        Assert.Equal("Страницы", pagesItem.Text);
        Assert.Equal("Следующая страница", nextPageItem.Text);
    }

    /// <summary>
    /// The top-level <see cref="ContextMenuStrip"/> is still built and handed to the adapter exactly once,
    /// even across multiple <see cref="TrayIconManager.SetPages"/> calls (MAJOR-1).
    /// </summary>
    [Fact]
    public void SetPages_CalledMultipleTimes_DoesNotHandTheAdapterANewContextMenuStrip()
    {
        _sut.Initialize();

        _sut.SetPages([new("a", "Alpha", IsCurrent: true)]);
        _sut.SetPages([new("a", "Alpha", IsCurrent: false), new("b", "Beta", IsCurrent: true)]);

        _notifyIconAdapterMock.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.Dispose"/> forwards to
    /// <see cref="IDisposable.Dispose"/>.
    /// </summary>
    [Fact]
    public void Dispose_ForwardsToNotifyIconAdapter()
    {
        _sut.Dispose();

        _notifyIconAdapterMock.Verify(a => a.Dispose(), Times.Once);
    }

    /// <summary>
    /// MAJOR-1 regression pin (fix commit b58c8a7): before this fix, EVERY <c>UpdateContextMenu</c> call
    /// constructed a brand-new <see cref="ContextMenuStrip"/> and handed it to
    /// <see cref="INotifyIconAdapter.SetContextMenu"/>, leaking the previous strip's native GDI/HWND
    /// resources (never <see cref="ContextMenuStrip.Dispose"/>'d). After the fix,
    /// <c>TrayIconManager.EnsureContextMenuBuilt</c> builds the strip exactly once per instance and every
    /// later call only mutates the already-live <c>Checked</c>/<c>Enabled</c>/<c>ToolTipText</c>
    /// properties — so <see cref="INotifyIconAdapter.SetContextMenu"/> must be invoked exactly ONCE
    /// across any number of <c>UpdateContextMenu</c> calls on the same instance.
    /// </summary>
    [Fact]
    public void UpdateContextMenu_CalledMultipleTimes_HandsTheAdapterTheSameContextMenuStripInstance_Once()
    {
        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var afterFirstCall = _capturedMenu;

        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: true, canEnterEditLayout: false, isLockLayoutChecked: true);
        var afterSecondCall = _capturedMenu;

        _notifyIconAdapterMock.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);
        Assert.NotNull(afterFirstCall);
        Assert.Same(afterFirstCall, afterSecondCall);

        // The second call's snapshot (canEnterEditLayout: false, isLockLayoutChecked: true) really did
        // reach the SAME instance's items — proves this isn't merely "SetContextMenu called once" while
        // some other, never-mutated strip silently sits in the tray (i.e. UpdateContextMenu still works
        // end-to-end on the reused instance).
        var editLayout = FindItem(afterSecondCall!, TrayIconManager.EditLayoutItemName);
        var lockLayout = FindItem(afterSecondCall!, TrayIconManager.LockLayoutItemName);
        Assert.False(editLayout.Enabled);
        Assert.True(lockLayout.Checked);
    }

    /// <summary>
    /// MAJOR-1 regression pin: the single <see cref="ContextMenuStrip"/> that
    /// <c>TrayIconManager.EnsureContextMenuBuilt</c> builds and hands off must actually be
    /// released on <see cref="TrayIconManager.Dispose"/> — otherwise fixing the "one strip per instance"
    /// leak (see the sibling test above) would just trade N leaked strips per instance for exactly one
    /// permanently-leaked strip per instance instead of zero.
    /// </summary>
    [Fact]
    public void Dispose_DisposesTheContextMenuStripThatWasHandedToTheAdapter()
    {
        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var menu = _capturedMenu!;
        Assert.False(menu.IsDisposed);

        _sut.Dispose();

        Assert.True(menu.IsDisposed);
    }

    /// <summary>
    /// Verifies that calling <see cref="TrayIconManager.Dispose"/> twice is idempotent and does not
    /// throw.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_IsIdempotent_DoesNotThrow()
    {
        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        _sut.Dispose();

        var exception = Record.Exception(() => _sut.Dispose());

        Assert.Null(exception);
    }

    /// <summary>
    /// docs/PLANS/localization.md L-7: verifies that switching the active language re-labels all five
    /// tray menu items in place, on the exact same <see cref="ToolStripMenuItem"/> instances — never by
    /// rebuilding the strip (see the sibling MAJOR-1 regression pins above for why that matters).
    /// </summary>
    [Fact]
    public void CultureChanged_RelabelsItemsInPlace_SameInstances()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var settingsBefore = FindItem(menu, TrayIconManager.SettingsItemName);
        var editLayoutBefore = FindItem(menu, TrayIconManager.EditLayoutItemName);
        var lockLayoutBefore = FindItem(menu, TrayIconManager.LockLayoutItemName);
        var showDesktopIconsBefore = FindItem(menu, TrayIconManager.ShowDesktopIconsItemName);
        var exitBefore = FindItem(menu, TrayIconManager.ExitItemName);
        Assert.Equal("Settings", settingsBefore.Text);
        Assert.Equal("Edit layout", editLayoutBefore.Text);
        Assert.Equal("Exit", exitBefore.Text);

        _localization.ApplyLanguage("ru");

        Assert.Same(_capturedMenu, menu);
        Assert.Same(settingsBefore, FindItem(menu, TrayIconManager.SettingsItemName));
        Assert.Same(editLayoutBefore, FindItem(menu, TrayIconManager.EditLayoutItemName));
        Assert.Same(lockLayoutBefore, FindItem(menu, TrayIconManager.LockLayoutItemName));
        Assert.Same(showDesktopIconsBefore, FindItem(menu, TrayIconManager.ShowDesktopIconsItemName));
        Assert.Same(exitBefore, FindItem(menu, TrayIconManager.ExitItemName));
        Assert.Equal("Параметры", settingsBefore.Text);
        Assert.Equal("Изменить расположение", editLayoutBefore.Text);
        Assert.Equal("Закрепить расположение", lockLayoutBefore.Text);
        Assert.Equal("Показывать значки рабочего стола", showDesktopIconsBefore.Text);
        Assert.Equal("Выход", exitBefore.Text);
    }

    /// <summary>
    /// docs/PLANS/localization.md L-7: verifies that a language switch never calls
    /// <see cref="INotifyIconAdapter.SetContextMenu"/> again — the whole point of relabeling in place is
    /// that the strip handed to the tray is never replaced (MAJOR-1: replacing it leaks native
    /// HWND/GDI resources and races with an in-flight <c>Click</c>).
    /// </summary>
    [Fact]
    public void CultureChanged_DoesNotCallSetContextMenuAgain()
    {
        CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        _notifyIconAdapterMock.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);

        _localization.ApplyLanguage("ru");
        _localization.ApplyLanguage("en");

        _notifyIconAdapterMock.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);
    }

    /// <summary>
    /// docs/PLANS/localization.md §2.7: verifies that the disabled "Edit layout" item's localized
    /// tooltip both appears while disabled and is re-rendered in the new language on a culture switch,
    /// and disappears again (empty) once re-enabled.
    /// </summary>
    [Fact]
    public void LockedTooltip_Localized()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: false, isLockLayoutChecked: true);
        var editLayout = FindItem(menu, TrayIconManager.EditLayoutItemName);
        Assert.Equal("Layout is locked", editLayout.ToolTipText);

        _localization.ApplyLanguage("ru");
        Assert.Equal("Расположение закреплено", editLayout.ToolTipText);

        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: true);
        Assert.Equal(string.Empty, editLayout.ToolTipText);
    }

    /// <summary>
    /// docs/PLANS/localization.md R-11: verifies that <see cref="TrayIconManager.Dispose"/> unsubscribes
    /// from <see cref="ILocalizationService.CultureChanged"/> — a subsequent language switch must not
    /// touch (or throw from) the now-disposed <see cref="ContextMenuStrip"/>.
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromCultureChanged()
    {
        CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);

        _sut.Dispose();
        var exception = Record.Exception(() => _localization.ApplyLanguage("ru"));

        Assert.Null(exception);
    }

    // ── ADR-audit WP-B review F5: "(read-only)" suffix on the tray icon tooltip ─────────────────

    /// <summary>
    /// A writable session shows the plain product name as the tray tooltip.
    /// </summary>
    [Fact]
    public void Initialize_WritableSession_SetsPlainToolTip()
    {
        _sut.Initialize();

        _notifyIconAdapterMock.Verify(a => a.SetToolTip("DesktopOrganizer"), Times.Once);
        _notifyIconAdapterMock.Verify(a => a.SetToolTip(It.Is<string>(t => t.Contains("read-only"))), Times.Never);
    }

    /// <summary>
    /// A read-only session appends the localized suffix, follows a language switch and drops it again when
    /// the session is writable.
    /// </summary>
    [Fact]
    public void SetReadOnlySession_AppendsLocalizedSuffix_AndFollowsLanguageSwitch()
    {
        _sut.Initialize();

        _sut.SetReadOnlySession(true);
        _notifyIconAdapterMock.Verify(a => a.SetToolTip("DesktopOrganizer (read-only)"), Times.Once);

        _localization.ApplyLanguage("ru");
        _notifyIconAdapterMock.Verify(a => a.SetToolTip("DesktopOrganizer (только чтение)"), Times.Once);

        _sut.SetReadOnlySession(false);
        _notifyIconAdapterMock.Verify(a => a.SetToolTip("DesktopOrganizer"), Times.Exactly(2));
    }

    private static ToolStripMenuItem FindItem(ContextMenuStrip menu, string name) =>
        Assert.IsType<ToolStripMenuItem>(Assert.Single(menu.Items.Cast<ToolStripItem>(), i => i.Name == name));

    // "Next page" and the per-page entries live in the "Pages" submenu (backlog-1x-master-plan.md §2.1),
    // not in the top-level strip's Items that FindItem searches.
    private static ToolStripMenuItem FindPagesSubmenuItem(ContextMenuStrip menu, string name) =>
        Assert.IsType<ToolStripMenuItem>(Assert.Single(FindItem(menu, TrayIconManager.PagesItemName).DropDownItems.Cast<ToolStripItem>(), i => i.Name == name));

    private ContextMenuStrip CaptureMenuFromUpdate(bool isEditLayoutChecked, bool canEnterEditLayout, bool isLockLayoutChecked)
    {
        _sut.UpdateContextMenu(
            isMainWindowVisible: false,
            isEditLayoutChecked: isEditLayoutChecked,
            canEnterEditLayout: canEnterEditLayout,
            isLockLayoutChecked: isLockLayoutChecked);

        return _capturedMenu!;
    }
}
