namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// How an <see cref="IconLoadRequest"/> was resolved by <see cref="IIconLoadQueue"/> (ADR-046).
/// </summary>
public enum IconLoadOutcome
{
    /// <summary>
    /// The final icon, resolved through <see cref="IIconProvider.GetIcon(string, int)"/>. May itself be
    /// the provider's own placeholder (check <see cref="IIconProvider.IsPlaceholder"/>) if every real
    /// fallback stage failed — this outcome only promises "no further result is coming for this
    /// request", not "this is a real icon".
    /// </summary>
    Loaded,

    /// <summary>
    /// A provisional type icon (stage 3, resolved purely by extension/attribute — see
    /// <see cref="IIconProvider.GetTypeIcon"/>), delivered while the real icon is still being resolved.
    /// A <see cref="Loaded"/> (or another terminal outcome) result for the same request always follows.
    /// </summary>
    Interim,

    /// <summary>
    /// Not fetched: the item's remote root is open (circuit breaker), the watchdog timed the job out, the
    /// item's path is quarantined, or its lane was disabled after too many abandoned workers.
    /// <see cref="IconLoadResult.Icon"/> is <see langword="null"/>.
    /// </summary>
    Unavailable,

    /// <summary>
    /// An unexpected worker failure — an exception escaped the job, or the resolved icon was not frozen
    /// (C-4). <see cref="IconLoadResult.Icon"/> is <see langword="null"/>.
    /// </summary>
    Failed,

    /// <summary>
    /// The job was evicted by back-pressure before it ever ran. <see cref="IconLoadResult.Icon"/> is
    /// <see langword="null"/>.
    /// </summary>
    Dropped,
}
