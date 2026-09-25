using System.Text;

using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Wire format for the single-instance activation protocol (ADR-023 D-5): a single UTF-8 line,
/// version-tagged in every message, response always required. Pure string (de)serialization — no
/// I/O; the actual pipe transport lives in <c>NamedPipeActivationServer</c>/<c>Client</c> (C2).
/// </summary>
public static class ActivationMessageCodec
{
    /// <summary>
    /// Hard cap on the encoded message size (UTF-8 bytes), including the trailing <c>\n</c>. Chosen
    /// generously above the largest v1 payload so the accept loop can bound its read without parsing
    /// first (ADR-023 D-4 dicipline item 3).
    /// </summary>
    public const int MaxMessageBytes = 256;

    /// <summary>Response codes a primary can send back for an activation request (ADR-023 D-5).</summary>
    public enum ActivationResponseCode
    {
        /// <summary>Activation accepted and will be raised.</summary>
        Ok,

        /// <summary>Primary already entered shutdown — sender should retry acquiring ownership.</summary>
        ShuttingDown,

        /// <summary>Primary lives in a different Windows session (Fast User Switching, ADR-023 D-4).</summary>
        OtherSession,

        /// <summary>Unknown command or unsupported protocol version. Never silence — always answer.</summary>
        Unsupported,
    }

    private const int SupportedProtocolVersion = 1;
    private const string ProtocolPrefix = "DO/";
    private const string ActivateSettingsVerb = "ACTIVATE_SETTINGS";
    private const string SessionIdPrefix = "s=";

    /// <summary>
    /// Encodes an activation request as <c>"DO/&lt;version&gt; ACTIVATE_SETTINGS s=&lt;sessionId&gt;\n"</c>.
    /// </summary>
    /// <param name="request">The request to encode.</param>
    public static string EncodeRequest(ActivationRequest request)
    {
        var verb = request.Command switch
        {
            ActivationCommand.ActivateSettings => ActivateSettingsVerb,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "Unknown activation command."),
        };

        return $"{ProtocolPrefix}{request.ProtocolVersion} {verb} {SessionIdPrefix}{request.SessionId}\n";
    }

    /// <summary>
    /// Attempts to decode a request line. Never throws on malformed input — any decode failure
    /// (unknown verb, unsupported version, truncated/garbage line, oversized payload) is reported as
    /// <see cref="ActivationResponseCode.Unsupported"/> via <paramref name="error"/> so the server can
    /// always answer instead of silently dropping the connection (ADR-023 D-5).
    /// </summary>
    /// <param name="line">The raw line, with or without a trailing <c>\n</c>/<c>\r\n</c>.</param>
    /// <param name="request">The decoded request when this method returns <see langword="true"/>.</param>
    /// <param name="error">
    /// The response code to send back when this method returns <see langword="false"/>. Always
    /// <see cref="ActivationResponseCode.Unsupported"/> — v1 has no other decode-failure response.
    /// </param>
    public static bool TryDecodeRequest(string line, out ActivationRequest request, out ActivationResponseCode error)
    {
        ArgumentNullException.ThrowIfNull(line);

        request = default;
        error = ActivationResponseCode.Unsupported;

        if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
        {
            return false;
        }

        var parts = line.TrimEnd('\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!parts[0].StartsWith(ProtocolPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[0].AsSpan(ProtocolPrefix.Length), out var version) || version != SupportedProtocolVersion)
        {
            return false;
        }

        if (!string.Equals(parts[1], ActivateSettingsVerb, StringComparison.Ordinal))
        {
            return false;
        }

        if (!parts[2].StartsWith(SessionIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[2].AsSpan(SessionIdPrefix.Length), out var sessionId))
        {
            return false;
        }

        request = new ActivationRequest(version, ActivationCommand.ActivateSettings, sessionId);
        error = ActivationResponseCode.Ok;
        return true;
    }

    /// <summary>Encodes a response as <c>"DO/&lt;version&gt; &lt;WORD&gt;\n"</c>.</summary>
    /// <param name="code">The response code to encode.</param>
    public static string EncodeResponse(ActivationResponseCode code)
    {
        var word = code switch
        {
            ActivationResponseCode.Ok => "OK",
            ActivationResponseCode.ShuttingDown => "SHUTTING_DOWN",
            ActivationResponseCode.OtherSession => "OTHER_SESSION",
            ActivationResponseCode.Unsupported => "UNSUPPORTED",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown activation response code."),
        };

        return $"{ProtocolPrefix}{SupportedProtocolVersion} {word}\n";
    }

    /// <summary>
    /// Attempts to decode a response line. Returns <see langword="false"/> for anything it does not
    /// recognize (unsupported version, unknown word, truncated/garbage line, oversized payload) — the
    /// caller (the handshake client) is expected to map that to
    /// <see cref="ActivationSendResult.ProtocolError"/>.
    /// </summary>
    /// <param name="line">The raw line, with or without a trailing <c>\n</c>/<c>\r\n</c>.</param>
    /// <param name="code">The decoded response code when this method returns <see langword="true"/>.</param>
    public static bool TryDecodeResponse(string line, out ActivationResponseCode code)
    {
        ArgumentNullException.ThrowIfNull(line);

        code = default;

        if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
        {
            return false;
        }

        var parts = line.TrimEnd('\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!parts[0].StartsWith(ProtocolPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[0].AsSpan(ProtocolPrefix.Length), out var version) || version != SupportedProtocolVersion)
        {
            return false;
        }

        switch (parts[1])
        {
            case "OK":
                code = ActivationResponseCode.Ok;
                return true;
            case "SHUTTING_DOWN":
                code = ActivationResponseCode.ShuttingDown;
                return true;
            case "OTHER_SESSION":
                code = ActivationResponseCode.OtherSession;
                return true;
            case "UNSUPPORTED":
                code = ActivationResponseCode.Unsupported;
                return true;
            default:
                return false;
        }
    }
}
