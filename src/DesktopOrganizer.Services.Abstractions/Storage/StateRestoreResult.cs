namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Result of a single <see cref="IStateRestoreCommand.Restore"/> call.
/// </summary>
/// <param name="Outcome">The coarse-grained outcome.</param>
/// <param name="CandidatePath">
/// The path of the candidate file the command attempted to restore from (a <c>.bak</c> file or a
/// quarantine file), or <see langword="null"/> when <paramref name="Outcome"/> is
/// <see cref="StateRestoreOutcome.NoCandidateFound"/>.
/// </param>
/// <param name="QuarantinedPrimaryPath">
/// The path the previous primary file (if one existed) was moved to, when
/// <paramref name="Outcome"/> is <see cref="StateRestoreOutcome.Restored"/>. <see langword="null"/>
/// when no primary existed to quarantine, or for every other outcome.
/// </param>
/// <param name="Message">
/// A short, human-readable, English, non-localized diagnostic message describing the outcome — safe
/// to print to a console or a message box. Never contains a full file path with a user name embedded
/// beyond what the caller already knows (the storage directory itself), mirroring the privacy
/// discipline of <c>StateSaveFailed</c> (ADR-024 §4).
/// </param>
public sealed record StateRestoreResult(
    StateRestoreOutcome Outcome,
    string? CandidatePath,
    string? QuarantinedPrimaryPath,
    string Message);
