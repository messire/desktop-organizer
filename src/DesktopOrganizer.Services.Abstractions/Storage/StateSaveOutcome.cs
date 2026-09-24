namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Return value of <see cref="IApplicationStateStore.Save"/>: the coarse-grained
/// <see cref="StateSaveResult"/>, plus — when it is <see cref="StateSaveResult.Failed"/> — exactly
/// which stage of the atomic write protocol (ADR-024 D-6) the failure occurred at.
/// </summary>
/// <remarks>
/// <see cref="StateSaveResult"/> is a plain enum and cannot itself carry this second piece of data,
/// hence this wrapper record. <see cref="FailureStage"/> is deliberately typed as the same
/// <see cref="StateWriteStage"/> vocabulary that implementations use for their internal
/// crash-injection test seam, rather than a second, parallel "public" stage enum — see the remarks
/// on <see cref="StateWriteStage"/> for why keeping one source of truth matters here.
/// </remarks>
/// <param name="Result">The coarse-grained outcome.</param>
/// <param name="FailureStage">
/// The stage the write protocol had reached when it failed. <see langword="null"/> for every
/// <see cref="StateSaveResult"/> other than <see cref="StateSaveResult.Failed"/>; always non-null
/// when <paramref name="Result"/> is <see cref="StateSaveResult.Failed"/>.
/// </param>
public sealed record StateSaveOutcome(StateSaveResult Result, StateWriteStage? FailureStage);
