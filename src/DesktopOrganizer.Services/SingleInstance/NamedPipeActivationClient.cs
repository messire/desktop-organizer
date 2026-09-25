using System.IO;
using System.IO.Pipes;
using System.Text;
using DesktopOrganizer.Services.Abstractions;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// <see cref="NamedPipeClientStream"/>-backed <see cref="IActivationClient"/> (ADR-023 D-4/D-5).
/// Bounded by the caller's <c>connectTimeout</c> twice over (once for connecting, once more for the
/// write+read round trip) and never throws — every failure mode maps to an
/// <see cref="ActivationSendResult"/> instead.
/// </summary>
/// <param name="pipeName">Machine-global activation pipe name (see <see cref="SingleInstanceNaming"/>).</param>
/// <param name="nativeApi">
/// Used for exactly one thing: the <c>AllowSetForegroundWindow</c> foreground handoff below
/// (ADR-023 D-8/R-13).
/// </param>
internal sealed class NamedPipeActivationClient(string pipeName, INativeApi nativeApi) : IActivationClient
{
    /// <summary>
    /// <c>ASFW_ANY</c> — "any process may take the foreground". Declared here rather than on
    /// <see cref="INativeApi"/> for the same reason <c>DesktopIconsService</c> declares its own
    /// <c>SW_HIDE</c>/<c>SW_SHOW</c>: the native layer stays a thin, constant-free P/Invoke surface and
    /// each caller names only the flags it actually uses.
    /// </summary>
    /// <remarks>
    /// A secondary process only ever knows the primary by pipe name, never by pid — there is no handle
    /// to query the owner of a named pipe's server end without additional privileges — so the
    /// pid-specific form of the call is not available here. The grant is narrow in time rather than in
    /// target: it lasts only until the next foreground change, and the primary consumes it within
    /// milliseconds via <c>IWindowManager.ShowMainWindow()</c>.
    /// </remarks>
    private const int AllowSetForegroundWindowAny = -1;

    /// <inheritdoc />
    public ActivationSendResult TrySend(ActivationRequest request, TimeSpan connectTimeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // ADR-023 D-8/R-13: must happen on the *client* side, before the primary is asked to show
            // its window. Windows' foreground lock otherwise reduces the primary's Activate() to a
            // flashing taskbar button. Best-effort by contract — INativeApi.AllowSetForegroundWindow
            // never throws and returns false when this process holds no foreground right to give away
            // (e.g. launched by the scheduler rather than by a user click), in which case the
            // activation still gets delivered; only the focus transfer degrades.
            nativeApi.AllowSetForegroundWindow(AllowSetForegroundWindowAny);

            try
            {
                client.Connect((int)connectTimeout.TotalMilliseconds);
            }
            catch (TimeoutException)
            {
                return ActivationSendResult.NoListener;
            }
            catch (IOException)
            {
                // E.g. ERROR_PIPE_BUSY (the single server instance is mid-handling another client) —
                // from this caller's point of view that is indistinguishable from "no listener right
                // now"; the handshake policy's retry loop (ADR-023 D-6) is what covers this case.
                return ActivationSendResult.NoListener;
            }

            using var budget = new CancellationTokenSource(connectTimeout);

            try
            {
                var requestBytes = Encoding.UTF8.GetBytes(EncodeRequest(request));
                client.WriteAsync(requestBytes, budget.Token).AsTask().GetAwaiter().GetResult();
                client.FlushAsync(budget.Token).GetAwaiter().GetResult();

                if (!TryReadLine(client, budget.Token, out var line))
                {
                    // Server disconnected before a full response line arrived.
                    return ActivationSendResult.Faulted;
                }

                if (!TryDecodeResponse(line, out var code))
                {
                    return ActivationSendResult.ProtocolError;
                }

                return code switch
                {
                    ActivationResponseCode.Ok => ActivationSendResult.Delivered,
                    ActivationResponseCode.ShuttingDown => ActivationSendResult.PrimaryShuttingDown,
                    ActivationResponseCode.OtherSession => ActivationSendResult.OtherSession,
                    ActivationResponseCode.Unsupported => ActivationSendResult.ProtocolError,
                    _ => ActivationSendResult.ProtocolError,
                };
            }
            catch (OperationCanceledException)
            {
                return ActivationSendResult.Timeout;
            }
        }
        catch (Exception)
        {
            // Any other failure (pipe torn down mid-write, access denied, ...) — never let TrySend
            // throw, the handshake loop treats this the same as a failed attempt.
            return ActivationSendResult.Faulted;
        }
    }

    private static bool TryReadLine(NamedPipeClientStream client, CancellationToken ct, out string line)
    {
        var buffer = new byte[MaxMessageBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = client.ReadAsync(buffer.AsMemory(length, 1), ct).AsTask().GetAwaiter().GetResult();
            if (read == 0)
            {
                line = string.Empty;
                return false;
            }

            length++;
            if (buffer[length - 1] == (byte)'\n')
            {
                line = Encoding.UTF8.GetString(buffer, 0, length);
                return true;
            }
        }

        // Oversized without a terminator — TryDecodeResponse will reject it as unrecognized, mapped
        // to ProtocolError by the caller.
        line = Encoding.UTF8.GetString(buffer, 0, length);
        return true;
    }
}
