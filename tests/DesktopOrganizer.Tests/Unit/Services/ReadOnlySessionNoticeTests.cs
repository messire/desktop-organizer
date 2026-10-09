using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// ADR-audit WP-B (B1) + decisions review cycle 1 (U4/U5): <see cref="ReadOnlySessionNotice"/> tells the user
/// - once per session, through the non-blocking <see cref="IStatusNotifier"/> - that this session cannot
/// persist panel changes, names the actual reason, and opens the settings window when nothing else is visible.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ReadOnlySessionNoticeTests
{
    private readonly Mock<IApplicationStateStore> _store = new();
    private readonly Mock<IStatusNotifier> _notifier = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationStateService> _state = new();
    private readonly Mock<IWindowManager> _windowManager = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlySessionNoticeTests"/> class with one loaded panel.
    /// </summary>
    public ReadOnlySessionNoticeTests()
    {
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_Title")).Returns("title");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_Hint")).Returns("hint");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_NewerSchema_Message")).Returns("newer-message");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_NewerSchema_Detail")).Returns("newer-detail");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_NewerSchema_Action")).Returns("newer-action");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_Locked_Message")).Returns("locked-message");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_Locked_Detail")).Returns("locked-detail");
        _localization.Setup(l => l.GetString("ReadOnlySession_Notice_Locked_Action")).Returns("locked-action");
        _state.SetupGet(s => s.Panels).Returns([new ChildWindowModel()]);
    }

    /// <summary>
    /// A newer on-disk schema shows the "newer version" wording: panels not deleted, run the latest version.
    /// </summary>
    [Fact]
    public void ShowIfReadOnly_NewerSchema_ShowsNewerSchemaText()
    {
        _store.SetupGet(s => s.IsWritable).Returns(false);
        _state.SetupGet(s => s.LastLoadStatus).Returns(StateLoadStatus.NewerSchema);
        var sut = Build();

        var shown = sut.ShowIfReadOnly();

        Assert.True(shown);
        _notifier.Verify(n => n.Show(StatusSeverity.Caution, "newer-message newer-detail hint newer-action", "title"), Times.Once);
    }

    /// <summary>
    /// Any other read-only reason (the advisory lock is held by another instance) shows the "other instance" wording.
    /// </summary>
    /// <param name="status">What the load reported.</param>
    [Theory]
    [InlineData(StateLoadStatus.Loaded)]
    [InlineData(StateLoadStatus.RecoveredFromBackup)]
    [InlineData(StateLoadStatus.FirstRun)]
    public void ShowIfReadOnly_OtherReason_ShowsLockedText(StateLoadStatus status)
    {
        _store.SetupGet(s => s.IsWritable).Returns(false);
        _state.SetupGet(s => s.LastLoadStatus).Returns(status);
        var sut = Build();

        var shown = sut.ShowIfReadOnly();

        Assert.True(shown);
        _notifier.Verify(n => n.Show(StatusSeverity.Caution, "locked-message locked-detail hint locked-action", "title"), Times.Once);
    }

    /// <summary>A writable session shows nothing and opens nothing.</summary>
    [Fact]
    public void ShowIfReadOnly_StoreWritable_ShowsNothing()
    {
        _store.SetupGet(s => s.IsWritable).Returns(true);
        _state.SetupGet(s => s.Panels).Returns([]);
        var sut = Build();

        var shown = sut.ShowIfReadOnly();

        Assert.False(shown);
        _notifier.Verify(n => n.Show(It.IsAny<StatusSeverity>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _windowManager.Verify(w => w.ShowMainWindow(), Times.Never);
    }

    /// <summary>The notice is one message per session: repeated calls do not show it again.</summary>
    [Fact]
    public void ShowIfReadOnly_CalledTwice_ShowsOnlyOnce()
    {
        _store.SetupGet(s => s.IsWritable).Returns(false);
        var sut = Build();

        sut.ShowIfReadOnly();
        var secondShown = sut.ShowIfReadOnly();

        Assert.False(secondShown);
        _notifier.Verify(n => n.Show(It.IsAny<StatusSeverity>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    /// <summary>
    /// A read-only start with no panels (the "all my panels are gone" scenario) opens the settings window once,
    /// so the InfoBar with the explanation is actually seen.
    /// </summary>
    [Fact]
    public void ShowIfReadOnly_NoPanels_OpensSettingsWindowOnce()
    {
        _store.SetupGet(s => s.IsWritable).Returns(false);
        _state.SetupGet(s => s.LastLoadStatus).Returns(StateLoadStatus.NewerSchema);
        _state.SetupGet(s => s.Panels).Returns([]);
        var sut = Build();

        sut.ShowIfReadOnly();
        sut.ShowIfReadOnly();

        _windowManager.Verify(w => w.ShowMainWindow(), Times.Once);
    }

    /// <summary>With panels on screen the notice just waits in the settings window - nothing is opened.</summary>
    [Fact]
    public void ShowIfReadOnly_WithPanels_DoesNotOpenSettingsWindow()
    {
        _store.SetupGet(s => s.IsWritable).Returns(false);
        var sut = Build();

        sut.ShowIfReadOnly();

        _windowManager.Verify(w => w.ShowMainWindow(), Times.Never);
    }

    private ReadOnlySessionNotice Build()
        => new(_store.Object, _notifier.Object, _localization.Object, _state.Object, _windowManager.Object);
}
