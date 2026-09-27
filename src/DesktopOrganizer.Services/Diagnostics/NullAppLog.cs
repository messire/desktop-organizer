using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// No-op <see cref="IAppLog"/> — the default <see cref="AppLog.Current"/> resolves to before
/// <see cref="AppLog.Initialize"/> ever runs (e.g. static field initializers, or any code that happens to
/// execute before <c>Program.Main</c> wires up the real one). Never touches the file system.
/// </summary>
public sealed class NullAppLog : IAppLog
{
    /// <summary>The single shared instance. This type carries no state.</summary>
    public static readonly NullAppLog Instance = new();

    private NullAppLog()
    {
    }

    /// <inheritdoc />
    public void Info(string source, string evt, params KeyValuePair<string, object?>[] data)
    {
        // Intentional no-op: no real IAppLog has been wired up yet.
    }

    /// <inheritdoc />
    public void Warn(string source, string evt, params KeyValuePair<string, object?>[] data)
    {
        // Intentional no-op: no real IAppLog has been wired up yet.
    }

    /// <inheritdoc />
    public void Error(string source, string evt, Exception? ex, params KeyValuePair<string, object?>[] data)
    {
        // Intentional no-op: no real IAppLog has been wired up yet.
    }

    /// <inheritdoc />
    public void Flush()
    {
        // Intentional no-op: nothing is buffered.
    }
}
