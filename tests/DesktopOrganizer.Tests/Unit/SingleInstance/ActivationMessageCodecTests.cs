using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;

using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>Unit tests for <see cref="ActivationMessageCodec"/> (ADR-023 D-5).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class ActivationMessageCodecTests
{
    /// <summary>
    /// Verifies that an <see cref="ActivationRequest"/> round-trips through
    /// <c>EncodeRequest</c>/<c>TryDecodeRequest</c> unchanged (T-COD-01).
    /// </summary>
    [Fact]
    public void EncodeThenDecodeRequest_RoundTrips()
    {
        var request = new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 7);

        var line = EncodeRequest(request);
        var decoded = TryDecodeRequest(line, out var result, out var error);

        Assert.True(decoded);
        Assert.Equal(request, result);
        Assert.Equal(ActivationResponseCode.Ok, error);
    }

    /// <summary>
    /// Verifies that <c>EncodeRequest</c> produces the exact expected wire shape
    /// (<c>"DO/1 ACTIVATE_SETTINGS s=&lt;session&gt;\n"</c>).
    /// </summary>
    [Fact]
    public void EncodeRequest_ProducesExpectedShape()
    {
        var request = new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 42);

        var line = EncodeRequest(request);

        Assert.Equal("DO/1 ACTIVATE_SETTINGS s=42\n", line);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> for an unrecognized verb (T-COD-02).
    /// </summary>
    [Fact]
    public void TryDecodeRequest_UnknownVerb_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/1 OPEN_PANEL s=1\n", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> for a request with an unsupported protocol
    /// version (T-COD-03).
    /// </summary>
    [Fact]
    public void TryDecodeRequest_UnsupportedProtocolVersion_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/2 ACTIVATE_SETTINGS s=1\n", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> still parses a complete message even when the trailing
    /// framing newline has already been stripped by the reader (T-COD-04).
    /// </summary>
    [Fact]
    public void TryDecodeRequest_TruncatedWithoutTrailingNewline_StillParsesCompleteContent()
    {
        // The framing newline is a transport delimiter; a line already stripped of it by the reader
        // (a complete message that simply never had \n re-appended) must still decode.
        var decoded = TryDecodeRequest("DO/1 ACTIVATE_SETTINGS s=1", out var request, out var error);

        Assert.True(decoded);
        Assert.Equal(new ActivationRequest(1, ActivationCommand.ActivateSettings, 1), request);
        Assert.Equal(ActivationResponseCode.Ok, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> for a message truncated mid-token.
    /// </summary>
    [Fact]
    public void TryDecodeRequest_TruncatedMidToken_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/1 ACTIVATE_SET", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> for a message exceeding <c>MaxMessageBytes</c>
    /// (T-COD-05).
    /// </summary>
    [Fact]
    public void TryDecodeRequest_OversizedPayload_ReturnsUnsupported()
    {
        var oversized = "DO/1 ACTIVATE_SETTINGS s=" + new string('9', MaxMessageBytes) + "\n";

        var decoded = TryDecodeRequest(oversized, out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> for an empty string (T-COD-06).
    /// </summary>
    [Fact]
    public void TryDecodeRequest_EmptyString_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest(string.Empty, out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> for arbitrary garbage bytes (T-COD-07).
    /// </summary>
    [Fact]
    public void TryDecodeRequest_GarbageBytes_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("�\0garbage", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> returns <see langword="false"/> and
    /// <see cref="ActivationResponseCode.Unsupported"/> when the session id token is not a number.
    /// </summary>
    [Fact]
    public void TryDecodeRequest_NullSessionIdToken_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/1 ACTIVATE_SETTINGS s=not-a-number\n", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeRequest</c> throws <see cref="ArgumentNullException"/> for a
    /// <see langword="null"/> line.
    /// </summary>
    [Fact]
    public void TryDecodeRequest_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TryDecodeRequest(null!, out _, out _));
    }

    /// <summary>
    /// Verifies that every <see cref="ActivationResponseCode"/> round-trips through
    /// <c>EncodeResponse</c>/<c>TryDecodeResponse</c> unchanged (T-COD-08).
    /// </summary>
    /// <param name="code">The response code to round-trip.</param>
    [Theory]
    [InlineData(ActivationResponseCode.Ok)]
    [InlineData(ActivationResponseCode.ShuttingDown)]
    [InlineData(ActivationResponseCode.OtherSession)]
    [InlineData(ActivationResponseCode.Unsupported)]
    public void EncodeThenDecodeResponse_RoundTrips_AllCodes(ActivationResponseCode code)
    {
        var line = EncodeResponse(code);

        var decoded = TryDecodeResponse(line, out var result);

        Assert.True(decoded);
        Assert.Equal(code, result);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeResponse</c> returns <see langword="false"/> for an unrecognized
    /// response word.
    /// </summary>
    [Fact]
    public void TryDecodeResponse_UnknownWord_ReturnsFalse()
    {
        var decoded = TryDecodeResponse("DO/1 WAT\n", out _);

        Assert.False(decoded);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeResponse</c> returns <see langword="false"/> for a response with an
    /// unsupported protocol version.
    /// </summary>
    [Fact]
    public void TryDecodeResponse_UnsupportedProtocolVersion_ReturnsFalse()
    {
        var decoded = TryDecodeResponse("DO/2 OK\n", out _);

        Assert.False(decoded);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeResponse</c> returns <see langword="false"/> for an empty string.
    /// </summary>
    [Fact]
    public void TryDecodeResponse_EmptyString_ReturnsFalse()
    {
        var decoded = TryDecodeResponse(string.Empty, out _);

        Assert.False(decoded);
    }

    /// <summary>
    /// Verifies that <c>TryDecodeResponse</c> throws <see cref="ArgumentNullException"/> for a
    /// <see langword="null"/> line.
    /// </summary>
    [Fact]
    public void TryDecodeResponse_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TryDecodeResponse(null!, out _));
    }
}
