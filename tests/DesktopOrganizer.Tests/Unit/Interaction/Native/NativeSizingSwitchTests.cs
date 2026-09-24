using DesktopOrganizer.UI.Interaction.Native;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// Phase 2 (commit C2, docs/PLANS/phase-2-native-panel-sizing.md §1.4/§5, T-SW-01..05) tests for
/// <see cref="NativeSizingSwitch"/> — an exact-pattern clone of
/// <c>DesktopOrganizer.Services.Diagnostics.DiagnosticsSwitch</c>. Priority: env
/// <c>DESKTOPORGANIZER_NATIVESIZING</c> (<c>0</c>/<c>1</c>) &gt; CLI <c>--native-sizing</c>/
/// <c>--no-native-sizing</c> &gt; <c>settingValue</c>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingSwitchTests
{
    [Fact]
    public void IsEnabled_EnvironmentVariableOne_OverridesCommandLineAndSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.DisableCommandLineFlag },
            name => name == NativeSizingSwitch.EnvironmentVariableName ? "1" : null,
            settingValue: false);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableZero_OverridesCommandLineAndSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag },
            name => name == NativeSizingSwitch.EnvironmentVariableName ? "0" : null,
            settingValue: true);

        Assert.False(result);
    }

    [Fact]
    public void IsEnabled_NoEnvironment_CommandLineEnableFlag_OverridesSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag },
            _ => null,
            settingValue: false);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_NoEnvironment_CommandLineDisableFlag_OverridesSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.DisableCommandLineFlag },
            _ => null,
            settingValue: true);

        Assert.False(result);
    }

    [Fact]
    public void IsEnabled_NoEnvironmentNoCommandLine_FallsBackToSettingValue()
    {
        Assert.True(NativeSizingSwitch.IsEnabled(Array.Empty<string>(), _ => null, settingValue: true));
        Assert.False(NativeSizingSwitch.IsEnabled(Array.Empty<string>(), _ => null, settingValue: false));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("2")]
    public void IsEnabled_GarbageEnvironmentValue_IsIgnored_FallsThroughToCommandLine(string garbageValue)
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag },
            name => name == NativeSizingSwitch.EnvironmentVariableName ? garbageValue : null,
            settingValue: false);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_BothCommandLineFlagsPresent_DisableWins()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag, NativeSizingSwitch.DisableCommandLineFlag },
            _ => null,
            settingValue: true);

        Assert.False(result);
    }

    [Fact]
    public void IsEnabled_CommandLineFlagIsCaseInsensitive()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { "--NATIVE-SIZING" },
            _ => null,
            settingValue: false);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_NullCommandLineArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NativeSizingSwitch.IsEnabled(null!, _ => null, false));
    }

    [Fact]
    public void IsEnabled_NullEnvironmentLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NativeSizingSwitch.IsEnabled(Array.Empty<string>(), null!, false));
    }
}
