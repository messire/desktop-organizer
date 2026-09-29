using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Theming;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// STA unit tests for <see cref="ThemedWindowBehavior"/> (docs/PLANS/design-system-1x.md §2.1/§3 phase
/// 6.1, ADR-047 D-2) against a mocked <see cref="IWindowFrameTheming"/> and <see cref="IAppThemeService"/> —
/// a real, off-screen, never-activated <see cref="Window"/> is used so <see cref="Window.SourceInitialized"/>
/// fires for real (no fake can raise that without a genuine <c>HWND</c>).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ThemedWindowBehaviorTests
{
    /// <summary><see cref="Window.SourceInitialized"/> applies <see cref="IAppThemeService.Current"/> to the now-real <c>HWND</c>.</summary>
    [Fact]
    public void SourceInitialized_AppliesTheCurrentTheme()
    {
        StaTestRunner.Run(() =>
        {
            var appThemeService = new Mock<IAppThemeService>();
            appThemeService.SetupGet(s => s.Current).Returns(AppTheme.Dark);
            var windowFrameTheming = new Mock<IWindowFrameTheming>();
            windowFrameTheming.Setup(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>())).Returns(true);

            var window = BuildOffscreenWindow();
            _ = new ThemedWindowBehavior(window, appThemeService.Object, windowFrameTheming.Object);

            try
            {
                window.Show(); // raises SourceInitialized for real, with a genuine HWND

                windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), true), Times.Once);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A later <see cref="IAppThemeService.ThemeChanged"/> re-applies with the newly resolved theme.</summary>
    [Fact]
    public void ThemeChanged_AfterSourceInitialized_ReappliesWithTheNewTheme()
    {
        StaTestRunner.Run(() =>
        {
            var appThemeService = new Mock<IAppThemeService>();
            appThemeService.SetupGet(s => s.Current).Returns(AppTheme.Light);
            var windowFrameTheming = new Mock<IWindowFrameTheming>();
            windowFrameTheming.Setup(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>())).Returns(true);

            var window = BuildOffscreenWindow();
            _ = new ThemedWindowBehavior(window, appThemeService.Object, windowFrameTheming.Object);

            try
            {
                window.Show();
                windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), false), Times.Once);

                appThemeService.Raise(s => s.ThemeChanged += null, appThemeService.Object, AppTheme.Dark);

                windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), true), Times.Once);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>High contrast is never dark — see <see cref="ThemedWindowBehavior"/>'s own remarks.</summary>
    [Fact]
    public void ThemeChanged_HighContrast_AppliesLightFrame()
    {
        StaTestRunner.Run(() =>
        {
            var appThemeService = new Mock<IAppThemeService>();
            appThemeService.SetupGet(s => s.Current).Returns(AppTheme.Light);
            var windowFrameTheming = new Mock<IWindowFrameTheming>();
            windowFrameTheming.Setup(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>())).Returns(true);

            var window = BuildOffscreenWindow();
            _ = new ThemedWindowBehavior(window, appThemeService.Object, windowFrameTheming.Object);

            try
            {
                window.Show();

                appThemeService.Raise(s => s.ThemeChanged += null, appThemeService.Object, AppTheme.HighContrast);

                windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), false), Times.Exactly(2)); // initial Light + HighContrast, both "not dark"
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A <see cref="IAppThemeService.ThemeChanged"/> event arriving before a real <c>HWND</c> exists is a safe no-op.</summary>
    [Fact]
    public void ThemeChanged_BeforeSourceInitialized_DoesNotThrowAndDoesNotApply()
    {
        StaTestRunner.Run(() =>
        {
            var appThemeService = new Mock<IAppThemeService>();
            appThemeService.SetupGet(s => s.Current).Returns(AppTheme.Light);
            var windowFrameTheming = new Mock<IWindowFrameTheming>();

            var window = BuildOffscreenWindow();
            _ = new ThemedWindowBehavior(window, appThemeService.Object, windowFrameTheming.Object);

            // No Show() yet — the window has no HWND.
            appThemeService.Raise(s => s.ThemeChanged += null, appThemeService.Object, AppTheme.Dark);

            windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>()), Times.Never);
        });
    }

    /// <summary><see cref="Window.Closed"/> self-disposes the behavior — a later <see cref="IAppThemeService.ThemeChanged"/> is ignored.</summary>
    [Fact]
    public void Closed_UnsubscribesFromThemeChanged()
    {
        StaTestRunner.Run(() =>
        {
            var appThemeService = new Mock<IAppThemeService>();
            appThemeService.SetupGet(s => s.Current).Returns(AppTheme.Light);
            var windowFrameTheming = new Mock<IWindowFrameTheming>();
            windowFrameTheming.Setup(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>())).Returns(true);

            var window = BuildOffscreenWindow();
            _ = new ThemedWindowBehavior(window, appThemeService.Object, windowFrameTheming.Object);

            window.Show();
            window.Close();

            windowFrameTheming.Invocations.Clear();
            appThemeService.Raise(s => s.ThemeChanged += null, appThemeService.Object, AppTheme.Dark);

            windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>()), Times.Never);
        });
    }

    /// <summary>An explicit <see cref="ThemedWindowBehavior.Dispose"/> unsubscribes from both <see cref="Window.SourceInitialized"/>/<see cref="Window.Closed"/> and <see cref="IAppThemeService.ThemeChanged"/>.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromBothEvents()
    {
        StaTestRunner.Run(() =>
        {
            var appThemeService = new Mock<IAppThemeService>();
            appThemeService.SetupGet(s => s.Current).Returns(AppTheme.Light);
            var windowFrameTheming = new Mock<IWindowFrameTheming>();
            windowFrameTheming.Setup(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>())).Returns(true);

            var window = BuildOffscreenWindow();
            var behavior = new ThemedWindowBehavior(window, appThemeService.Object, windowFrameTheming.Object);

            try
            {
                window.Show();
                behavior.Dispose();

                windowFrameTheming.Invocations.Clear();
                appThemeService.Raise(s => s.ThemeChanged += null, appThemeService.Object, AppTheme.Dark);

                windowFrameTheming.Verify(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>()), Times.Never);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Same off-screen/never-activated precaution every other real-<see cref="Window"/> harness in this project uses.</summary>
    private static Window BuildOffscreenWindow() => new()
    {
        ShowActivated = false,
        Left = -5000,
        Top = -5000,
        Width = 50,
        Height = 50,
    };
}
