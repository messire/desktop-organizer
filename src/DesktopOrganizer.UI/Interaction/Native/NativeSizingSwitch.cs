namespace DesktopOrganizer.UI.Interaction.Native;

/// <summary>
/// Decides whether the native (<c>WM_SIZING</c>-loop) panel resize path should be used instead of the
/// legacy WPF <c>Thumb</c> path. Pure and fully unit-testable, an exact-pattern clone of
/// <c>DesktopOrganizer.Services.Diagnostics.DiagnosticsSwitch</c> — takes command-line arguments, an
/// environment-variable lookup, and the persisted setting value as explicit inputs rather than reading
/// <see cref="Environment"/>/<c>IAppSettings</c> itself (docs/PLANS/phase-2-native-panel-sizing.md §1.4).
/// </summary>
public static class NativeSizingSwitch
{
    /// <summary>
    /// Command-line flag that forces the native path on, e.g. <c>DesktopOrganizer.exe --native-sizing</c>.
    /// </summary>
    public const string EnableCommandLineFlag = "--native-sizing";

    /// <summary>
    /// Command-line flag that forces the native path off, e.g. <c>DesktopOrganizer.exe --no-native-sizing</c>.
    /// </summary>
    public const string DisableCommandLineFlag = "--no-native-sizing";

    /// <summary>
    /// Environment variable that forces the native path on (<c>"1"</c>) or off (<c>"0"</c>), overriding
    /// both the command line and <c>settingValue</c>.
    /// </summary>
    public const string EnvironmentVariableName = "DESKTOPORGANIZER_NATIVESIZING";

    /// <summary>
    /// Returns <see langword="true"/> if the native panel-sizing path should be used. Priority order:
    /// <see cref="EnvironmentVariableName"/> (<c>"1"</c>/<c>"0"</c>, any other value is ignored) overrides
    /// everything; otherwise <see cref="EnableCommandLineFlag"/>/<see cref="DisableCommandLineFlag"/>
    /// override <paramref name="settingValue"/> (if both flags are present, disable wins — the safer
    /// direction to fail towards); otherwise <paramref name="settingValue"/> (the persisted
    /// <c>IAppSettings.NativePanelSizing</c> value, default <see langword="false"/>) is used as-is.
    /// </summary>
    /// <param name="commandLineArgs">The process's command-line arguments.</param>
    /// <param name="environment">
    /// A lookup for environment variables, e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// Injected so this method stays pure/testable.
    /// </param>
    /// <param name="settingValue">The persisted setting's current value, used when neither the
    /// environment variable nor a command-line flag decides the outcome.</param>
    public static bool IsEnabled(IReadOnlyList<string> commandLineArgs, Func<string, string?> environment, bool settingValue)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        ArgumentNullException.ThrowIfNull(environment);

        var environmentValue = environment(EnvironmentVariableName);
        if (string.Equals(environmentValue, "1", StringComparison.Ordinal))
            return true;
        if (string.Equals(environmentValue, "0", StringComparison.Ordinal))
            return false;

        var hasDisableFlag = commandLineArgs.Any(arg =>
            string.Equals(arg, DisableCommandLineFlag, StringComparison.OrdinalIgnoreCase));
        if (hasDisableFlag)
            return false;

        var hasEnableFlag = commandLineArgs.Any(arg =>
            string.Equals(arg, EnableCommandLineFlag, StringComparison.OrdinalIgnoreCase));
        if (hasEnableFlag)
            return true;

        return settingValue;
    }
}
