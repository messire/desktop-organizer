using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.UI.Lifecycle;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="CrashCoordinator"/> (ADR-033 part 2, release-hardening plan §2). Every
/// dependency is either a <see cref="Mock{T}"/> (for the two real interfaces this collaborates with) or a
/// plain recording delegate — both append to a single shared <c>_order</c> list, so ordering can be
/// asserted across the mix without needing Moq's <c>MockSequence</c> to span both kinds.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class CrashCoordinatorTests
{
    private readonly List<string> _order = [];
    private readonly Mock<IDesktopIconsService> _desktopIcons = new();
    private readonly Mock<IAppLog> _appLog = new();
    private bool _showDialogResult;
    private int? _exitCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashCoordinatorTests"/> class, wiring the two mocked
    /// collaborators to append to the same shared <see cref="_order"/> list every plain delegate below
    /// also appends to.
    /// </summary>
    public CrashCoordinatorTests()
    {
        _desktopIcons
            .Setup(x => x.SetIconsVisible(true))
            .Callback(() => _order.Add("Icons"));

        _appLog
            .Setup(x => x.Error(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback(() => _order.Add("AppLog"));
    }

    /// <summary>The Dispatcher-origin happy path runs every step, in order, ending with exit code 5.</summary>
    [Fact]
    public void HandleFatal_DispatcherOrigin_RunsEveryStepInOrder()
    {
        var sut = CreateSut(showDialogResult: false);

        sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        Assert.Equal(new List<string> { "Icons", "WriteReport", "AppLog", "Flush", "SaveState", "Dialog", "Exit" }, _order);
        Assert.Equal(ExitCodes.Crashed, _exitCode);
    }

    /// <summary>When the dialog reports the user asked to open the logs folder, that step runs after the dialog.</summary>
    [Fact]
    public void HandleFatal_DialogRequestsOpenLogsFolder_OpensIt()
    {
        var sut = CreateSut(showDialogResult: true);

        sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        Assert.Contains("OpenLogsFolder", _order);
        Assert.True(_order.IndexOf("Dialog") < _order.IndexOf("OpenLogsFolder"));
    }

    /// <summary>When the dialog does not report an "open logs folder" request, that step never runs.</summary>
    [Fact]
    public void HandleFatal_DialogDoesNotRequestOpenLogsFolder_NeverOpensIt()
    {
        var sut = CreateSut(showDialogResult: false);

        sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        Assert.DoesNotContain("OpenLogsFolder", _order);
    }

    /// <summary>Plan Q1: a background-thread origin never attempts to save application state.</summary>
    [Fact]
    public void HandleFatal_BackgroundOrigin_NeverSavesState()
    {
        var sut = CreateSut(showDialogResult: false);

        sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.Background);

        Assert.DoesNotContain("SaveState", _order);
        Assert.Contains("Icons", _order);
        Assert.Contains("WriteReport", _order);
        Assert.Contains("Exit", _order);
    }

    /// <summary>Plan Q1: a WinForms-origin crash — still the UI thread — does save application state.</summary>
    [Fact]
    public void HandleFatal_WinFormsOrigin_SavesState()
    {
        var sut = CreateSut(showDialogResult: false);

        sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.WinForms);

        Assert.Contains("SaveState", _order);
    }

    /// <summary>A failure in the icons step must not cancel any of the remaining steps.</summary>
    [Fact]
    public void HandleFatal_IconsStepThrows_RemainingStepsStillRun()
    {
        _desktopIcons.Setup(x => x.SetIconsVisible(true)).Throws(new InvalidOperationException("icons failed"));
        var sut = CreateSut(showDialogResult: false);

        var exception = Record.Exception(() => sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.Dispatcher));

        Assert.Null(exception);
        Assert.DoesNotContain("Icons", _order);
        Assert.Contains("WriteReport", _order);
        Assert.Contains("AppLog", _order);
        Assert.Contains("Flush", _order);
        Assert.Contains("SaveState", _order);
        Assert.Contains("Dialog", _order);
        Assert.Contains("Exit", _order);
        Assert.Equal(ExitCodes.Crashed, _exitCode);
    }

    /// <summary>Every step throwing must still never let an exception escape <see cref="CrashCoordinator.HandleFatal"/> itself.</summary>
    [Fact]
    public void HandleFatal_EveryStepThrows_NeverThrowsBackToCaller()
    {
        var sut = new CrashCoordinator(
            ThrowingDesktopIcons(),
            (_, _) => throw new InvalidOperationException(),
            ThrowingAppLog(),
            () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());

        var exception = Record.Exception(() => sut.HandleFatal(new InvalidOperationException("boom"), CrashOrigin.Dispatcher));

        Assert.Null(exception);
    }

    /// <summary>A second <see cref="CrashCoordinator.HandleFatal"/> call, for any exception, is a no-op.</summary>
    [Fact]
    public void HandleFatal_CalledTwice_SecondCallIsANoOp()
    {
        var sut = CreateSut(showDialogResult: false);

        sut.HandleFatal(new InvalidOperationException("first"), CrashOrigin.Dispatcher);
        var firstRunLength = _order.Count;

        sut.HandleFatal(new InvalidOperationException("second"), CrashOrigin.Dispatcher);

        Assert.Equal(firstRunLength, _order.Count);
    }

    /// <summary>A <see langword="null"/> exception is rejected up front, before any step runs.</summary>
    [Fact]
    public void HandleFatal_NullException_Throws()
    {
        var sut = CreateSut(showDialogResult: false);

        Assert.Throws<ArgumentNullException>(() => sut.HandleFatal(null!, CrashOrigin.Dispatcher));
    }

    private static IDesktopIconsService ThrowingDesktopIcons()
    {
        var mock = new Mock<IDesktopIconsService>();
        mock.Setup(x => x.SetIconsVisible(It.IsAny<bool>())).Throws(new InvalidOperationException());
        return mock.Object;
    }

    private static IAppLog ThrowingAppLog()
    {
        var mock = new Mock<IAppLog>();
        mock.Setup(x => x.Error(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()))
            .Throws(new InvalidOperationException());
        return mock.Object;
    }

    /// <summary>
    /// Builds a <see cref="CrashCoordinator"/> whose every delegate dependency appends a short label to
    /// <see cref="_order"/> (plus the two mocked collaborators wired in the constructor), so a single
    /// list can be asserted against regardless of which dependency ran.
    /// </summary>
    /// <param name="showDialogResult">What the fake crash dialog reports as its "open logs folder" answer.</param>
    private CrashCoordinator CreateSut(bool showDialogResult)
    {
        _showDialogResult = showDialogResult;

        return new CrashCoordinator(
            _desktopIcons.Object,
            (_, _) =>
            {
                _order.Add("WriteReport");
                return "C:\\fake\\crash-report.log";
            },
            _appLog.Object,
            () => _order.Add("Flush"),
            () => _order.Add("SaveState"),
            _ =>
            {
                _order.Add("Dialog");
                return _showDialogResult;
            },
            _ => _order.Add("OpenLogsFolder"),
            code =>
            {
                _order.Add("Exit");
                _exitCode = code;
            });
    }
}
