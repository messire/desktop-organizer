using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="SelectedPanelSettingsViewModel"/> — in particular its FIX-B fix-cycle
/// MAJOR finding M-2 addition: subscribing/unsubscribing to the panel's own
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> in <see cref="SelectedPanelSettingsViewModel.SetPanel"/>
/// so a change made elsewhere (the panel's own context menu, a rename) is reflected here too, and
/// <see cref="SelectedPanelSettingsViewModel.Dispose"/> tearing that subscription down.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SelectedPanelSettingsViewModelTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();

    /// <summary>
    /// Verifies that changing <see cref="ChildWindowModel.Title"/> directly on the panel updates
    /// <see cref="SelectedPanelSettingsViewModel.SelectedPanelTitle"/> and raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for it.
    /// </summary>
    [Fact]
    public void ModelTitleChangedDirectly_UpdatesSelectedPanelTitle()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        var model = new ChildWindowModel { Title = "Old" };
        sut.SetPanel(model);

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        model.Title = "Renamed From Panel";

        Assert.Equal("Renamed From Panel", sut.SelectedPanelTitle);
        Assert.Contains(nameof(SelectedPanelSettingsViewModel.SelectedPanelTitle), raised);
    }

    /// <summary>
    /// Verifies that changing <see cref="ChildWindowModel.SourceFolderPath"/> directly on the panel
    /// updates <see cref="SelectedPanelSettingsViewModel.SelectedPanelSupportsManualOrder"/>.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathChangedDirectly_UpdatesSupportsManualOrder()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        var model = new ChildWindowModel();
        sut.SetPanel(model);
        Assert.True(sut.SelectedPanelSupportsManualOrder);

        model.SourceFolderPath = @"C:\fake\portal";

        Assert.False(sut.SelectedPanelSupportsManualOrder);
    }

    /// <summary>
    /// Verifies that retargeting to a different panel unsubscribes from the previous one — a change made
    /// directly on the old panel afterward must not raise notifications any more.
    /// </summary>
    [Fact]
    public void SetPanel_SwitchingModels_UnsubscribesFromPrevious()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        var oldPanel = new ChildWindowModel { Title = "Old" };
        var newPanel = new ChildWindowModel { Title = "New" };
        sut.SetPanel(oldPanel);
        sut.SetPanel(newPanel);

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        oldPanel.Title = "Mutated After Retarget";

        Assert.False(raised);
        Assert.Equal("New", sut.SelectedPanelTitle);
    }

    /// <summary>
    /// Verifies that <see cref="SelectedPanelSettingsViewModel.SetPanel"/> with <see langword="null"/>
    /// releases the current panel and disables the editor (<see cref="SelectedPanelSettingsViewModel.HasSelectedPanel"/>),
    /// and that a subsequent change on that now-unbound panel raises nothing.
    /// </summary>
    [Fact]
    public void SetPanel_Null_ReleasesModelAndDisablesEditor()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        var panel = new ChildWindowModel { Title = "Old" };
        sut.SetPanel(panel);

        sut.SetPanel(null);

        Assert.False(sut.HasSelectedPanel);
        Assert.Equal(string.Empty, sut.SelectedPanelTitle);

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;
        panel.Title = "Changed After Release";

        Assert.False(raised);
    }

    /// <summary>
    /// Verifies that <see cref="SelectedPanelSettingsViewModel.Dispose"/> unsubscribes from the current
    /// panel (FIX-B fix-cycle MAJOR finding M-2 — called by <see cref="PanelCustomizeViewModel.Dispose"/>
    /// when its dialog closes).
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromPanel()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        var panel = new ChildWindowModel { Title = "Old" };
        sut.SetPanel(panel);

        sut.Dispose();

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;
        panel.Title = "Changed After Dispose";

        Assert.False(raised);
        Assert.False(sut.HasSelectedPanel);
    }

    // ── SetViewModeCommand / SetSortModeCommand (FIX-B fix-cycle minor finding 11) ──────────────────

    /// <summary>
    /// Verifies that <see cref="SelectedPanelSettingsViewModel.SetViewModeCommand"/> sets
    /// <see cref="SelectedPanelSettingsViewModel.SelectedPanelViewMode"/> — backs the "Customize panel…"
    /// dialog's View radio group.
    /// </summary>
    [Fact]
    public void SetViewModeCommand_Execute_SetsSelectedPanelViewMode()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        sut.SetPanel(new ChildWindowModel { ViewMode = PanelViewMode.List });

        sut.SetViewModeCommand.Execute(PanelViewMode.Grid);

        Assert.Equal(PanelViewMode.Grid, sut.SelectedPanelViewMode);
    }

    /// <summary>
    /// Verifies that <see cref="SelectedPanelSettingsViewModel.SetSortModeCommand"/> sets
    /// <see cref="SelectedPanelSettingsViewModel.SelectedPanelSortMode"/> when the panel supports a
    /// manual order, and cannot execute for a Folder Portal panel.
    /// </summary>
    [Fact]
    public void SetSortModeCommand_Execute_SetsSelectedPanelSortMode_CanExecuteFalseForPortal()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        var model = new ChildWindowModel { SortMode = PanelSortMode.Manual };
        sut.SetPanel(model);

        Assert.True(sut.SetSortModeCommand.CanExecute(PanelSortMode.Name));
        sut.SetSortModeCommand.Execute(PanelSortMode.Name);
        Assert.Equal(PanelSortMode.Name, sut.SelectedPanelSortMode);

        model.SourceFolderPath = @"C:\fake\portal";
        Assert.False(sut.SetSortModeCommand.CanExecute(PanelSortMode.Manual));
    }

    /// <summary>
    /// Verifies that <see cref="SelectedPanelSettingsViewModel.SetSurfaceStyleCommand"/> (ADR-042) sets
    /// <see cref="SelectedPanelSettingsViewModel.SelectedPanelSurfaceStyle"/> — backs the "Customize
    /// panel…" dialog's Surface Style segmented control.
    /// </summary>
    [Fact]
    public void SetSurfaceStyleCommand_Execute_SetsSelectedPanelSurfaceStyle()
    {
        var sut = new SelectedPanelSettingsViewModel(_windowManagerMock.Object);
        sut.SetPanel(new ChildWindowModel { SurfaceStyle = PanelSurfaceStyle.Ambient });

        sut.SetSurfaceStyleCommand.Execute(PanelSurfaceStyle.Glass);

        Assert.Equal(PanelSurfaceStyle.Glass, sut.SelectedPanelSurfaceStyle);
    }
}
