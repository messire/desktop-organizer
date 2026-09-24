using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="DiagnosticsSwitch"/>.
/// </summary>
/// <remarks>
/// <see cref="DiagnosticsSwitch.IsEnabled"/> always returns <see langword="true"/> in a Debug build of
/// <c>DesktopOrganizer.Services</c> (<c>#if DEBUG</c> — the assembly the type is compiled into, which
/// tracks this test project's own configuration since project references propagate <c>Configuration</c>).
/// Rather than special-casing assertions per build configuration, tests that would otherwise depend on
/// that compile-time default assert equality against <see cref="CurrentBuildIsDebug"/> — computed via the
/// same <c>#if DEBUG</c> — so the suite is meaningful and green under both <c>dotnet test</c> (Debug) and
/// <c>dotnet test -c Release</c>. Tests whose outcome doesn't depend on the Debug default (an explicit
/// command-line flag or <c>=1</c> env var always wins) assert a fixed <see langword="true"/> instead.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DiagnosticsSwitchTests
{
#if DEBUG
    private const bool CurrentBuildIsDebug = true;
#else
    private const bool CurrentBuildIsDebug = false;
#endif

    private static readonly Func<string, string?> NoEnvironmentVariables = _ => null;

    // ── Command-line flag ───────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_CommandLineFlagPresent_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled([DiagnosticsSwitch.CommandLineFlag], NoEnvironmentVariables);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_CommandLineFlagPresent_AmongOtherArgs_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled(
            ["DesktopOrganizer.exe", "--minimized", DiagnosticsSwitch.CommandLineFlag],
            NoEnvironmentVariables);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_CommandLineFlagPresent_CaseInsensitive()
    {
        var result = DiagnosticsSwitch.IsEnabled(["--DIAG"], NoEnvironmentVariables);

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_CommandLineFlagAbsent_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled(["--minimized"], NoEnvironmentVariables);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    [Fact]
    public void IsEnabled_EmptyCommandLineArgs_NoEnvVar_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], NoEnvironmentVariables);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    // ── Environment variable ────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_EnvironmentVariableEquals1_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning("1"));

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableEquals0_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning("0"));

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableMissing_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], NoEnvironmentVariables);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableGarbageValue_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning("yes-please"));

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableEmptyString_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning(string.Empty));

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    [Fact]
    public void IsEnabled_UnrelatedEnvironmentVariableSetTo1_MatchesCurrentBuildConfiguration()
    {
        // Only DESKTOPORGANIZER_DIAG matters — a same-valued but differently-named variable must not
        // be picked up by accident.
        var result = DiagnosticsSwitch.IsEnabled([], name => name == "SOME_OTHER_VAR" ? "1" : null);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    // ── Priority between sources ────────────────────────────────────────────

    [Fact]
    public void IsEnabled_BothCommandLineFlagAndEnvironmentVariableSet_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled([DiagnosticsSwitch.CommandLineFlag], EnvironmentReturning("1"));

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_CommandLineFlagSet_EnvironmentVariableEquals0_ReturnsTrue()
    {
        // The flag alone is sufficient regardless of what the env var says.
        var result = DiagnosticsSwitch.IsEnabled([DiagnosticsSwitch.CommandLineFlag], EnvironmentReturning("0"));

        Assert.True(result);
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableEquals1_CommandLineFlagAbsent_ReturnsTrue()
    {
        // The env var alone is sufficient regardless of the command line.
        var result = DiagnosticsSwitch.IsEnabled(["--minimized"], EnvironmentReturning("1"));

        Assert.True(result);
    }

    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_NullCommandLineArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticsSwitch.IsEnabled(null!, NoEnvironmentVariables));
    }

    [Fact]
    public void IsEnabled_NullEnvironmentLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticsSwitch.IsEnabled([], null!));
    }

    private static Func<string, string?> EnvironmentReturning(string value) =>
        name => name == DiagnosticsSwitch.EnvironmentVariableName ? value : null;
}
