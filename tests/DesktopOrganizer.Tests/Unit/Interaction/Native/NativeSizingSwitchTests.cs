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
    /// <summary>Verifies that an environment variable value of <c>"1"</c> overrides both the command-line flag and the setting value.</summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableOne_OverridesCommandLineAndSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.DisableCommandLineFlag },
            name => name == NativeSizingSwitch.EnvironmentVariableName ? "1" : null,
            settingValue: false);

        Assert.True(result);
    }

    /// <summary>Verifies that an environment variable value of <c>"0"</c> overrides both the command-line flag and the setting value.</summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableZero_OverridesCommandLineAndSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag },
            name => name == NativeSizingSwitch.EnvironmentVariableName ? "0" : null,
            settingValue: true);

        Assert.False(result);
    }

    /// <summary>Verifies that with no environment variable set, the command-line enable flag overrides the setting value.</summary>
    [Fact]
    public void IsEnabled_NoEnvironment_CommandLineEnableFlag_OverridesSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag },
            _ => null,
            settingValue: false);

        Assert.True(result);
    }

    /// <summary>Verifies that with no environment variable set, the command-line disable flag overrides the setting value.</summary>
    [Fact]
    public void IsEnabled_NoEnvironment_CommandLineDisableFlag_OverridesSetting()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.DisableCommandLineFlag },
            _ => null,
            settingValue: true);

        Assert.False(result);
    }

    /// <summary>Verifies that with neither an environment variable nor a command-line flag, <see cref="NativeSizingSwitch.IsEnabled"/> falls back to the setting value.</summary>
    [Fact]
    public void IsEnabled_NoEnvironmentNoCommandLine_FallsBackToSettingValue()
    {
        Assert.True(NativeSizingSwitch.IsEnabled(Array.Empty<string>(), _ => null, settingValue: true));
        Assert.False(NativeSizingSwitch.IsEnabled(Array.Empty<string>(), _ => null, settingValue: false));
    }

    /// <summary>Verifies that an unrecognized <paramref name="garbageValue"/> for the environment variable is ignored, falling through to the command-line flag.</summary>
    /// <param name="garbageValue">A value that is neither <c>"0"</c> nor <c>"1"</c>.</param>
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

    /// <summary>Verifies that when both the enable and disable command-line flags are present, disable wins.</summary>
    [Fact]
    public void IsEnabled_BothCommandLineFlagsPresent_DisableWins()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { NativeSizingSwitch.EnableCommandLineFlag, NativeSizingSwitch.DisableCommandLineFlag },
            _ => null,
            settingValue: true);

        Assert.False(result);
    }

    /// <summary>Verifies that the command-line flag is matched case-insensitively.</summary>
    [Fact]
    public void IsEnabled_CommandLineFlagIsCaseInsensitive()
    {
        var result = NativeSizingSwitch.IsEnabled(
            new[] { "--NATIVE-SIZING" },
            _ => null,
            settingValue: false);

        Assert.True(result);
    }

    /// <summary>Verifies that <see cref="NativeSizingSwitch.IsEnabled"/> throws <see cref="ArgumentNullException"/> for <see langword="null"/> command-line args.</summary>
    [Fact]
    public void IsEnabled_NullCommandLineArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NativeSizingSwitch.IsEnabled(null!, _ => null, false));
    }

    /// <summary>Verifies that <see cref="NativeSizingSwitch.IsEnabled"/> throws <see cref="ArgumentNullException"/> for a <see langword="null"/> environment lookup delegate.</summary>
    [Fact]
    public void IsEnabled_NullEnvironmentLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NativeSizingSwitch.IsEnabled(Array.Empty<string>(), null!, false));
    }
}
