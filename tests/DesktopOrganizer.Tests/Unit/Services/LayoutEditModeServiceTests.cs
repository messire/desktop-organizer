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

    // ----- R-11: Mode is transient, never persisted -----

    [Fact]
    public void Construct_AlwaysStartsInNormalMode()
    {
        var (sut, _, _) = CreateSut();

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    [Fact]
    public void Construct_StartsInNormalMode_EvenWhenLayoutIsLocked()
    {
        // R-11: there is no persisted "previous Mode" to read at all — Mode is always Normal at
        // construction regardless of any other persisted state (IsLayoutLocked included).
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    [Fact]
    public void Construct_IsEditing_IsFalse()
    {
        var (sut, _, _) = CreateSut();

        Assert.False(sut.IsEditing);
    }

    // ----- IsLayoutLocked: read from / persisted through IAppSettings (D-8) -----

    [Fact]
    public void IsLayoutLocked_Getter_ReflectsAppSettingsValueAtConstruction()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        Assert.True(sut.IsLayoutLocked);
        Assert.False(sut.CanEnterEditLayout);
    }

    [Fact]
    public void IsLayoutLocked_Setter_PersistsThroughAppSettings()
    {
        var (sut, appSettings, _) = CreateSut();

        sut.IsLayoutLocked = true;

        Assert.True(appSettings.Object.IsLayoutLocked);
    }

    [Fact]
    public void CanEnterEditLayout_IsInverseOfIsLayoutLocked()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: false);

        Assert.True(sut.CanEnterEditLayout);

        sut.IsLayoutLocked = true;

        Assert.False(sut.CanEnterEditLayout);
    }

    // ----- D-3/R-9: locking while editing forces an immediate exit to Normal -----

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

    [Fact]
    public void TryEnterEditLayout_WhenUnlocked_ReturnsTrueAndEntersEditLayout()
    {
        var (sut, _, _) = CreateSut();

        var result = sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.EditLayout, sut.Mode);
        Assert.True(sut.IsEditing);
    }

    [Fact]
    public void TryEnterEditLayout_WhenLocked_ReturnsFalseAndStaysNormal()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        var result = sut.TryEnterEditLayout(LayoutModeChangeSource.PanelMenu);

        Assert.False(result);
        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    [Fact]
    public void TryEnterEditLayout_WhenLocked_DoesNotRaiseModeChanged()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);
        var raisedCount = 0;
        sut.ModeChanged += (_, _) => raisedCount++;

        sut.TryEnterEditLayout(LayoutModeChangeSource.PanelMenu);

        Assert.Equal(0, raisedCount);
    }

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

    [Fact]
    public void ExitEditLayout_FromEditLayout_ReturnsToNormal()
    {
        var (sut, _, _) = CreateSut();
        Assert.True(sut.TryEnterEditLayout(LayoutModeChangeSource.Tray));

        sut.ExitEditLayout(LayoutModeChangeSource.Tray);

        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
        Assert.False(sut.IsEditing);
    }

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

    [Fact]
    public void Toggle_FromNormal_EntersEditLayout_ReturnsTrue()
    {
        var (sut, _, _) = CreateSut();

        var result = sut.Toggle(LayoutModeChangeSource.SettingsWindow);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.EditLayout, sut.Mode);
    }

    [Fact]
    public void Toggle_IsIdempotentAcrossTwoCalls_ReturnsToNormal()
    {
        var (sut, _, _) = CreateSut();

        sut.Toggle(LayoutModeChangeSource.SettingsWindow);
        var result = sut.Toggle(LayoutModeChangeSource.SettingsWindow);

        Assert.True(result);
        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    [Fact]
    public void Toggle_WhenLocked_ReturnsFalse_DoesNotChangeMode()
    {
        var (sut, _, _) = CreateSut(isLayoutLockedInitially: true);

        var result = sut.Toggle(LayoutModeChangeSource.SettingsWindow);

        Assert.False(result);
        Assert.Equal(LayoutInteractionMode.Normal, sut.Mode);
    }

    // ----- INotifyPropertyChanged -----

    [Fact]
    public void PropertyChanged_RaisedForMode_OnRealTransition()
    {
        var (sut, _, _) = CreateSut();
        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);

        Assert.Contains(nameof(ILayoutEditModeService.Mode), raisedProperties);
    }

    [Fact]
    public void PropertyChanged_RaisedForIsEditing_OnRealTransition()
    {
        var (sut, _, _) = CreateSut();
        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.TryEnterEditLayout(LayoutModeChangeSource.Tray);

        Assert.Contains(nameof(ILayoutEditModeService.IsEditing), raisedProperties);
    }

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
}
