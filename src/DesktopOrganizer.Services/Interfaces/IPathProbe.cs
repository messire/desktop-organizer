using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// A single, cheap existence/directory-ness probe (ADR-046 D-11) — one metadata call instead of the 2-3
/// <c>File.Exists</c>/<c>Directory.Exists</c> round-trips the pre-ADR-046 code made per item, with the
/// same "any failure means missing" semantics as <c>File.Exists</c>.
/// </summary>
internal interface IPathProbe
{
    /// <summary>
    /// Probes <paramref name="path"/>. Its own remote root being open in the circuit breaker short-circuits
    /// to <c>(Exists: false, IsKnown: false)</c> without any I/O. Never throws — any other failure degrades
    /// to <c>Exists = false</c>, matching legacy <c>File.Exists</c> semantics.
    /// </summary>
    /// <param name="path">The path to probe.</param>
    /// <returns>The probe result.</returns>
    PathProbeResult Probe(string path);

    /// <summary>
    /// Same probe as <see cref="Probe"/>, for a caller that ALREADY holds an
    /// <see cref="IRemoteRootCircuitBreaker.BeginCall"/> scope for <paramref name="path"/>'s own remote
    /// root (ADR-046 review M-4 — <c>StaIconLoadQueue.ExecuteIconFetch</c> on the Remote lane, which wraps
    /// the whole job in its own <c>BeginCall</c>). Skips the breaker's <see cref="IRemoteRootCircuitBreaker.IsOpen"/>/
    /// <see cref="IRemoteRootCircuitBreaker.BeginCall"/> entirely instead of calling <see cref="Probe"/>
    /// again: re-entering them here would see the caller's OWN in-flight call — possibly the half-open
    /// trial itself — and short-circuit to <c>Exists = false</c> for a probe that the breaker isn't
    /// actually blocking. Never touches the network beyond the one metadata call; same "any failure means
    /// missing" semantics as <see cref="Probe"/>.
    /// </summary>
    /// <param name="path">The path to probe.</param>
    /// <returns>The probe result.</returns>
    PathProbeResult ProbeWithinCall(string path);
}
