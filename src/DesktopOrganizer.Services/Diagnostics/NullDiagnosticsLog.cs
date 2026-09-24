using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// No-op <see cref="IDiagnosticsLog"/> used whenever diagnostics logging is disabled (the default
/// in Release builds — see <see cref="DiagnosticsSwitch"/>). Never touches the file system, never
/// allocates a queue or a background thread: this is the zero-cost path production users run.
/// </summary>
public sealed class NullDiagnosticsLog : IDiagnosticsLog
{
    /// <summary>
    /// The single shared instance. This type carries no state, so there's no reason for more than
    /// one to ever exist.
    /// </summary>
    public static readonly NullDiagnosticsLog Instance = new();

    private NullDiagnosticsLog()
    {
    }

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public void Write(string category, string name, string? scope, params KeyValuePair<string, object?>[] payload)
    {
        // Intentional no-op: diagnostics logging is disabled.
    }

    /// <inheritdoc />
    public void Flush()
    {
        // Intentional no-op: nothing is ever buffered.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Intentional no-op: nothing to release.
    }
}
