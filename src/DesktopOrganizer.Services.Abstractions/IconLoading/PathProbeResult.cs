namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Result of a single, cheap existence/directory-ness probe for one path (<see cref="IItemProbeQueue"/>,
/// ADR-046) — one metadata call instead of the 2-3 <c>File.Exists</c>/<c>Directory.Exists</c> round-trips
/// the pre-ADR-046 liveness tick made per item.
/// </summary>
/// <param name="Path">The probed path, as passed in.</param>
/// <param name="Exists">Whether the path existed at probe time. <see langword="false"/> whenever <paramref name="IsKnown"/> is <see langword="false"/> (legacy <c>File.Exists</c> semantics: any failure degrades to "does not exist").</param>
/// <param name="IsDirectory">Whether the path is a directory. Only meaningful when <paramref name="Exists"/> is <see langword="true"/>.</param>
/// <param name="IsKnown">
/// <see langword="false"/> when the probe was short-circuited (its remote root is open in the circuit
/// breaker) or otherwise failed to actually query the file system — <see cref="Exists"/> is then a
/// conservative <see langword="false"/>, not a real observation.
/// </param>
public readonly record struct PathProbeResult(string Path, bool Exists, bool IsDirectory, bool IsKnown);
