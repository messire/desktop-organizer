using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Theming;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="NativeMenuThemeAdapter"/> (design system 1.x, phase 6.4b, ADR-047 D-11): the
/// classic Win32 menus are requested dark exactly when the resolved application theme is
/// <see cref="AppTheme.Dark"/> — at start and on every <see cref="IAppThemeService.ThemeChanged"/> — and
/// <see cref="NativeMenuThemeAdapter.Dispose"/> unsubscribes.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeMenuThemeAdapterTests
{
    private readonly Mock<IAppThemeService> _appTheme = new();
    private readonly Mock<INativeMenuTheming> _menuTheming = new();

    /// <summary>Start applies the current theme: dark only for <see cref="AppTheme.Dark"/>.</summary>
    /// <param name="theme">The resolved theme at start.</param>
    /// <param name="expectedDark">The expected request.</param>
    [Theory]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Dark, true)]
    [InlineData(AppTheme.HighContrast, false)]
    public void Start_AppliesCurrentTheme(AppTheme theme, bool expectedDark)
    {
        _appTheme.SetupGet(s => s.Current).Returns(theme);
        using var sut = new NativeMenuThemeAdapter(_appTheme.Object, _menuTheming.Object);

        sut.Start();
        sut.Start();

        _menuTheming.Verify(m => m.TryApply(expectedDark), Times.Once);
        _menuTheming.VerifyNoOtherCalls();
    }

    /// <summary>Theme changes are forwarded as they arrive.</summary>
    [Fact]
    public void ThemeChanged_Forwarded()
    {
        _appTheme.SetupGet(s => s.Current).Returns(AppTheme.Light);
        using var sut = new NativeMenuThemeAdapter(_appTheme.Object, _menuTheming.Object);
        sut.Start();

        _appTheme.Raise(s => s.ThemeChanged += null, _appTheme.Object, AppTheme.Dark);
        _appTheme.Raise(s => s.ThemeChanged += null, _appTheme.Object, AppTheme.HighContrast);

        _menuTheming.Verify(m => m.TryApply(true), Times.Once);
        _menuTheming.Verify(m => m.TryApply(false), Times.Exactly(2));
    }

    /// <summary>After Dispose theme changes are ignored and Start is a no-op.</summary>
    [Fact]
    public void Dispose_Unsubscribes()
    {
        _appTheme.SetupGet(s => s.Current).Returns(AppTheme.Light);
        var sut = new NativeMenuThemeAdapter(_appTheme.Object, _menuTheming.Object);
        sut.Start();

        sut.Dispose();
        _appTheme.Raise(s => s.ThemeChanged += null, _appTheme.Object, AppTheme.Dark);
        sut.Start();

        _menuTheming.Verify(m => m.TryApply(true), Times.Never);
    }

    /// <summary>Null dependencies are rejected.</summary>
    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new NativeMenuThemeAdapter(null!, _menuTheming.Object));
        Assert.Throws<ArgumentNullException>(() => new NativeMenuThemeAdapter(_appTheme.Object, null!));
    }
}
