using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// A single, cheap existence/directory-ness probe (ADR-046 D-11): one <c>File.GetAttributes</c> call
/// instead of the 2-3 <c>File.Exists</c>/<c>Directory.Exists</c> round-trips the pre-ADR-046 code made per
/// item, with a remote-root short-circuit through <see cref="IRemoteRootCircuitBreaker"/> so a probe never
/// pays for its own SMB timeout once the root is already known to be unavailable.
/// </summary>
/// <param name="classifier">Classifies a path's own root (never touches the network itself).</param>
/// <param name="breaker">Shared circuit breaker — the same instance <see cref="StaIconLoadQueue"/>'s Remote lane uses.</param>
internal sealed class PathProbe(IRemotePathClassifier classifier, IRemoteRootCircuitBreaker breaker) : IPathProbe
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Review fix (7c): a probe against a network root that ISN'T (yet) open must still register itself
    /// with the breaker via <see cref="IRemoteRootCircuitBreaker.BeginCall"/> — not just consult
    /// <see cref="IRemoteRootCircuitBreaker.IsOpen"/> — so a slow probe (this method is also what
    /// <c>BackgroundItemProbeQueue</c>'s liveness ticks call, entirely independent of
    /// <see cref="StaIconLoadQueue"/>'s own Remote lane) actually counts toward the breaker's in-flight
    /// slow-call tracking (D-3) and can open the root on its own.
    /// </para>
    /// <para>
    /// Correction (review it.2, M-4): <c>StaIconLoadQueue.ExecuteIconFetch</c> on the Remote lane does
    /// <b>not</b> call this overload — it always already holds its own <see cref="IRemoteRootCircuitBreaker.BeginCall"/>
    /// scope for the job's root (possibly as the half-open trial itself), and calling <see cref="Probe"/>
    /// again from inside it would see that very own in-flight call via <see cref="IRemoteRootCircuitBreaker.IsOpen"/>
    /// and wrongly short-circuit to <c>Exists = false</c> even when the share just came back. It calls
    /// <see cref="ProbeWithinCall"/> instead, which skips the breaker entirely. This overload
    /// (<see cref="Probe"/>) remains correct — and is what registers a NEW in-flight call, exactly as
    /// documented above — for every OTHER caller that does not already hold one, chiefly
    /// <c>BackgroundItemProbeQueue</c>'s liveness ticks.
    /// </para>
    /// </remarks>
    public PathProbeResult Probe(string path)
    {
        var root = classifier.GetOwnRemoteRoot(path);
        if (root is null) return ProbeCore(path);

        if (breaker.IsOpen(root))
        {
            return new PathProbeResult(path, Exists: false, IsDirectory: false, IsKnown: false);
        }

        using var call = breaker.BeginCall(root);
        return ProbeCore(path);
    }

    /// <inheritdoc />
    public PathProbeResult ProbeWithinCall(string path) => ProbeCore(path);

    private static PathProbeResult ProbeCore(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            return new PathProbeResult(path, Exists: true, isDirectory, IsKnown: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Legacy File.Exists semantics (D-11): missing, access-denied, or any other failure all
            // degrade to "does not exist" — this IS a real observation (IsKnown stays true), unlike the
            // remote-root short-circuit above which never actually asked the file system anything.
            return new PathProbeResult(path, Exists: false, IsDirectory: false, IsKnown: true);
        }
    }
}
