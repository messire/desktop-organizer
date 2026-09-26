using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PanelCustomizeService"/> (FIX-B fix-cycle MAJOR findings M-3/M-4), exercised
/// against a fake <see cref="IPanelCustomizeDialog"/> — see that seam's own remarks — instead of a live
/// WPF window, and a synchronous (immediate) defer instead of a real <see cref="System.Windows.Threading.Dispatcher"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCustomizeServiceTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly List<FakePanelCustomizeDialog> _createdDialogs = [];

    /// <summary>
    /// Verifies that the first <see cref="PanelCustomizeService.Show"/> call for a given panel builds a
    /// dialog, retargets/positions it, and shows/activates it.
    /// </summary>
    [Fact]
    public void Show_FirstCall_CreatesDialogAndShowsIt()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        RegisterWithWindowManager(model);

        sut.Show(model);

        var dialog = Assert.Single(_createdDialogs);
        Assert.Same(model, dialog.RetargetedTo);
        Assert.Same(model, dialog.PositionedFor);
        Assert.Equal(1, dialog.ShowAndActivateCallCount);
    }

    /// <summary>
    /// Verifies that a second <see cref="PanelCustomizeService.Show"/> call for a DIFFERENT panel, while
    /// the dialog from the first call is still open, re-targets that same dialog instead of creating a
    /// second one.
    /// </summary>
    [Fact]
    public void Show_SecondCallForAnotherModel_ReusesDialogAndRetargets()
    {
        var sut = BuildSut();
        var first = new ChildWindowModel();
        var second = new ChildWindowModel();
        RegisterWithWindowManager(first, second);

        sut.Show(first);
        sut.Show(second);

        Assert.Single(_createdDialogs);
        var dialog = _createdDialogs[0];
        Assert.Same(second, dialog.RetargetedTo);
        Assert.Equal(2, dialog.ShowAndActivateCallCount);
    }

    /// <summary>
    /// Verifies that once the dialog has closed, the next <see cref="PanelCustomizeService.Show"/> call
    /// builds a brand-new dialog instead of reusing the closed (unusable) one.
    /// </summary>
    [Fact]
    public void Show_AfterDialogClosed_CreatesNewDialog()
    {
        var sut = BuildSut();
        var first = new ChildWindowModel();
        var second = new ChildWindowModel();
        RegisterWithWindowManager(first, second);
        sut.Show(first);
        _createdDialogs[0].RaiseClosed();

        sut.Show(second);

        Assert.Equal(2, _createdDialogs.Count);
        Assert.Same(second, _createdDialogs[1].RetargetedTo);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeService.Show"/> throws for a <see langword="null"/> model
    /// without ever touching the dialog factory.
    /// </summary>
    [Fact]
    public void Show_NullModel_Throws()
    {
        var sut = BuildSut();

        Assert.Throws<ArgumentNullException>(() => sut.Show(null!));
        Assert.Empty(_createdDialogs);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeService.Show"/> defers the actual work through the
    /// injected <c>defer</c> callback rather than running it inline — production wiring uses this to run
    /// after the invoking <c>WS_EX_NOACTIVATE</c> context-menu click has finished processing (M-4);
    /// here it's asserted directly against a recording (non-executing) defer.
    /// </summary>
    [Fact]
    public void Show_RunsThroughTheProvidedDeferCallback()
    {
        var deferCallCount = 0;
        var model = new ChildWindowModel();
        RegisterWithWindowManager(model);
        var sut = new PanelCustomizeService(BuildDialog, _windowManagerMock.Object, action =>
        {
            deferCallCount++;
            action(); // still run it, so the rest of this test can assert on its effect
        });

        sut.Show(model);

        Assert.Equal(1, deferCallCount);
        Assert.Single(_createdDialogs);
    }

    // ── M-3: closing the dialog when its panel is removed ───────────────────────────────────────────

    /// <summary>
    /// Verifies that removing the panel the open dialog is currently editing closes that dialog (FIX-B
    /// fix-cycle MAJOR finding M-3 — covers every <see cref="IWindowManager.RemoveChildWindow(Guid, bool, bool)"/>
    /// call site: Settings → Remove, page deletion, and layout-snapshot restore all route through it and
    /// raise <see cref="IWindowManager.OnChildWindowRemoved"/>).
    /// </summary>
    [Fact]
    public void PanelRemoved_ForCustomizedPanel_ClosesDialog()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        RegisterWithWindowManager(model);
        sut.Show(model);

        _windowManagerMock.Raise(w => w.OnChildWindowRemoved += null, _windowManagerMock.Object, model);

        Assert.Equal(1, _createdDialogs[0].CloseCallCount);
    }

    /// <summary>
    /// Verifies that removing a DIFFERENT panel than the one the open dialog is editing leaves the
    /// dialog open.
    /// </summary>
    [Fact]
    public void PanelRemoved_ForOtherPanel_KeepsDialog()
    {
        var sut = BuildSut();
        var edited = new ChildWindowModel();
        var otherPanel = new ChildWindowModel();
        RegisterWithWindowManager(edited, otherPanel);
        sut.Show(edited);

        _windowManagerMock.Raise(w => w.OnChildWindowRemoved += null, _windowManagerMock.Object, otherPanel);

        Assert.Equal(0, _createdDialogs[0].CloseCallCount);
    }

    /// <summary>
    /// Verifies that a panel-removed event while no dialog is open at all is a harmless no-op.
    /// </summary>
    [Fact]
    public void PanelRemoved_NoDialogOpen_DoesNotThrow()
    {
        var sut = BuildSut();

        var exception = Record.Exception(() => _windowManagerMock.Raise(w => w.OnChildWindowRemoved += null, _windowManagerMock.Object, new ChildWindowModel()));

        Assert.Null(exception);
    }

    // ── OPTIONAL (FIX round 2): ShowCore skips opening when the model is no longer registered with
    //    IWindowManager — Show() defers to the UI thread, so the panel it was originally requested for
    //    can be removed in the gap between the call and the deferred callback actually running. ─────────

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeService.Show"/> does not open (or retarget) the dialog for a
    /// model <see cref="IWindowManager.GetAllChildWindows"/> does not currently know about — e.g. the
    /// panel was removed in the gap between the (deferred) <see cref="PanelCustomizeService.Show"/> call
    /// and its callback actually running.
    /// </summary>
    [Fact]
    public void Show_ModelNoLongerRegisteredWithWindowManager_DoesNotOpenDialog()
    {
        var sut = BuildSut();
        var removedModel = new ChildWindowModel();
        RegisterWithWindowManager(); // Deliberately excludes removedModel.

        sut.Show(removedModel);

        Assert.Empty(_createdDialogs);
    }

    /// <summary>
    /// Verifies that an ALREADY-open dialog is left untouched (never retargeted/re-shown) by a
    /// <see cref="PanelCustomizeService.Show"/> call for a model that is no longer registered.
    /// </summary>
    [Fact]
    public void Show_ModelNoLongerRegistered_WhileDialogAlreadyOpenForAnotherPanel_LeavesExistingDialogUntouched()
    {
        var sut = BuildSut();
        var open = new ChildWindowModel();
        var removedModel = new ChildWindowModel();
        RegisterWithWindowManager(open); // removedModel deliberately excluded.
        sut.Show(open);
        var dialog = Assert.Single(_createdDialogs);

        sut.Show(removedModel);

        Assert.Same(open, dialog.RetargetedTo);
        Assert.Equal(1, dialog.ShowAndActivateCallCount);
        Assert.Single(_createdDialogs);
    }

    private PanelCustomizeService BuildSut() =>
        new(BuildDialog, _windowManagerMock.Object, action => action());

    private IPanelCustomizeDialog BuildDialog()
    {
        var dialog = new FakePanelCustomizeDialog();
        _createdDialogs.Add(dialog);
        return dialog;
    }

    /// <summary>
    /// Stubs <see cref="IWindowManager.GetAllChildWindows"/> to return exactly <paramref name="models"/> —
    /// the OPTIONAL registration guard in <c>PanelCustomizeService.ShowCore</c> checks this before opening
    /// or retargeting the dialog.
    /// </summary>
    private void RegisterWithWindowManager(params ChildWindowModel[] models) =>
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns(models);

    /// <summary>
    /// Minimal fake <see cref="IPanelCustomizeDialog"/> — a hand-rolled test double (rather than Moq)
    /// because the tests need to both raise <see cref="Closed"/> themselves and inspect which panel the
    /// real <see cref="PanelCustomizeViewModel"/> ends up targeting after <see cref="Retarget"/>.
    /// </summary>
    private sealed class FakePanelCustomizeDialog : IPanelCustomizeDialog
    {
        // A real ViewModel (with no-op dependencies) rather than a mock: PanelCustomizeService reads
        // ViewModel.Target — a settable property with real retarget semantics — to decide whether a
        // removed panel matches what this dialog is currently editing (M-3).
        public PanelCustomizeViewModel ViewModel { get; } = new(
            new Mock<IWindowManager>().Object,
            BuildAlwaysUnlockedLayoutEditMode(),
            new Mock<IFolderPickerService>().Object);

        public event EventHandler? Closed;

        public ChildWindowModel? RetargetedTo { get; private set; }

        public ChildWindowModel? PositionedFor { get; private set; }

        public int ShowAndActivateCallCount { get; private set; }

        public int CloseCallCount { get; private set; }

        public void Retarget(ChildWindowModel model)
        {
            RetargetedTo = model;
            ViewModel.Target = model;
        }

        public void PositionForPanel(ChildWindowModel model) => PositionedFor = model;

        public void ShowAndActivate() => ShowAndActivateCallCount++;

        public void Close()
        {
            CloseCallCount++;
            RaiseClosed();
        }

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

        private static ILayoutEditModeService BuildAlwaysUnlockedLayoutEditMode()
        {
            var mock = new Mock<ILayoutEditModeService>();
            mock.SetupGet(l => l.CanEnterEditLayout).Returns(true);
            return mock.Object;
        }
    }
}
