namespace DesktopOrganizer.UI.Services;

/// <summary>
/// A process-wide, in-memory "show once per session" latch for the cloud-folder drop hint
/// (ADR-009 amendment 2026-10-10). Deliberately NOT persisted (<see cref="IAppSettings"/> is untouched):
/// the hint repeats once per app run, so a user who keeps dragging OneDrive files in is reminded again
/// after a restart but never nagged within a session.
/// </summary>
public interface ICloudDropHintGate
{
    /// <summary>
    /// Claims the single per-session slot. Thread-safe.
    /// </summary>
    /// <returns><see langword="true"/> for the first caller only; <see langword="false"/> ever after.</returns>
    bool TryClaim();
}

/// <inheritdoc />
public sealed class CloudDropHintGate : ICloudDropHintGate
{
    private int _claimed;

    /// <inheritdoc />
    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
}
