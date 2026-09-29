using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="WindowFrameTheming"/> (design system 1.x, phase 6.1): version gating of
/// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (19 before Windows 10 20H1, 20 after, nothing before 1809),
/// idempotency, and "never throws" on DWM failures — all against a mocked <see cref="INativeApi"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class WindowFrameThemingTests
{
    private const int Win11Build = 26100;
    private const int Win10Build22H2 = 19045;
    private const int Win10Build1903 = 18362;
    private const int Win10Build1809 = 17763;
    private const int Win10Build1803 = 17134;
    private const int EAccessDenied = unchecked((int)0x80070005);

    private static readonly IntPtr Hwnd = new(0x3456);

    private readonly Mock<INativeApi> _nativeApi = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly ManualTimeProvider _time = new();

    /// <summary>Build → attribute id mapping at and around every boundary.</summary>
    /// <param name="build">Windows build number.</param>
    /// <param name="expected">Expected attribute id (null = unsupported).</param>
    [Theory]
    [InlineData(0, null)]
    [InlineData(Win10Build1803, null)]
    [InlineData(Win10Build1809 - 1, null)]
    [InlineData(Win10Build1809, 19)]
    [InlineData(Win10Build1903, 19)]
    [InlineData(18984, 19)]
    [InlineData(18985, 20)]
    [InlineData(19041, 20)]
    [InlineData(Win10Build22H2, 20)]
    [InlineData(22000, 20)]
    [InlineData(Win11Build, 20)]
    public void GetImmersiveDarkModeAttribute_GatesOnBuild(int build, int? expected)
        => Assert.Equal(expected, WindowFrameTheming.GetImmersiveDarkModeAttribute(build));

    /// <summary>Windows 11 / Windows 10 20H1+: attribute 20 with BOOL 1 for dark and 0 for light.</summary>
    /// <param name="build">Windows build number.</param>
    /// <param name="dark">Requested frame.</param>
    [Theory]
    [InlineData(Win11Build, true)]
    [InlineData(Win11Build, false)]
    [InlineData(Win10Build22H2, true)]
    public void Apply_ModernBuild_UsesAttribute20(int build, bool dark)
    {
        _nativeApi.Setup(api => api.SetDwmWindowAttribute(Hwnd, 20, dark ? 1 : 0)).Returns(0);
        var sut = CreateSut(build);

        Assert.True(sut.IsSupported);
        Assert.True(sut.TryApplyImmersiveDarkMode(Hwnd, dark));
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, 20, dark ? 1 : 0), Times.Once);
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), 19, It.IsAny<int>()), Times.Never);
    }

    /// <summary>Windows 10 1809–1909: the pre-20H1 attribute 19.</summary>
    [Fact]
    public void Apply_Pre20H1Build_UsesAttribute19()
    {
        _nativeApi.Setup(api => api.SetDwmWindowAttribute(Hwnd, 19, 1)).Returns(0);
        var sut = CreateSut(Win10Build1903);

        Assert.True(sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, 19, 1), Times.Once);
    }

    /// <summary>Unsupported build: no native call at all, false, and the "unsupported" fact is logged exactly once.</summary>
    [Fact]
    public void Apply_UnsupportedBuild_NoOp_LogsOnce()
    {
        var sut = CreateSut(Win10Build1803);

        Assert.False(sut.IsSupported);
        Assert.False(sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));
        Assert.False(sut.TryApplyImmersiveDarkMode(Hwnd, dark: false));

        _nativeApi.Verify(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _log.Verify(l => l.Info("WindowFrameTheming", "ImmersiveDarkModeUnsupported", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A zero handle is rejected without a native call.</summary>
    [Fact]
    public void Apply_ZeroHandle_ReturnsFalse_NoNativeCall()
    {
        var sut = CreateSut(Win11Build);

        Assert.False(sut.TryApplyImmersiveDarkMode(IntPtr.Zero, dark: true));
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>Repeating the same request gives the same answer and sends the same single attribute value — nothing accumulates.</summary>
    [Fact]
    public void Apply_IsIdempotent()
    {
        _nativeApi.Setup(api => api.SetDwmWindowAttribute(Hwnd, 20, 1)).Returns(0);
        var sut = CreateSut(Win11Build);

        Assert.True(sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));
        Assert.True(sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));

        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, 20, 1), Times.Exactly(2));
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), It.IsAny<int>(), 0), Times.Never);
        _nativeApi.VerifyNoOtherCalls();
    }

    /// <summary>A failing HRESULT answers false and is logged once per window per minute (rate-limited), never thrown.</summary>
    [Fact]
    public void Apply_FailedHResult_ReturnsFalse_RateLimitedWarning()
    {
        _nativeApi.Setup(api => api.SetDwmWindowAttribute(Hwnd, 20, It.IsAny<int>())).Returns(EAccessDenied);
        var sut = CreateSut(Win11Build);

        Assert.False(sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));
        Assert.False(sut.TryApplyImmersiveDarkMode(Hwnd, dark: false));

        _log.Verify(l => l.Warn("WindowFrameTheming", "ImmersiveDarkModeFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));

        _log.Verify(l => l.Warn("WindowFrameTheming", "ImmersiveDarkModeFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Exactly(2));
    }

    /// <summary>Even a native layer that throws (contract violation) is contained: false, logged, no exception.</summary>
    [Fact]
    public void Apply_NativeThrows_ReturnsFalse_LogsError()
    {
        _nativeApi
            .Setup(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new DllNotFoundException("dwmapi"));
        var sut = CreateSut(Win11Build);

        var result = true;
        var ex = Record.Exception(() => result = sut.TryApplyImmersiveDarkMode(Hwnd, dark: true));

        Assert.Null(ex);
        Assert.False(result);
        _log.Verify(
            l => l.Error("WindowFrameTheming", "ImmersiveDarkModeFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>The production constructor gates on the real OS build (this machine runs Windows 10 1809+/11).</summary>
    [Fact]
    public void ProductionConstructor_OnSupportedWindows_IsSupported()
    {
        var sut = new WindowFrameTheming(_nativeApi.Object, _log.Object);

        Assert.Equal(Environment.OSVersion.Version.Build >= WindowFrameTheming.MinimumSupportedBuild, sut.IsSupported);
    }

    private WindowFrameTheming CreateSut(int build) => new(_nativeApi.Object, _log.Object, build, _time);
}
