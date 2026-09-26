using System.IO;
using System.Windows.Forms;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="TrayIconManager"/> — in particular the P6-3 "Edit layout"/"Lock layout"
/// checkable tray menu items (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #1). This layer has no
/// visibility into <c>ILayoutEditModeService</c> (it lives in DesktopOrganizer.UI, above this project in
/// the layering) — everything checkable-state related is a caller-supplied snapshot passed into
/// <see cref="ITrayIconManager.UpdateContextMenu"/>, and every user click is surfaced as an event for the
/// caller (DesktopOrganizer.UI's App.xaml.cs) to translate. <see cref="INotifyIconAdapter"/> is mocked
/// throughout — the real <c>NotifyIconAdapter</c>/<see cref="NotifyIcon"/> wrapper is a thin,
/// headless-untestable shell around the actual Win32 tray icon (manual verify only, see this task's
/// report).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class TrayIconManagerTests
{
    private const string EditLayoutHeader = "Edit layout";
    private const string LockLayoutHeader = "Lock layout";
    private const string ShowDesktopIconsHeader = "Show desktop icons";

    private readonly Mock<INotifyIconAdapter> _notifyIconAdapterMock = new();
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

        _sut = new TrayIconManager(_notifyIconAdapterMock.Object);
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
        var editLayout = FindItem(built!, EditLayoutHeader);
        var lockLayout = FindItem(built!, LockLayoutHeader);
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

        var editLayout = FindItem(menu, EditLayoutHeader);
        var lockLayout = FindItem(menu, LockLayoutHeader);

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

        var lockLayout = FindItem(menuWhenDisabledEditLayout, LockLayoutHeader);

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

        Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Text == "Settings");
        Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Text == "Exit");
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

        var lockLayoutIndex = items.FindIndex(i => i.Text == LockLayoutHeader);
        var showDesktopIconsIndex = items.FindIndex(i => i.Text == ShowDesktopIconsHeader);
        var exitIndex = items.FindIndex(i => i.Text == "Exit");

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

        FindItem(_capturedMenu!, ShowDesktopIconsHeader).PerformClick();

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

        var item = FindItem(_capturedMenu!, ShowDesktopIconsHeader);
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
        Assert.True(FindItem(_capturedMenu!, ShowDesktopIconsHeader).Checked);
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

        FindItem(menu, EditLayoutHeader).PerformClick();

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

        FindItem(menu, LockLayoutHeader).PerformClick();

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

        FindItem(menu, "Settings").PerformClick();

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

        FindItem(menu, "Exit").PerformClick();

        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that <see cref="TrayIconManager.Initialize"/> still wires the tray icon's double-click
    /// to <see cref="TrayIconManager.OnToggleMainWindowVisibilityRequested"/>.
    /// </summary>
    [Fact]
    public void Initialize_StillWires_DoubleClick_ToToggleMainWindowVisibilityRequested()
    {
        _sut.Initialize();
        var raised = false;
        _sut.OnToggleMainWindowVisibilityRequested += (_, _) => raised = true;

        _notifyIconAdapterMock.Raise(a => a.DoubleClick += null, _notifyIconAdapterMock.Object, EventArgs.Empty);

        Assert.True(raised);
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
        var editLayout = FindItem(afterSecondCall!, EditLayoutHeader);
        var lockLayout = FindItem(afterSecondCall!, LockLayoutHeader);
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

    private static ToolStripMenuItem FindItem(ContextMenuStrip menu, string header) =>
        Assert.IsType<ToolStripMenuItem>(Assert.Single(menu.Items.Cast<ToolStripItem>(), i => i.Text == header));

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
