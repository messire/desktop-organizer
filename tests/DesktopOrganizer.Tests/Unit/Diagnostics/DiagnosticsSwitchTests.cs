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

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> returns <see langword="true"/> when the
    /// command-line flag is the only argument present.
    /// </summary>
    [Fact]
    public void IsEnabled_CommandLineFlagPresent_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled([DiagnosticsSwitch.CommandLineFlag], NoEnvironmentVariables);

        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> returns <see langword="true"/> when the
    /// command-line flag is present among other, unrelated arguments.
    /// </summary>
    [Fact]
    public void IsEnabled_CommandLineFlagPresent_AmongOtherArgs_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled(
            ["DesktopOrganizer.exe", "--minimized", DiagnosticsSwitch.CommandLineFlag],
            NoEnvironmentVariables);

        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> matches the command-line flag
    /// case-insensitively.
    /// </summary>
    [Fact]
    public void IsEnabled_CommandLineFlagPresent_CaseInsensitive()
    {
        var result = DiagnosticsSwitch.IsEnabled(["--DIAG"], NoEnvironmentVariables);

        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> falls back to the current build's
    /// compile-time default (<see cref="CurrentBuildIsDebug"/>) when the command-line flag is absent
    /// and no environment variable is set.
    /// </summary>
    [Fact]
    public void IsEnabled_CommandLineFlagAbsent_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled(["--minimized"], NoEnvironmentVariables);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> falls back to
    /// <see cref="CurrentBuildIsDebug"/> when both the command-line args and environment lookup are
    /// empty/absent.
    /// </summary>
    [Fact]
    public void IsEnabled_EmptyCommandLineArgs_NoEnvVar_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], NoEnvironmentVariables);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    // ── Environment variable ────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> returns <see langword="true"/> when the
    /// environment variable is set to <c>"1"</c>.
    /// </summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableEquals1_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning("1"));

        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> falls back to
    /// <see cref="CurrentBuildIsDebug"/> when the environment variable is set to <c>"0"</c>.
    /// </summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableEquals0_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning("0"));

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> falls back to
    /// <see cref="CurrentBuildIsDebug"/> when the environment lookup never returns a value.
    /// </summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableMissing_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], NoEnvironmentVariables);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> falls back to
    /// <see cref="CurrentBuildIsDebug"/> when the environment variable holds an unrecognized value.
    /// </summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableGarbageValue_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning("yes-please"));

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> falls back to
    /// <see cref="CurrentBuildIsDebug"/> when the environment variable is set to an empty string.
    /// </summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableEmptyString_MatchesCurrentBuildConfiguration()
    {
        var result = DiagnosticsSwitch.IsEnabled([], EnvironmentReturning(string.Empty));

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> ignores a differently-named environment
    /// variable set to <c>"1"</c> and falls back to <see cref="CurrentBuildIsDebug"/>.
    /// </summary>
    [Fact]
    public void IsEnabled_UnrelatedEnvironmentVariableSetTo1_MatchesCurrentBuildConfiguration()
    {
        // Only DESKTOPORGANIZER_DIAG matters — a same-valued but differently-named variable must not
        // be picked up by accident.
        var result = DiagnosticsSwitch.IsEnabled([], name => name == "SOME_OTHER_VAR" ? "1" : null);

        Assert.Equal(CurrentBuildIsDebug, result);
    }

    // ── Priority between sources ────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> returns <see langword="true"/> when both
    /// the command-line flag and the environment variable are set.
    /// </summary>
    [Fact]
    public void IsEnabled_BothCommandLineFlagAndEnvironmentVariableSet_ReturnsTrue()
    {
        var result = DiagnosticsSwitch.IsEnabled([DiagnosticsSwitch.CommandLineFlag], EnvironmentReturning("1"));

        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> returns <see langword="true"/> when the
    /// command-line flag is set, regardless of an environment variable set to <c>"0"</c>.
    /// </summary>
    [Fact]
    public void IsEnabled_CommandLineFlagSet_EnvironmentVariableEquals0_ReturnsTrue()
    {
        // The flag alone is sufficient regardless of what the env var says.
        var result = DiagnosticsSwitch.IsEnabled([DiagnosticsSwitch.CommandLineFlag], EnvironmentReturning("0"));

        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> returns <see langword="true"/> when the
    /// environment variable is set to <c>"1"</c>, regardless of the command-line flag being absent.
    /// </summary>
    [Fact]
    public void IsEnabled_EnvironmentVariableEquals1_CommandLineFlagAbsent_ReturnsTrue()
    {
        // The env var alone is sufficient regardless of the command line.
        var result = DiagnosticsSwitch.IsEnabled(["--minimized"], EnvironmentReturning("1"));

        Assert.True(result);
    }

    // ── Argument validation ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> throws <see cref="ArgumentNullException"/>
    /// when the command-line args list is <see langword="null"/>.
    /// </summary>
    [Fact]
    public void IsEnabled_NullCommandLineArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticsSwitch.IsEnabled(null!, NoEnvironmentVariables));
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsSwitch.IsEnabled"/> throws <see cref="ArgumentNullException"/>
    /// when the environment lookup delegate is <see langword="null"/>.
    /// </summary>
    [Fact]
    public void IsEnabled_NullEnvironmentLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticsSwitch.IsEnabled([], null!));
    }

    // ── Resolve (ADR-033 part 1: DiagnosticsLevel) ──────────────────────────

    /// <summary>
    /// <see cref="DiagnosticsSwitch.Resolve"/> matches the current build's compile-time default
    /// (<see cref="CurrentBuildIsDebug"/>) when neither the command-line flag nor the environment
    /// variable is present — <see cref="DiagnosticsLevel.Verbose"/> in a Debug build (this test project's
    /// own configuration), <see cref="DiagnosticsLevel.Standard"/> in Release.
    /// </summary>
    [Fact]
    public void Resolve_NoFlagsPresent_MatchesCurrentBuildConfiguration()
    {
        var expected = CurrentBuildIsDebug ? DiagnosticsLevel.Verbose : DiagnosticsLevel.Standard;

        var result = DiagnosticsSwitch.Resolve([], NoEnvironmentVariables);

        Assert.Equal(expected, result);
    }

    /// <summary><see cref="DiagnosticsSwitch.Resolve"/> resolves to <see cref="DiagnosticsLevel.Verbose"/>
    /// when the command-line flag is present, regardless of build configuration.</summary>
    [Fact]
    public void Resolve_CommandLineFlagPresent_ReturnsVerbose()
    {
        var result = DiagnosticsSwitch.Resolve([DiagnosticsSwitch.CommandLineFlag], NoEnvironmentVariables);

        Assert.Equal(DiagnosticsLevel.Verbose, result);
    }

    /// <summary><see cref="DiagnosticsSwitch.Resolve"/> resolves to <see cref="DiagnosticsLevel.Verbose"/>
    /// when the environment variable is set to <c>"1"</c>, regardless of build configuration.</summary>
    [Fact]
    public void Resolve_EnvironmentVariableEquals1_ReturnsVerbose()
    {
        var result = DiagnosticsSwitch.Resolve([], EnvironmentReturning("1"));

        Assert.Equal(DiagnosticsLevel.Verbose, result);
    }

    /// <summary>
    /// <see cref="DiagnosticsSwitch.IsEnabled"/> stays consistent with <see cref="DiagnosticsSwitch.Resolve"/>:
    /// it is exactly the "resolves to Verbose" boolean view existing callers (gesture-diagnostics
    /// registration) keep using.
    /// </summary>
    /// <param name="commandLineFlagPresent">Whether <see cref="DiagnosticsSwitch.CommandLineFlag"/> is included in the args passed to both methods.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsEnabled_AlwaysMatchesResolveEqualsVerbose(bool commandLineFlagPresent)
    {
        string[] args = commandLineFlagPresent ? [DiagnosticsSwitch.CommandLineFlag] : [];

        var level = DiagnosticsSwitch.Resolve(args, NoEnvironmentVariables);
        var isEnabled = DiagnosticsSwitch.IsEnabled(args, NoEnvironmentVariables);

        Assert.Equal(level == DiagnosticsLevel.Verbose, isEnabled);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static Func<string, string?> EnvironmentReturning(string value) =>
        name => name == DiagnosticsSwitch.EnvironmentVariableName ? value : null;
}
