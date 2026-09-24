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

    public TrayIconManagerTests()
    {
        _notifyIconAdapterMock
            .Setup(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()))
            .Callback<ContextMenuStrip>(menu => _capturedMenu = menu);

        _sut = new TrayIconManager(_notifyIconAdapterMock.Object);
    }

    private static ToolStripMenuItem FindItem(ContextMenuStrip menu, string header) =>
        Assert.IsType<ToolStripMenuItem>(Assert.Single(menu.Items.Cast<ToolStripItem>(), i => i.Text == header));

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

    [Fact]
    public void UpdateContextMenu_LockLayoutItem_AlwaysEnabled_RegardlessOfCanEnterEditLayout()
    {
        // D-3: "Lock layout" must stay clickable at all times — it's the only way to unlock again.
        var menuWhenDisabledEditLayout = CaptureMenuFromUpdate(
            isEditLayoutChecked: false, canEnterEditLayout: false, isLockLayoutChecked: true);

        var lockLayout = FindItem(menuWhenDisabledEditLayout, LockLayoutHeader);

        Assert.True(lockLayout.Enabled);
    }

    [Fact]
    public void UpdateContextMenu_KeepsSettingsAndExitItems_Present()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);

        Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Text == "Settings");
        Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Text == "Exit");
    }

    [Fact]
    public void EditLayoutItem_Click_RaisesOnEditLayoutToggleRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnEditLayoutToggleRequested += (_, _) => raised = true;

        FindItem(menu, EditLayoutHeader).PerformClick();

        Assert.True(raised);
    }

    [Fact]
    public void LockLayoutItem_Click_RaisesOnLockLayoutToggleRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnLockLayoutToggleRequested += (_, _) => raised = true;

        FindItem(menu, LockLayoutHeader).PerformClick();

        Assert.True(raised);
    }

    [Fact]
    public void SettingsItem_Click_StillRaisesOnShowMainWindowRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnShowMainWindowRequested += (_, _) => raised = true;

        FindItem(menu, "Settings").PerformClick();

        Assert.True(raised);
    }

    [Fact]
    public void ExitItem_Click_StillRaisesOnExitRequested()
    {
        var menu = CaptureMenuFromUpdate(isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        var raised = false;
        _sut.OnExitRequested += (_, _) => raised = true;

        FindItem(menu, "Exit").PerformClick();

        Assert.True(raised);
    }

    [Fact]
    public void Initialize_StillWires_DoubleClick_ToToggleMainWindowVisibilityRequested()
    {
        _sut.Initialize();
        var raised = false;
        _sut.OnToggleMainWindowVisibilityRequested += (_, _) => raised = true;

        _notifyIconAdapterMock.Raise(a => a.DoubleClick += null, _notifyIconAdapterMock.Object, EventArgs.Empty);

        Assert.True(raised);
    }

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
    /// <see cref="TrayIconManager.EnsureContextMenuBuilt"/> builds and hands off must actually be
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

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent_DoesNotThrow()
    {
        _sut.UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        _sut.Dispose();

        var exception = Record.Exception(() => _sut.Dispose());

        Assert.Null(exception);
    }

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
