using System.ComponentModel;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="LayoutEditModeService"/> — the process-wide singleton holding the
/// application-global Edit Layout mode (docs/PLANS/phase-6-edit-layout-ux.md §1 D-1/D-3/D-8/D-11).
/// TDD red phase (tdd-test-engineer, P6-1): written before <see cref="LayoutEditModeService"/> is
/// implemented — every test below is expected to fail (currently via
/// <see cref="NotImplementedException"/>) until dotnet-services-engineer implements the real state
/// machine. No WPF/STA dependency: the service is pure C# state plus <see cref="IAppSettings"/>/
/// <see cref="IGestureDiagnostics"/> collaborators, both mocked here.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LayoutEditModeServiceTests
{
    // ----- R-11: Mode is transient, never persisted -----

    /// <summary>
    /// Verifies that a freshly constructed <see cref="LayoutEditModeService"/> always starts in
    /// <see cref="LayoutInteractionMode.Normal"/>.
    /// </summary>
    [Fact]
    public void Construct_AlwaysStartsInNormalMode()
    {
        var (sut, _, _) = CreateSut();

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    /// <summary>
    /// Verifies that Mode starts as <see cref="LayoutInteractionMode.Normal"/> even when
    /// <see cref="ILayoutEditModeService.IsLayoutLocked"/> was already <see langword="true"/> at
    /// construction — there is no persisted "previous Mode" to restore.
    /// </summary>
    [Fact]
    public void Construct_StartsInNormalMode_EvenWhenLayoutIsLocked()
    {
        // R-11: there is no persisted "previous Mode" to read at all — Mode is always Normal at
        // construction regardless of any other persisted state (IsLayoutLocked included).
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.IsEditing"/> is <see langword="false"/> at construction.
    /// </summary>
    [Fact]
    public void Construct_IsEditing_IsFalse()
    {
        var (sut, _, _) = CreateSut();

        Assert.False(sut.IsEditing);
    }

    // ----- IsLayoutLocked: read from / persisted through IAppSettings (D-8) -----

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.IsLayoutLocked"/> reflects the
    /// <see cref="IAppSettings"/> value it was constructed with, and that
    /// <see cref="ILayoutEditModeService.CanEnterEditLayout"/> is correspondingly <see langword="false"/>.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_Getter_ReflectsAppSettingsValueAtConstruction()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        Assert.True(sut.IsLayoutLocked);
        Assert.False(sut.CanEnterEditLayout);
    }

    /// <summary>
    /// Verifies that setting <see cref="ILayoutEditModeService.IsLayoutLocked"/> writes the new value
    /// through to the underlying <see cref="IAppSettings"/> instance.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_Setter_PersistsThroughAppSettings()
    {
        var (sut, appSettings, _) = CreateSut();

        sut.IsLayoutLocked = true;

        Assert.True(appSettings.Object.IsLayoutLocked);
    }

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.CanEnterEditLayout"/> always tracks the logical
    /// inverse of <see cref="ILayoutEditModeService.IsLayoutLocked"/>.
    /// </summary>
    [Fact]
    public void CanEnterEditLayout_IsInverseOfIsLayoutLocked()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: false);

        Assert.True(sut.CanEnterEditLayout);

        sut.IsLayoutLocked = true;

        Assert.False(sut.CanEnterEditLayout);
    }

    // ----- D-3/R-9: locking while editing forces an immediate exit to Normal -----

    /// <summary>
    /// Verifies that locking the layout while in <see cref="LayoutInteractionMode.EditLayout"/> forces an
    /// immediate exit back to <see cref="LayoutInteractionMode.Normal"/>.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_SetTrue_WhileEditing_ForcesExitToNormal()
    {
        var (sut, _, _) = CreateSut();
        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));
        Assert.Equal(LayoutInteractionMode.EditLayout, sut.Mode);

        sut.IsLayoutLocked = true;

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
        Assert.False(sut.IsEditing);
    }

    /// <summary>
    /// Verifies that the forced exit to Normal triggered by locking the layout while editing raises
    /// <see cref="ILayoutEditModeService.ModeChanged"/> with the correct From/To values.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_SetTrue_WhileEditing_RaisesModeChanged()
    {
        var (sut, _, _) = CreateSut();
        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));
        LayoutModeChangedEventArgs? raised = null;
        sut.ModeChanged += (_, e) => raised = e;

        sut.IsLayoutLocked = true;

        Assert.NotNull(raised);
        Assert.Equal(LayoutInteractionMode.EditLayout, raised!.From);
        Assert.Equal(LayoutInteractionMode.Normal, raised.To);
    }

    /// <summary>
    /// Verifies that locking the layout while already in <see cref="LayoutInteractionMode.Normal"/> does
    /// not raise <see cref="ILayoutEditModeService.ModeChanged"/>, since there is no real transition.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_SetTrue_WhileAlreadyNormal_DoesNotRaiseModeChanged()
    {
        var (sut, _, _) = CreateSut();
        var raisedCount = 0;
        sut.ModeChanged += (_, _) => raisedCount++;

        sut.IsLayoutLocked = true;

        Assert.Equal(0, raisedCount);
    }

    // ----- TryEnterEditLayout -----

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.TryEnterEditLayout"/> succeeds and transitions to
    /// <see cref="LayoutInteractionMode.EditLayout"/> when the layout is not locked.
    /// </summary>
    [Fact]
    public void TryEnterEditLayout_WhenUnlocked_ReturnsTrueAndEntersEditLayout()
    {
        var (sut, _, _) = CreateSut();

        var result = sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.EditLayout, sut.Mode);
        Assert.True(sut.IsEditing);
    }

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.TryEnterEditLayout"/> fails and leaves the mode at
    /// <see cref="LayoutInteractionMode.Normal"/> when the layout is locked.
    /// </summary>
    [Fact]
    public void TryEnterEditLayout_WhenLocked_ReturnsFalseAndStaysNormal()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        var result = sut.TryEnterEditLayout(LayoutModeChangeSource.PanelMenu);

        Assert.False(result);
        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    /// <summary>
    /// Verifies that a rejected <see cref="ILayoutEditModeService.TryEnterEditLayout"/> call (layout
    /// locked) does not raise <see cref="ILayoutEditModeService.ModeChanged"/>.
    /// </summary>
    [Fact]
    public void TryEnterEditLayout_WhenLocked_DoesNotRaiseModeChanged()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);
        var raisedCount = 0;
        sut.ModeChanged += (_, _) => raisedCount++;

        sut.TryEnterEditLayout(LayoutModeChangeSource.PanelMenu);

        Assert.Equal(0, raisedCount);
    }

    /// <summary>
    /// Verifies that calling <see cref="ILayoutEditModeService.TryEnterEditLayout"/> while already in
    /// <see cref="LayoutInteractionMode.EditLayout"/> is a no-op: it still reports success but raises no
    /// <see cref="ILayoutEditModeService.ModeChanged"/> event.
    /// </summary>
    [Fact]
    public void TryEnterEditLayout_WhenAlreadyEditLayout_IsNoOp_ReturnsTrueWithoutRaisingModeChanged()
    {
        var (sut, _, _) = CreateSut();
        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));
        var raisedCount = 0;
        sut.ModeChanged += (_, _) => raisedCount++;

        var result = sut.TryEnterEditLayout(LayoutModeChangeSource.Keyboard);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.EditLayout, sut.Mode);
        Assert.Equal(0, raisedCount);
    }

    // ----- ExitEditLayout -----

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.ExitEditLayout"/> returns the mode to
    /// <see cref="LayoutInteractionMode.Normal"/> from <see cref="LayoutInteractionMode.EditLayout"/>.
    /// </summary>
    [Fact]
    public void ExitEditLayout_FromEditLayout_ReturnsToNormal()
    {
        var (sut, _, _) = CreateSut();
        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));

        sut.ExitEditLayout(LayoutModeChangeSource.Tray);

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
        Assert.False(sut.IsEditing);
    }

    /// <summary>
    /// Verifies that calling <see cref="ILayoutEditModeService.ExitEditLayout"/> while already in
    /// <see cref="LayoutInteractionMode.Normal"/> is a no-op that raises no
    /// <see cref="ILayoutEditModeService.ModeChanged"/> event.
    /// </summary>
    [Fact]
    public void ExitEditLayout_WhenAlreadyNormal_IsNoOp_DoesNotRaiseModeChanged()
    {
        var (sut, _, _) = CreateSut();
        var raisedCount = 0;
        sut.ModeChanged += (_, _) => raisedCount++;

        sut.ExitEditLayout(LayoutModeChangeSource.Tray);

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
        Assert.Equal(0, raisedCount);
    }

    // ----- ModeChanged: exactly once per real transition (T-TRK-07 precedent) -----

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.ModeChanged"/> fires exactly once per real mode
    /// transition and never for a call that is a no-op because the mode is already at its target.
    /// </summary>
    [Fact]
    public void ModeChanged_RaisedExactlyOncePerRealTransition_NeverOnNoOps()
    {
        var (sut, _, _) = CreateSut();
        var raisedCount = 0;
        sut.ModeChanged += (_, _) => raisedCount++;

        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));  // 1 real transition
        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));  // no-op: already EditLayout
        sut.ExitEditLayout(LayoutModeChangeSource.Tray);                  // 1 real transition
        sut.ExitEditLayout(LayoutModeChangeSource.Tray);                  // no-op: already Normal

        Assert.Equal(2, raisedCount);
    }

    /// <summary>
    /// Verifies that the <see cref="LayoutModeChangedEventArgs"/> raised on a real transition carries the
    /// correct From, To, and Source values.
    /// </summary>
    [Fact]
    public void ModeChanged_CarriesFromToAndSource()
    {
        var (sut, _, _) = CreateSut();
        LayoutModeChangedEventArgs? raised = null;
        sut.ModeChanged += (_, e) => raised = e;

        sut.TryEnterEditLayout(LayoutModeChangeSource.PanelMenu);

        Assert.NotNull(raised);
        Assert.Equal(LayoutInteractionMode.Normal, raised!.From);
        Assert.Equal(LayoutInteractionMode.EditLayout, raised.To);
        Assert.Equal(LayoutModeChangeSource.PanelMenu, raised.Source);
    }

    // ----- Toggle -----

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.Toggle"/> enters
    /// <see cref="LayoutInteractionMode.EditLayout"/> and reports success when starting from Normal.
    /// </summary>
    [Fact]
    public void Toggle_FromNormal_EntersEditLayout_ReturnsTrue()
    {
        var (sut, _, _) = CreateSut();

        var result = sut.Toggle(LayoutModeChangeSource.SettingsWindow);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.EditLayout, sut.Mode);
    }

    /// <summary>
    /// Verifies that two consecutive <see cref="ILayoutEditModeService.Toggle"/> calls flip the mode there
    /// and back, ending at <see cref="LayoutInteractionMode.Normal"/> again.
    /// </summary>
    [Fact]
    public void Toggle_IsIdempotentAcrossTwoCalls_ReturnsToNormal()
    {
        var (sut, _, _) = CreateSut();

        sut.Toggle(LayoutModeChangeSource.SettingsWindow);
        var result = sut.Toggle(LayoutModeChangeSource.SettingsWindow);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.Toggle"/> fails and leaves the mode unchanged when
    /// the layout is locked.
    /// </summary>
    [Fact]
    public void Toggle_WhenLocked_ReturnsFalse_DoesNotChangeMode()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        var result = sut.Toggle(LayoutModeChangeSource.SettingsWindow);

        Assert.False(result);
        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    // ----- INotifyPropertyChanged -----

    /// <summary>
    /// Verifies that a real mode transition raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// for <see cref="ILayoutEditModeService.Mode"/>.
    /// </summary>
    [Fact]
    public void PropertyChanged_RaisedForMode_OnRealTransition()
    {
        var (sut, _, _) = CreateSut();
        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);

        Assert.Contains(nameof(ILayoutEditModeService.Mode), raisedProperties);
    }

    /// <summary>
    /// Verifies that a real mode transition raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// for <see cref="ILayoutEditModeService.IsEditing"/>.
    /// </summary>
    [Fact]
    public void PropertyChanged_RaisedForIsEditing_OnRealTransition()
    {
        var (sut, _, _) = CreateSut();
        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);

        Assert.Contains(nameof(ILayoutEditModeService.IsEditing), raisedProperties);
    }

    /// <summary>
    /// Verifies that changing <see cref="ILayoutEditModeService.IsLayoutLocked"/> raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> for both
    /// <see cref="ILayoutEditModeService.CanEnterEditLayout"/> and
    /// <see cref="ILayoutEditModeService.IsLayoutLocked"/> itself.
    /// </summary>
    [Fact]
    public void PropertyChanged_RaisedForCanEnterEditLayout_WhenIsLayoutLockedChanges()
    {
        var (sut, _, _) = CreateSut();
        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.IsLayoutLocked = true;

        Assert.Contains(nameof(ILayoutEditModeService.CanEnterEditLayout), raisedProperties);
        Assert.Contains(nameof(ILayoutEditModeService.IsLayoutLocked), raisedProperties);
    }

    /// <summary>
    /// Verifies that a no-op transition (already at the target mode) raises no
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> events at all.
    /// </summary>
    [Fact]
    public void PropertyChanged_NotRaised_OnNoOpTransition()
    {
        var (sut, _, _) = CreateSut();
        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);
        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray); // no-op: already EditLayout

        Assert.Empty(raisedProperties);
    }

    // ----- D-11: IGestureDiagnostics.LayoutModeChanged -----

    /// <summary>
    /// Verifies that a real mode transition calls <see cref="IGestureDiagnostics.LayoutModeChanged"/>
    /// with the correct from/to modes, the triggering source, and the current lock state.
    /// </summary>
    [Fact]
    public void RealTransition_CallsDiagnosticsLayoutModeChanged_WithFromToSourceAndLockState()
    {
        var (sut, _, diagnostics) = CreateSut(isLayoutLockedInitially: false);

        sut.TryEnterEditLayout(LayoutModeChangeSource.Keyboard);

        diagnostics.Verify(
            d => d.LayoutModeChanged(
                LayoutInteractionMode.Normal,
                LayoutInteractionMode.EditLayout,
                LayoutModeChangeSource.Keyboard,
                false),
            Times.Once);
    }

    /// <summary>
    /// Verifies that a no-op transition never calls <see cref="IGestureDiagnostics.LayoutModeChanged"/>.
    /// </summary>
    [Fact]
    public void NoOpTransition_DoesNotCallDiagnosticsLayoutModeChanged()
    {
        var (sut, _, diagnostics) = CreateSut();
        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);
        diagnostics.Invocations.Clear();

        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray); // no-op: already EditLayout

        diagnostics.Verify(
            d => d.LayoutModeChanged(
                It.IsAny<LayoutInteractionMode>(),
                It.IsAny<LayoutInteractionMode>(),
                It.IsAny<LayoutModeChangeSource>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    // ----- helpers -----
    private static (LayoutEditModeService Sut, Mock<IAppSettings> AppSettings, Mock<IGestureDiagnostics> Diagnostics) CreateSut(
        bool isLayoutLockedInitially = false)
    {
        var appSettings = new Mock<IAppSettings>();
        appSettings.SetupProperty(s => s.IsLayoutLocked, isLayoutLockedInitially);
        appSettings.SetupProperty(s => s.NormalModeLayoutGestures, "MoveOnly");
        var diagnostics = new Mock<IGestureDiagnostics>();

        var sut = new LayoutEditModeService(appSettings.Object, diagnostics.Object);
        return (sut, appSettings, diagnostics);
    }
}
