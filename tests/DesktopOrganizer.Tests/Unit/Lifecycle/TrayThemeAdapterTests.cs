using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Theming;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="TrayThemeAdapter"/> (design system 1.x, docs/PLANS/design-system-1x.md §2.9,
/// phase 6.4, T-11): the start-up push, re-pushes on a Windows-mode change and on an application-theme
/// (high contrast) change — both through the injected deferral, never inline — the dark-theme gate, and
/// <see cref="TrayThemeAdapter.Dispose"/> unsubscribing and cancelling an already-queued refresh.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TrayThemeAdapterTests
{
    private readonly Mock<ITrayIconManager> _tray = new();
    private readonly Mock<IAppThemeService> _appTheme = new();
    private readonly Mock<ISystemThemeProvider> _systemTheme = new();
    private readonly Queue<Action> _deferred = new();
    private readonly List<TrayMenuPalette?> _pushed = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayThemeAdapterTests"/> class — light Windows, light app
    /// theme, every <see cref="ITrayIconManager.SetMenuPalette"/> call recorded.
    /// </summary>
    public TrayThemeAdapterTests()
    {
        _appTheme.SetupGet(s => s.Current).Returns(AppTheme.Light);
        _systemTheme.SetupGet(s => s.Current).Returns(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
        _tray.Setup(t => t.SetMenuPalette(It.IsAny<TrayMenuPalette?>())).Callback<TrayMenuPalette?>(p => _pushed.Add(p));
    }

    /// <summary>Start pushes the current palette synchronously, exactly once, even if called twice.</summary>
    [Fact]
    public void Start_PushesCurrentPaletteOnce()
    {
        var sut = CreateSut(darkThemeEnabled: true);

        sut.Start();
        sut.Start();

        Assert.Equal([TrayMenuPaletteFactory.Light], _pushed);
        Assert.Empty(_deferred);
    }

    /// <summary>A Windows-mode change is deferred, then pushes the dark palette (gate open).</summary>
    [Fact]
    public void SystemThemeChanged_ToDark_PushesDarkThroughDefer()
    {
        var sut = CreateSut(darkThemeEnabled: true);
        sut.Start();
        var dark = new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: false);
        _systemTheme.SetupGet(s => s.Current).Returns(dark);

        _systemTheme.Raise(s => s.Changed += null, _systemTheme.Object, dark);
        Assert.Single(_pushed);

        RunDeferred();

        Assert.Equal(TrayMenuPaletteFactory.Dark, _pushed[^1]);
    }

    /// <summary>With the dark-theme gate closed the tray stays light even in dark Windows mode.</summary>
    [Fact]
    public void GateClosed_DarkWindowsMode_StaysLight()
    {
        _systemTheme.SetupGet(s => s.Current).Returns(new SystemThemeSnapshot(AppsUseLightTheme: false, SystemUsesLightTheme: false));
        var sut = CreateSut(darkThemeEnabled: false);

        sut.Start();

        Assert.Equal([TrayMenuPaletteFactory.Light], _pushed);
    }

    /// <summary>The application theme switching to high contrast pushes null (system renderer).</summary>
    [Fact]
    public void AppThemeChanged_ToHighContrast_PushesNull()
    {
        var sut = CreateSut(darkThemeEnabled: true);
        sut.Start();
        _appTheme.SetupGet(s => s.Current).Returns(AppTheme.HighContrast);

        _appTheme.Raise(s => s.ThemeChanged += null, _appTheme.Object, AppTheme.HighContrast);
        RunDeferred();

        Assert.Null(_pushed[^1]);
    }

    /// <summary>Dispose unsubscribes from both events.</summary>
    [Fact]
    public void Dispose_Unsubscribes()
    {
        var sut = CreateSut(darkThemeEnabled: true);
        sut.Start();

        sut.Dispose();
        _systemTheme.Raise(s => s.Changed += null, _systemTheme.Object, SystemThemeSnapshot.Default);
        _appTheme.Raise(s => s.ThemeChanged += null, _appTheme.Object, AppTheme.Dark);

        Assert.Empty(_deferred);
        Assert.Single(_pushed);
    }

    /// <summary>A refresh queued before Dispose does not push afterwards; Start after Dispose is a no-op.</summary>
    [Fact]
    public void QueuedRefresh_AfterDispose_DoesNotPush()
    {
        var sut = CreateSut(darkThemeEnabled: true);
        sut.Start();
        _systemTheme.Raise(s => s.Changed += null, _systemTheme.Object, SystemThemeSnapshot.Default);

        sut.Dispose();
        RunDeferred();
        sut.Start();

        Assert.Single(_pushed);
    }

    /// <summary>The public constructor rejects null dependencies.</summary>
    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new TrayThemeAdapter(null!, _appTheme.Object, _systemTheme.Object));
        Assert.Throws<ArgumentNullException>(() => new TrayThemeAdapter(_tray.Object, null!, _systemTheme.Object));
        Assert.Throws<ArgumentNullException>(() => new TrayThemeAdapter(_tray.Object, _appTheme.Object, null!));
    }

    private TrayThemeAdapter CreateSut(bool darkThemeEnabled)
        => new(_tray.Object, _appTheme.Object, _systemTheme.Object, _deferred.Enqueue, darkThemeEnabled);

    private void RunDeferred()
    {
        while (_deferred.Count > 0)
        {
            _deferred.Dequeue()();
        }
    }
}
