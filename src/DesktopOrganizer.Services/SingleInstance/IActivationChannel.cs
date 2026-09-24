using DesktopOrganizer.Services.Abstractions;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Why <see cref="IActivationServer.TryStart"/> could not start the activation server. Reported in the
/// <c>ActivationServerUnavailable</c> diagnostics event (phase-3 plan §4) — a closed enum, so its names
/// are safe to write to the log verbatim (privacy invariant, see <c>DiagnosticEvent</c>).
/// </summary>
internal enum ActivationServerUnavailableReason
{
    /// <summary>
    /// Another process already owns the single (<c>maxNumberOfServerInstances: 1</c>) pipe instance —
    /// the machine-global tie-breaker firing, typically Fast User Switching (ADR-023 D-4/R-8).
    /// </summary>
    PipeBusy,

    /// <summary>
    /// The pipe's ACL could not be built or applied (no SID for the current identity, or the security
    /// descriptor was rejected). The server deliberately does not fall back to an unsecured pipe
    /// (ADR-023 R-14).
    /// </summary>
    AclFailed,

    /// <summary>Anything else — an unclassified failure creating the server stream.</summary>
    Faulted,
}

/// <summary>
/// Server side of the single-instance activation channel (ADR-023 D-4/D-5): a background accept loop
/// listening for <see cref="ActivationRequest"/>s from secondary processes. Only the current primary
/// runs one of these.
/// </summary>
internal interface IActivationServer
{
    /// <summary>
    /// Why the most recent <see cref="TryStart"/> returned <see langword="false"/>, or
    /// <see langword="null"/> if it has never failed. Purely informational — the caller's control flow
    /// is driven by <see cref="TryStart"/>'s return value, not by this (ADR-023 D-4 treats every
    /// failure the same way); it exists so the failure can be named in the diagnostics log.
    /// </summary>
    ActivationServerUnavailableReason? LastStartFailure { get; }

    /// <summary>
    /// Starts the background accept loop. Idempotent — calling it again while already started is a
    /// no-op that returns <see langword="true"/>.
    /// </summary>
    /// <param name="onRequest">
    /// Invoked once per successfully decoded request. Must do the absolute minimum amount of work
    /// (the same discipline as the <c>WH_MOUSE_LL</c> hook callback) — in practice, this is always
    /// <c>relay.Publish</c>, never application logic run directly on the pipe thread.
    /// </param>
    /// <param name="statusProvider">
    /// Queried once per incoming connection, before the request is read, to decide whether to answer
    /// with <see cref="ActivationResponseCode.Ok"/> or <see cref="ActivationResponseCode.ShuttingDown"/>
    /// (ADR-023 D-9: the primary flips this during <c>EnterShutdown()</c>).
    /// </param>
    /// <returns>
    /// <see langword="false"/> if the server could not be started — either because another process's
    /// server already owns the single pipe instance (<c>ERROR_PIPE_BUSY</c>, ADR-023 D-4: the
    /// machine-global tie-breaker) or because the pipe's ACL could not be created (ADR-023 R-14: never
    /// degrade silently to an unsecured pipe). The caller must treat both as "I am not the primary
    /// after all" / "activation is unavailable", per ADR-023 D-4.
    /// </returns>
    bool TryStart(Action<ActivationRequest> onRequest, Func<ActivationResponseCode> statusProvider);

    /// <summary>
    /// Stops the accept loop. Bounded (≤2s) and idempotent — safe to call multiple times, including
    /// when the server was never started.
    /// </summary>
    void Stop();
}

/// <summary>
/// Client side of the single-instance activation channel (ADR-023 D-4/D-5): sends a single
/// <see cref="ActivationRequest"/> to whichever process currently owns the pipe server, if any.
/// </summary>
internal interface IActivationClient
{
    /// <summary>
    /// Connects, sends <paramref name="request"/>, and waits for a response — all bounded by
    /// <paramref name="connectTimeout"/>. Never blocks indefinitely and never throws.
    /// </summary>
    ActivationSendResult TrySend(ActivationRequest request, TimeSpan connectTimeout);
}
