using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// Integration tests for the real <see cref="NativeApi"/> theme/DWM members and the DI registration of the
/// theme services (design system 1.x, phase 6.1). They only assert the "never throws" contracts and the
/// failure paths that are deterministic on any Windows machine — the actual theme values depend on the
/// user's settings and are manual-verify territory.
/// </summary>
[Trait(IntegrationTests, nameof(DesktopOrganizer.Services))]
public class NativeApiThemeTests
{
    private readonly NativeApi _nativeApi = new();

    /// <summary>The real Personalize values read as absent or 0/1 — never an exception.</summary>
    /// <param name="valueName">Registry value name.</param>
    [Theory]
    [InlineData(SystemThemeProvider.AppsUseLightThemeValueName)]
    [InlineData(SystemThemeProvider.SystemUsesLightThemeValueName)]
    public void ReadCurrentUserDword_Personalize_IsNullOrBoolean(string valueName)
    {
        var value = _nativeApi.ReadCurrentUserDword(SystemThemeProvider.PersonalizeKeyPath, valueName);

        Assert.True(value is null or 0 or 1, $"unexpected {valueName} = {value}");
    }

    /// <summary>A missing key, a missing value and an empty path all answer null.</summary>
    [Fact]
    public void ReadCurrentUserDword_Missing_ReturnsNull()
    {
        Assert.Null(_nativeApi.ReadCurrentUserDword(@"Software\DesktopOrganizer.Tests\" + Guid.NewGuid().ToString("N"), "Anything"));
        Assert.Null(_nativeApi.ReadCurrentUserDword(SystemThemeProvider.PersonalizeKeyPath, "DesktopOrganizer.NoSuchValue." + Guid.NewGuid().ToString("N")));
        Assert.Null(_nativeApi.ReadCurrentUserDword(string.Empty, "AppsUseLightTheme"));
    }

    /// <summary>The live high-contrast query never throws.</summary>
    [Fact]
    public void IsHighContrastOn_DoesNotThrow()
        => Assert.Null(Record.Exception(() => _nativeApi.IsHighContrastOn()));

    /// <summary>
    /// Every fingerprinted system colour reads as a <c>COLORREF</c> (<c>0x00BBGGRR</c>: high byte zero), and
    /// an out-of-range index never throws.
    /// </summary>
    [Fact]
    public void GetSystemColor_ReturnsColorRef_NeverThrows()
    {
        foreach (var index in SystemThemeProvider.FingerprintColorIndices)
        {
            var color = _nativeApi.GetSystemColor(index);
            Assert.True((color & unchecked((int)0xFF000000)) == 0, $"COLOR index {index} = 0x{color:X8} is not a COLORREF");
        }

        Assert.Null(Record.Exception(() => _nativeApi.GetSystemColor(-1)));
        Assert.Null(Record.Exception(() => _nativeApi.GetSystemColor(10_000)));
    }

    /// <summary>
    /// The live message font size is a plausible DIP size and matches WPF's own (cached) conversion of the
    /// same <c>NONCLIENTMETRICS</c> value — which also pins the native struct layout (a wrong offset for
    /// <c>lfMessageFont</c> would read an unrelated field).
    /// </summary>
    [Fact]
    public void GetMessageFontSize_MatchesWpfSystemFonts()
    {
        var size = _nativeApi.GetMessageFontSize();

        Assert.InRange(size, 6d, 72d);
        Assert.InRange(size, System.Windows.SystemFonts.MessageFontSize - 1d, System.Windows.SystemFonts.MessageFontSize + 1d);
    }

    /// <summary>The real provider's snapshot carries a non-zero colour fingerprint and the live message font size.</summary>
    [Fact]
    public void SystemThemeProvider_RealNativeApi_SnapshotCarriesFingerprintAndFont()
    {
        using var provider = new SystemThemeProvider(_nativeApi);

        var snapshot = provider.Current;

        Assert.NotEqual(0, snapshot.SystemColorsFingerprint);
        Assert.Equal(_nativeApi.GetMessageFontSize(), snapshot.MessageFontSize);
    }

    /// <summary>A zero or dead window handle yields a failure HRESULT, never an exception.</summary>
    [Fact]
    public void SetDwmWindowAttribute_InvalidHandle_ReturnsFailureHResult()
    {
        Assert.True(_nativeApi.SetDwmWindowAttribute(IntPtr.Zero, WindowFrameTheming.DwmwaUseImmersiveDarkMode, 1) < 0);
        Assert.True(_nativeApi.SetDwmWindowAttribute(new IntPtr(0x7FFF_FFF0), WindowFrameTheming.DwmwaUseImmersiveDarkMode, 1) < 0);
    }

    /// <summary>
    /// Phase 6.4b: the undocumented uxtheme ordinals resolve and run on every supported build (this machine's
    /// build decides which branch is asserted) — called only with the harmless <c>Default</c> mode, which is
    /// the state every process starts in — and never throw.
    /// </summary>
    [Fact]
    public void UxThemeMenuExports_DefaultMode_ResolveOnSupportedBuilds_NeverThrow()
    {
        var supported = NativeMenuTheming.IsSupportedOn(Environment.OSVersion.Version.Build, featureEnabled: true);

        var setMode = _nativeApi.TrySetPreferredAppMode(NativeMenuTheming.PreferredAppModeDefault);
        var flush = _nativeApi.TryFlushMenuThemes();

        if (supported)
        {
            Assert.True(setMode);
            Assert.True(flush);
        }
    }

    /// <summary>
    /// Review L-2: after <see cref="NativeApi.Dispose"/> released the uxtheme reference, the menu-theme calls
    /// are no-ops answering <see langword="false"/> (no export of a freed module is resolved, and no fresh,
    /// never-released reference is loaded), and a second <see cref="NativeApi.Dispose"/> neither throws nor
    /// frees again.
    /// </summary>
    [Fact]
    public void UxThemeMenuExports_AfterDispose_AreNoOps_DoubleDisposeIsSafe()
    {
        var nativeApi = new NativeApi();
        _ = nativeApi.TryFlushMenuThemes(); // loads the module (on every supported build)

        nativeApi.Dispose();
        var secondDispose = Record.Exception(nativeApi.Dispose);

        Assert.Null(secondDispose);
        Assert.False(nativeApi.TrySetPreferredAppMode(NativeMenuTheming.PreferredAppModeDefault));
        Assert.False(nativeApi.TryFlushMenuThemes());
    }

    /// <summary>
    /// Review L-2: an instance disposed before its first menu-theme call never loads uxtheme afterwards —
    /// the calls answer <see langword="false"/> and a repeated <see cref="NativeApi.Dispose"/> stays safe.
    /// </summary>
    [Fact]
    public void UxThemeMenuExports_DisposedBeforeFirstUse_NeverLoad()
    {
        var nativeApi = new NativeApi();

        nativeApi.Dispose();

        Assert.False(nativeApi.TryFlushMenuThemes());
        Assert.False(nativeApi.TrySetPreferredAppMode(NativeMenuTheming.PreferredAppModeDefault));
        Assert.Null(Record.Exception(nativeApi.Dispose));
    }

    /// <summary>
    /// Phase 6.4: the tray popup styler and the native menu theming resolve as singletons, and
    /// <see cref="ITrayIconManager"/> is constructed through the overload that receives the styler.
    /// </summary>
    [Fact]
    public void RegisterServices_TrayAndNativeMenuTheming_ResolveAsSingletons()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        using var provider = services.BuildServiceProvider();

        var styler = provider.GetRequiredService<DesktopOrganizer.Services.Interfaces.ITrayMenuWindowStyler>();
        var menuTheming = provider.GetRequiredService<INativeMenuTheming>();

        Assert.IsType<TrayMenuWindowStyler>(styler);
        Assert.IsType<NativeMenuTheming>(menuTheming);
        Assert.Same(styler, provider.GetRequiredService<DesktopOrganizer.Services.Interfaces.ITrayMenuWindowStyler>());
        Assert.Same(menuTheming, provider.GetRequiredService<INativeMenuTheming>());
    }

    /// <summary>
    /// Both theme services resolve as singletons from the Services container; merely resolving the
    /// provider must not start listening (that is an explicit Start on the UI thread).
    /// </summary>
    [Fact]
    public void RegisterServices_ThemeServices_ResolveAsSingletons_ProviderInertUntilStart()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        using var provider = services.BuildServiceProvider();

        var themeProvider = provider.GetRequiredService<ISystemThemeProvider>();
        var frameTheming = provider.GetRequiredService<IWindowFrameTheming>();

        Assert.Same(themeProvider, provider.GetRequiredService<ISystemThemeProvider>());
        Assert.Same(frameTheming, provider.GetRequiredService<IWindowFrameTheming>());
        Assert.IsType<SystemThemeProvider>(themeProvider);
        Assert.IsType<WindowFrameTheming>(frameTheming);
        Assert.False(themeProvider.IsListening);
    }
}
