using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;

using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>Unit tests for <see cref="ActivationMessageCodec"/> (ADR-023 D-5).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class ActivationMessageCodecTests
{
    // T-COD-01
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

    [Fact]
    public void EncodeRequest_ProducesExpectedShape()
    {
        var request = new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 42);

        var line = EncodeRequest(request);

        Assert.Equal("DO/1 ACTIVATE_SETTINGS s=42\n", line);
    }

    // T-COD-02
    [Fact]
    public void TryDecodeRequest_UnknownVerb_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/1 OPEN_PANEL s=1\n", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    // T-COD-03
    [Fact]
    public void TryDecodeRequest_UnsupportedProtocolVersion_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/2 ACTIVATE_SETTINGS s=1\n", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    // T-COD-04
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

    [Fact]
    public void TryDecodeRequest_TruncatedMidToken_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/1 ACTIVATE_SET", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    // T-COD-05
    [Fact]
    public void TryDecodeRequest_OversizedPayload_ReturnsUnsupported()
    {
        var oversized = "DO/1 ACTIVATE_SETTINGS s=" + new string('9', MaxMessageBytes) + "\n";

        var decoded = TryDecodeRequest(oversized, out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    // T-COD-06
    [Fact]
    public void TryDecodeRequest_EmptyString_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest(string.Empty, out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    // T-COD-07
    [Fact]
    public void TryDecodeRequest_GarbageBytes_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("�\0garbage", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    [Fact]
    public void TryDecodeRequest_NullSessionIdToken_ReturnsUnsupported()
    {
        var decoded = TryDecodeRequest("DO/1 ACTIVATE_SETTINGS s=not-a-number\n", out _, out var error);

        Assert.False(decoded);
        Assert.Equal(ActivationResponseCode.Unsupported, error);
    }

    [Fact]
    public void TryDecodeRequest_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TryDecodeRequest(null!, out _, out _));
    }

    // T-COD-08
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

    [Fact]
    public void TryDecodeResponse_UnknownWord_ReturnsFalse()
    {
        var decoded = TryDecodeResponse("DO/1 WAT\n", out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecodeResponse_UnsupportedProtocolVersion_ReturnsFalse()
    {
        var decoded = TryDecodeResponse("DO/2 OK\n", out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecodeResponse_EmptyString_ReturnsFalse()
    {
        var decoded = TryDecodeResponse(string.Empty, out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecodeResponse_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TryDecodeResponse(null!, out _));
    }
}
