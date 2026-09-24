namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Decides whether structured diagnostics logging should be enabled for this process. Pure and
/// fully unit-testable: takes the command-line arguments and an environment-variable lookup as
/// explicit inputs rather than reading <see cref="Environment"/> itself.
/// </summary>
public static class DiagnosticsSwitch
{
    /// <summary>
    /// Command-line flag that enables diagnostics logging, e.g. <c>DesktopOrganizer.exe --diag</c>.
    /// </summary>
    public const string CommandLineFlag = "--diag";

    /// <summary>
    /// Environment variable that enables diagnostics logging when set to <c>"1"</c>.
    /// </summary>
    public const string EnvironmentVariableName = "DESKTOPORGANIZER_DIAG";

#if DEBUG
    private static readonly bool IsDebugBuild = true;
#else
    private static readonly bool IsDebugBuild = false;
#endif

    /// <summary>
    /// Returns <see langword="true"/> if diagnostics logging should be enabled: always in Debug
    /// builds, and in Release builds only when <see cref="CommandLineFlag"/> is present in
    /// <paramref name="commandLineArgs"/> or <see cref="EnvironmentVariableName"/> is set to
    /// <c>"1"</c>.
    /// </summary>
    /// <param name="commandLineArgs">The process's command-line arguments.</param>
    /// <param name="environment">
    /// A lookup for environment variables, e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// Injected so this method stays pure/testable.
    /// </param>
    public static bool IsEnabled(IReadOnlyList<string> commandLineArgs, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        ArgumentNullException.ThrowIfNull(environment);

        var hasCommandLineFlag = commandLineArgs.Any(arg =>
            string.Equals(arg, CommandLineFlag, StringComparison.OrdinalIgnoreCase));
        var hasEnvironmentFlag = string.Equals(environment(EnvironmentVariableName), "1", StringComparison.Ordinal);

        return IsDebugBuild || hasCommandLineFlag || hasEnvironmentFlag;
    }
}
