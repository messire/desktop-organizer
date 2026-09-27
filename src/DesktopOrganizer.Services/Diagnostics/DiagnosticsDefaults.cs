namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Compile-time defaults for the always-on application log (ADR-033 part 1, plan Q3). Intentionally not
/// a runtime setting — CLAUDE.md's persistence freeze forbids adding a new <c>IAppSettings</c> switch for
/// this, and the whole point of an always-on error/crash trail is that it cannot be silently turned off
/// by a corrupted, tampered, or simply out-of-date settings file.
/// </summary>
public static class DiagnosticsDefaults
{
    /// <summary>
    /// Whether <see cref="Abstractions.IAppLog"/> writes to the JSONL sink at all. <see langword="true"/>
    /// in every build configuration today — disabling it requires recompiling with this flipped, which is
    /// deliberate (see type remarks).
    /// </summary>
    public const bool FileLoggingEnabled = true;
}
