using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Store-publication B2: <see cref="FirstRunTrayNotice"/> shows the "the app lives in the tray" balloon exactly
/// when the load status is <see cref="StateLoadStatus.FirstRun"/> on a writable session, at most once, with
/// localized text, and persists nothing.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class FirstRunTrayNoticeTests
{
    private readonly Mock<IApplicationStateStore> _store = new();
    private readonly Mock<IApplicationStateService> _state = new();
    private readonly Mock<ITrayIconManager> _tray = new();

    /// <summary>Verifies the balloon is shown, with the English title and message, on a writable first run.</summary>
    [Fact]
    public void ShowIfFirstRun_FirstRunWritable_ShowsLocalizedBalloon()
    {
        var sut = Build(StateLoadStatus.FirstRun, writable: true, TestLocalization.English);

        var shown = sut.ShowIfFirstRun();

        Assert.True(shown);
        _tray.Verify(
            t => t.ShowInfoBalloon(
                "DesktopOrganizer is ready",
                "Drag desktop files onto the panel to group them. The app lives in the notification area (look under ^): right-click its icon for Settings or Exit."),
            Times.Once);
    }

    /// <summary>Verifies the balloon text follows the active language.</summary>
    [Fact]
    public void ShowIfFirstRun_Russian_ShowsRussianBalloon()
    {
        var sut = Build(StateLoadStatus.FirstRun, writable: true, TestLocalization.Russian);

        sut.ShowIfFirstRun();

        _tray.Verify(
            t => t.ShowInfoBalloon(
                "DesktopOrganizer готов к работе",
                "Перетащите файлы с рабочего стола на панель. Значок в области уведомлений (возможно, под «^»): щёлкните правой кнопкой — Параметры или Выход."),
            Times.Once);
    }

    /// <summary>Verifies that no other load status ever shows the balloon.</summary>
    /// <param name="status">What the load reported.</param>
    [Theory]
    [InlineData(StateLoadStatus.Loaded)]
    [InlineData(StateLoadStatus.RecoveredFromBackup)]
    [InlineData(StateLoadStatus.Quarantined)]
    [InlineData(StateLoadStatus.ImportedFromLegacy)]
    [InlineData(StateLoadStatus.NewerSchema)]
    public void ShowIfFirstRun_NotFirstRun_ShowsNothing(StateLoadStatus status)
    {
        var sut = Build(status, writable: true, TestLocalization.English);

        var shown = sut.ShowIfFirstRun();

        Assert.False(shown);
        _tray.Verify(t => t.ShowInfoBalloon(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>Verifies that a first run that cannot even save (read-only session) is not announced.</summary>
    [Fact]
    public void ShowIfFirstRun_FirstRunNotWritable_ShowsNothing()
    {
        var sut = Build(StateLoadStatus.FirstRun, writable: false, TestLocalization.English);

        Assert.False(sut.ShowIfFirstRun());
        _tray.Verify(t => t.ShowInfoBalloon(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>Verifies the balloon is shown at most once per session.</summary>
    [Fact]
    public void ShowIfFirstRun_CalledTwice_ShowsOnce()
    {
        var sut = Build(StateLoadStatus.FirstRun, writable: true, TestLocalization.English);

        Assert.True(sut.ShowIfFirstRun());
        Assert.False(sut.ShowIfFirstRun());

        _tray.Verify(t => t.ShowInfoBalloon(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Verifies the balloon fits the <c>NotifyIcon</c> limits (title at most 63, text at most 255 characters;
    /// longer values are truncated or rejected by Windows) in every shipped language, and that the menu items it
    /// tells the user to pick are named exactly like the tray menu items (<c>Tray_Settings</c>/<c>Tray_Exit</c>).
    /// </summary>
    /// <param name="language">The language under test.</param>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void ShowIfFirstRun_Text_FitsNotifyIconLimits_AndNamesTheTrayMenuItems(string language)
    {
        var localization = language == "ru" ? TestLocalization.Russian : TestLocalization.English;
        string? title = null;
        string? text = null;
        _tray
            .Setup(t => t.ShowInfoBalloon(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((t1, t2) => (title, text) = (t1, t2));
        var sut = Build(StateLoadStatus.FirstRun, writable: true, localization);

        sut.ShowIfFirstRun();

        Assert.NotNull(title);
        Assert.NotNull(text);
        Assert.InRange(title!.Length, 1, 63);
        Assert.InRange(text!.Length, 1, 255);
        Assert.Contains(localization.GetString("Tray_Settings"), text, StringComparison.Ordinal);
        Assert.Contains(localization.GetString("Tray_Exit"), text, StringComparison.Ordinal);
    }

    private FirstRunTrayNotice Build(StateLoadStatus status, bool writable, ILocalizationService localization)
    {
        _store.SetupGet(s => s.IsWritable).Returns(writable);
        _state.SetupGet(s => s.LastLoadStatus).Returns(status);
        return new FirstRunTrayNotice(_store.Object, _state.Object, _tray.Object, localization);
    }
}
