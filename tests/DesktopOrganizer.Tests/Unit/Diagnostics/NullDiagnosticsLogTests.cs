using System.IO;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="NullDiagnosticsLog"/> — the zero-cost, disabled-by-default sink.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class NullDiagnosticsLogTests
{
    [Fact]
    public void IsEnabled_ReturnsFalse()
    {
        Assert.False(NullDiagnosticsLog.Instance.IsEnabled);
    }

    [Fact]
    public void Instance_IsASingleton_SameReferenceEveryAccess()
    {
        var first = NullDiagnosticsLog.Instance;
        var second = NullDiagnosticsLog.Instance;

        Assert.Same(first, second);
    }

    [Fact]
    public void Write_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            NullDiagnosticsLog.Instance.Write(
                "gesture",
                "ResizeDelta",
                "abcd1234",
                new KeyValuePair<string, object?>("hChange", 1.0)));

        Assert.Null(exception);
    }

    [Fact]
    public void Write_WithNoPayload_DoesNotThrow()
    {
        var exception = Record.Exception(() => NullDiagnosticsLog.Instance.Write("session", "SessionExiting", null));

        Assert.Null(exception);
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        var exception = Record.Exception(NullDiagnosticsLog.Instance.Flush);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var exception = Record.Exception(NullDiagnosticsLog.Instance.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        NullDiagnosticsLog.Instance.Dispose();
        var exception = Record.Exception(NullDiagnosticsLog.Instance.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Instance_UsableAfterDispose_StillNoOp()
    {
        // The singleton carries no state, so a Dispose() call from one caller must never poison it
        // for every other caller sharing the same static instance.
        NullDiagnosticsLog.Instance.Dispose();

        Assert.False(NullDiagnosticsLog.Instance.IsEnabled);
        var exception = Record.Exception(() => NullDiagnosticsLog.Instance.Write("gesture", "ResizeStarted", null));
        Assert.Null(exception);
    }

    /// <summary>
    /// <see cref="NullDiagnosticsLog"/> carries no directory/file state at all — unlike
    /// <c>JsonLinesDiagnosticsLog</c>, there is nothing to "point it at". This test documents that
    /// invariant directly: exercising every member never creates a file or directory anywhere,
    /// including at an arbitrary path a caller might otherwise expect a diagnostics sink to touch.
    /// </summary>
    [Fact]
    public void Operations_NeverCreateAnyFileOrDirectory_OnDisk()
    {
        var probeDirectory = Path.Combine(Path.GetTempPath(), $"DOrgNullDiagProbe_{Guid.NewGuid():N}");

        var sut = NullDiagnosticsLog.Instance;
        sut.Write(
            "gesture",
            "ResizeDelta",
            "abcd1234",
            new KeyValuePair<string, object?>("hChange", 1.0),
            new KeyValuePair<string, object?>("vChange", 2.0));
        sut.Flush();
        sut.Dispose();

        Assert.False(Directory.Exists(probeDirectory), "NullDiagnosticsLog must never create a directory.");
        Assert.False(File.Exists(probeDirectory), "NullDiagnosticsLog must never create a file.");
    }
}
