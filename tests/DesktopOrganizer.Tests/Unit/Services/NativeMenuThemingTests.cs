using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="NativeMenuTheming"/> (design system 1.x, phase 6.4b, ADR-047 D-11): version and
/// feature gating of the undocumented uxtheme ordinals, "light before dark makes no native call",
/// idempotency, the <c>ForceDark</c>/<c>Default</c> mode values, and graceful failure when the export is
/// missing — all against a mocked <see cref="INativeApi"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class NativeMenuThemingTests
{
    private const int Win11Build = 26100;
    private const int Win10Build22H2 = 19045;
    private const int Win10Build1903 = 18362;
    private const int Win10Build1809 = 17763;

    private readonly Mock<INativeApi> _nativeApi = new();
    private readonly Mock<IAppLog> _log = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeMenuThemingTests"/> class — both exports present.
    /// </summary>
    public NativeMenuThemingTests()
    {
        _nativeApi.Setup(api => api.TrySetPreferredAppMode(It.IsAny<int>())).Returns(true);
        _nativeApi.Setup(api => api.TryFlushMenuThemes()).Returns(true);
    }

    /// <summary>Gate matrix: build ≥ 1903 AND feature constant on.</summary>
    /// <param name="build">Windows build.</param>
    /// <param name="enabled">Feature gate.</param>
    /// <param name="expected">Expected support.</param>
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(Win10Build1809, true, false)]
    [InlineData(Win10Build1903 - 1, true, false)]
    [InlineData(Win10Build1903, true, true)]
    [InlineData(Win10Build22H2, true, true)]
    [InlineData(Win11Build, true, true)]
    [InlineData(Win11Build, false, false)]
    public void IsSupportedOn_GatesOnBuildAndFeature(int build, bool enabled, bool expected)
    {
        Assert.Equal(expected, NativeMenuTheming.IsSupportedOn(build, enabled));
        Assert.Equal(expected, CreateSut(build, enabled).IsSupported);
    }

    /// <summary>The production feature constant is on and the gate starts at Windows 10 1903.</summary>
    [Fact]
    public void ProductionConstants()
    {
        Assert.True(NativeMenuTheming.FeatureEnabled);
        Assert.Equal(18362, NativeMenuTheming.MinimumSupportedBuild);
        Assert.Equal(0, NativeMenuTheming.PreferredAppModeDefault);
        Assert.Equal(2, NativeMenuTheming.PreferredAppModeForceDark);
    }

    /// <summary>Unsupported (old build or feature off): never a native call, always false.</summary>
    /// <param name="build">Windows build.</param>
    /// <param name="enabled">Feature gate.</param>
    [Theory]
    [InlineData(Win10Build1809, true)]
    [InlineData(Win11Build, false)]
    public void TryApply_Unsupported_NoNativeCall(int build, bool enabled)
    {
        var sut = CreateSut(build, enabled);

        Assert.False(sut.TryApply(true));
        Assert.False(sut.TryApply(false));
        _nativeApi.VerifyNoOtherCalls();
    }

    /// <summary>Light before dark was ever requested is already the process default — no native call.</summary>
    [Fact]
    public void TryApply_LightFirst_NoNativeCall()
    {
        var sut = CreateSut(Win11Build);

        Assert.True(sut.TryApply(false));
        Assert.True(sut.TryApply(false));

        _nativeApi.VerifyNoOtherCalls();
    }

    /// <summary>Dark → ForceDark + flush, once; back to light → Default + flush.</summary>
    [Fact]
    public void TryApply_DarkThenLight_SetsModesAndFlushes_Idempotently()
    {
        var sut = CreateSut(Win10Build22H2);

        Assert.True(sut.TryApply(true));
        Assert.True(sut.TryApply(true));
        _nativeApi.Verify(api => api.TrySetPreferredAppMode(NativeMenuTheming.PreferredAppModeForceDark), Times.Once);
        _nativeApi.Verify(api => api.TryFlushMenuThemes(), Times.Once);

        Assert.True(sut.TryApply(false));
        _nativeApi.Verify(api => api.TrySetPreferredAppMode(NativeMenuTheming.PreferredAppModeDefault), Times.Once);
        _nativeApi.Verify(api => api.TryFlushMenuThemes(), Times.Exactly(2));
    }

    /// <summary>A missing SetPreferredAppMode export: false, no flush, logged once, retried later (state not advanced).</summary>
    [Fact]
    public void TryApply_ExportMissing_ReturnsFalse_LogsOnce()
    {
        _nativeApi.Setup(api => api.TrySetPreferredAppMode(It.IsAny<int>())).Returns(false);
        var sut = CreateSut(Win11Build);

        Assert.False(sut.TryApply(true));
        Assert.False(sut.TryApply(true));

        _nativeApi.Verify(api => api.TryFlushMenuThemes(), Times.Never);
        _nativeApi.Verify(api => api.TrySetPreferredAppMode(NativeMenuTheming.PreferredAppModeForceDark), Times.Exactly(2));
        _log.Verify(l => l.Warn("NativeMenuTheming", "SetPreferredAppModeUnavailable", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A missing flush export does not undo the mode switch — true, with a warning.</summary>
    [Fact]
    public void TryApply_FlushMissing_StillTrue_Warns()
    {
        _nativeApi.Setup(api => api.TryFlushMenuThemes()).Returns(false);
        var sut = CreateSut(Win11Build);

        Assert.True(sut.TryApply(true));

        _log.Verify(l => l.Warn("NativeMenuTheming", "FlushMenuThemesUnavailable", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A null native façade is rejected.</summary>
    [Fact]
    public void Constructor_NullNativeApi_Throws()
        => Assert.Throws<ArgumentNullException>(() => new NativeMenuTheming(null!, _log.Object, Win11Build, featureEnabled: true));

    private NativeMenuTheming CreateSut(int build, bool enabled = true) => new(_nativeApi.Object, _log.Object, build, enabled);
}
