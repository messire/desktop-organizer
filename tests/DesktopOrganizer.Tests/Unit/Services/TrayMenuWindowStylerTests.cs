using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="TrayMenuWindowStyler"/> (design system 1.x, phase 6.4): Windows 11 gating, the
/// DWM corner preference/border colour for a palette and the system defaults for high contrast, the ARGB →
/// <c>COLORREF</c> conversion, and "never throws" with rate-limited logging — against a mocked <see cref="INativeApi"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class TrayMenuWindowStylerTests
{
    private const int Win11Build = 22631;
    private const int Win10Build22H2 = 19045;
    private const int EAccessDenied = unchecked((int)0x80070005);

    private static readonly IntPtr Hwnd = new(0x4567);
    private static readonly TrayMenuPalette Palette = new(0xFF2C2C2C, 0xFF102030, 0xFFFFFFFF, 0xFF7A7A7A, 0xFF3B3B3B, 0xFF404040, 0xFFFFFFFF, 4);

    private readonly Mock<INativeApi> _nativeApi = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly ManualTimeProvider _time = new();

    /// <summary>A palette: small rounded corners and the palette border as a COLORREF.</summary>
    [Fact]
    public void Apply_Palette_RoundSmallAndBorderColour()
    {
        var sut = CreateSut(Win11Build);

        sut.Apply(Hwnd, Palette);

        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, TrayMenuWindowStyler.DwmwaWindowCornerPreference, TrayMenuWindowStyler.DwmwcpRoundSmall), Times.Once);
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, TrayMenuWindowStyler.DwmwaBorderColor, 0x00302010), Times.Once);
    }

    /// <summary>High contrast (null): both attributes reset to the system defaults.</summary>
    [Fact]
    public void Apply_Null_ResetsToSystemDefaults()
    {
        var sut = CreateSut(Win11Build);

        sut.Apply(Hwnd, null);

        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, 33, 0), Times.Once);
        _nativeApi.Verify(api => api.SetDwmWindowAttribute(Hwnd, 34, unchecked((int)0xFFFFFFFF)), Times.Once);
    }

    /// <summary>Windows 10, the feature switch off, or a zero handle: no native call at all.</summary>
    /// <param name="build">Windows build.</param>
    /// <param name="enabled">Feature switch.</param>
    /// <param name="zeroHandle">Whether the handle is zero.</param>
    [Theory]
    [InlineData(Win10Build22H2, true, false)]
    [InlineData(21999, true, false)]
    [InlineData(Win11Build, false, false)]
    [InlineData(Win11Build, true, true)]
    public void Apply_Unsupported_NoNativeCall(int build, bool enabled, bool zeroHandle)
    {
        var sut = CreateSut(build, enabled);

        sut.Apply(zeroHandle ? IntPtr.Zero : Hwnd, Palette);

        _nativeApi.VerifyNoOtherCalls();
        Assert.Equal(build >= 22000 && enabled, sut.IsSupported);
    }

    /// <summary>Failures are logged, at most once per window per minute; never thrown.</summary>
    [Fact]
    public void Apply_Failure_LogsRateLimited_NeverThrows()
    {
        _nativeApi.Setup(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>())).Returns(EAccessDenied);
        var sut = CreateSut(Win11Build);

        sut.Apply(Hwnd, Palette);
        sut.Apply(Hwnd, Palette);
        _time.Advance(TimeSpan.FromMinutes(2));
        sut.Apply(Hwnd, Palette);

        _log.Verify(l => l.Warn("TrayMenuWindowStyler", "TrayMenuWindowStyleFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Exactly(2));
    }

    /// <summary>Even a throwing native façade does not escape.</summary>
    [Fact]
    public void Apply_NativeThrows_DoesNotEscape()
    {
        _nativeApi.Setup(api => api.SetDwmWindowAttribute(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>())).Throws<InvalidOperationException>();
        var sut = CreateSut(Win11Build);

        Assert.Null(Record.Exception(() => sut.Apply(Hwnd, Palette)));
        _log.Verify(l => l.Error("TrayMenuWindowStyler", "TrayMenuWindowStyleFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>ARGB → COLORREF drops alpha and swaps red/blue.</summary>
    /// <param name="argb">Palette colour.</param>
    /// <param name="expected">Expected COLORREF.</param>
    [Theory]
    [InlineData(0xFF102030u, 0x00302010)]
    [InlineData(0x00FFFFFFu, 0x00FFFFFF)]
    [InlineData(0xFFFF0000u, 0x000000FF)]
    public void ToColorRef_SwapsChannels(uint argb, int expected)
        => Assert.Equal(expected, TrayMenuWindowStyler.ToColorRef(argb));

    private TrayMenuWindowStyler CreateSut(int build, bool enabled = true)
        => new(_nativeApi.Object, _log.Object, build, enabled, _time);
}
