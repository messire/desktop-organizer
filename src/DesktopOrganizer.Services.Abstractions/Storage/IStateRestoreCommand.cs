namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Implements the manual recovery path named by ADR-024 §7 rollback scenario C: given a primary
/// application-state document that is corrupt (or simply undesirable), pick the best available
/// backup candidate, validate it, and — only if validation succeeds — atomically make it the new
/// primary, moving the previous primary to quarantine rather than deleting it.
/// </summary>
/// <remarks>
/// <b>Candidate selection.</b> Prefers <c>application-state.json.bak</c> (the most recent
/// previously-committed document, preserved by <see cref="System.IO.File.Replace(string, string, string, bool)"/>
/// as part of the normal write protocol); if no <c>.bak</c> exists, falls back to the newest
/// <c>application-state.corrupt-{yyyyMMdd-HHmmss}.json</c> quarantine file. Exactly one candidate is
/// ever tried per call — there is no ladder of fallbacks within a single <see cref="Restore"/> call
/// beyond that single choice (ADR-024 §7 scenario C).
/// <para>
/// <b>No destructive action without validation.</b> The candidate is parsed and run through the full
/// migration chain up to the current schema version before anything on disk changes. If validation
/// fails for any reason, <see cref="Restore"/> returns <see cref="StateRestoreOutcome.CandidateInvalid"/>
/// and leaves every file under the storage directory untouched — including the corrupt primary.
/// </para>
/// <para>
/// <b>Standalone, not part of the normal store lifecycle.</b> This command does not go through
/// <see cref="IApplicationStateStore"/>'s write barrier or no-op guard (ADR-024 D-11) — it is meant
/// to be invoked once, from <c>Program.Main</c>, in response to the <c>--restore-state-backup</c>
/// command-line switch, strictly before <see cref="IApplicationStateStore.Load"/> is ever called in
/// that process (a subsequent normal run then loads whatever this command produced).
/// </para>
/// </remarks>
public interface IStateRestoreCommand
{
    /// <summary>
    /// Attempts the recovery described in the type remarks. Never throws — every failure mode is
    /// represented by <see cref="StateRestoreResult.Outcome"/> instead of an exception.
    /// </summary>
    StateRestoreResult Restore();
}
