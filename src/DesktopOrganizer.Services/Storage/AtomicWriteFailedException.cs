using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Thrown by <see cref="AtomicJsonFileWriter.Write"/> when the atomic write protocol (ADR-024 D-6)
/// does not complete successfully — either a real I/O failure, or (in tests) an injected failure via
/// the <c>testForceFailureAt</c> constructor seam. Carries the exact <see cref="StateWriteStage"/>
/// reached, which <see cref="JsonApplicationStateStore.Save"/> reports back to callers as
/// <see cref="StateSaveOutcome.FailureStage"/> and never lets move the store's committed state (see
/// remarks on <see cref="IApplicationStateStore.Save"/>).
/// </summary>
internal sealed class AtomicWriteFailedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AtomicWriteFailedException"/> class.
    /// </summary>
    /// <param name="stage">The write protocol stage reached when the failure occurred.</param>
    /// <param name="message">A message describing the failure.</param>
    /// <param name="innerException">The exception that caused the failure, if any.</param>
    public AtomicWriteFailedException(StateWriteStage stage, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    /// <summary>The stage of the write protocol reached when the failure occurred.</summary>
    public StateWriteStage Stage { get; }
}
