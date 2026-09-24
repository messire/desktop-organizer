using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Models;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit and integration tests for <see cref="IconHandler"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class IconHandlerTests
{
    private const string InvalidFilePath = "InvalidFilePath.txt";
    private const string ValidFilePath = "Resources/TestFile.txt";

    private static readonly IntPtr ValidIconHandle = new (12345);
    private static readonly IntPtr InvalidIconHandle = IntPtr.Zero;

    private readonly IconHandler _iconHandler;
    private readonly Mock<INativeApi> _nativeApiMock;
    private readonly Mock<IDiagnosticsLog> _diagnosticsLogMock;
    private readonly List<(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)> _diagnosticsWrites = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="IconHandlerTests"/> class.
    /// </summary>
    public IconHandlerTests()
    {
        _nativeApiMock = new Mock<INativeApi>();
        _diagnosticsLogMock = new Mock<IDiagnosticsLog>();
        _diagnosticsLogMock.SetupGet(l => l.IsEnabled).Returns(true);
        _diagnosticsLogMock
            .Setup(l => l.Write(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback<string, string, string?, KeyValuePair<string, object?>[]>(
                (category, name, scope, payload) => _diagnosticsWrites.Add((category, name, scope, payload)));

        _iconHandler = new IconHandler(_nativeApiMock.Object, _diagnosticsLogMock.Object);
    }

    public static IEnumerable<object[]> GetIconHandlerData => new List<object[]>
    {
        new object[] { ValidFilePath, ValidIconHandle },
        new object[] { InvalidFilePath, InvalidIconHandle },
    };

    /// <summary>
    /// Ensures that GetIconHandle returns the expected handle for valid and invalid file paths.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetIconHandlerData))]
    public void GetIconHandle_ReturnsExpectedHandle_ForGivenFilePath(string filePath, IntPtr expectedHandle)
    {
        // Arrange
        var shellFileInfo = default(ShellFileInfo);
        SetupNativeApiMock(filePath, expectedHandle, true);

        // Act
        var result = _iconHandler.GetIconHandle(filePath, ref shellFileInfo, FileAttribute.Normal, FileFlag.Icon);

        // Assert
        Assert.Equal(expectedHandle, result);
    }

    /// <summary>
    /// Regression for the P00 baseline audit (docs/DIAGNOSTICS/icon-loading-findings.md): a failed
    /// shell lookup used to go to <c>Console.Error</c>, which a <c>WinExe</c> process never surfaces
    /// anywhere. It must now reach the diagnostics log instead, with a privacy-safe payload (bounded
    /// extension + closed-set attribute name — never the path itself).
    /// </summary>
    [Fact]
    public void GetIconHandle_WritesDiagnosticsEvent_WhenHandleIsInvalid()
    {
        // Arrange
        var shellFileInfo = default(ShellFileInfo);
        SetupNativeApiMock(InvalidFilePath, InvalidIconHandle, true);

        // Act
        _iconHandler.GetIconHandle(InvalidFilePath, ref shellFileInfo, FileAttribute.Normal, FileFlag.Icon);

        // Assert
        var write = Assert.Single(_diagnosticsWrites, w => w.Name == "IconHandleFailed");
        Assert.Equal("icon", write.Category);
        Assert.Null(write.Scope);
        Assert.Equal(".txt", GetPayloadValue<string>(write.Payload, "extension"));
        Assert.Equal("Normal", GetPayloadValue<string>(write.Payload, "attributes"));
        Assert.DoesNotContain(write.Payload, kv => kv.Value is string s && s.Contains(InvalidFilePath));
    }

    /// <summary>
    /// A successful lookup must never write a diagnostics event — only the failure path does.
    /// </summary>
    [Fact]
    public void GetIconHandle_DoesNotWriteDiagnosticsEvent_WhenHandleIsValid()
    {
        // Arrange
        var shellFileInfo = default(ShellFileInfo);
        SetupNativeApiMock(ValidFilePath, ValidIconHandle, true);

        // Act
        _iconHandler.GetIconHandle(ValidFilePath, ref shellFileInfo, FileAttribute.Normal, FileFlag.Icon);

        // Assert
        Assert.Empty(_diagnosticsWrites);
    }

    /// <summary>
    /// Ensures that DestroyIconHandle writes a diagnostics event when DestroyIcon returns false, and
    /// none when it succeeds (P00 baseline: previously <c>Console.Error</c>, silently discarded).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DestroyIconHandle_WritesDiagnosticsEvent_OnlyWhenDestroyIconFails(bool destroyResult)
    {
        // Arrange
        SetupNativeApiMock(ValidFilePath, ValidIconHandle, destroyResult);

        // Act
        _iconHandler.DestroyIconHandle(ValidIconHandle);

        // Assert
        _nativeApiMock.Verify(api => api.DestroyIcon(ValidIconHandle), Times.Once);
        if (destroyResult)
        {
            Assert.Empty(_diagnosticsWrites);
        }
        else
        {
            var write = Assert.Single(_diagnosticsWrites, w => w.Name == "IconDestroyFailed");
            Assert.Equal("icon", write.Category);
        }
    }

    /// <summary>
    /// Ensures that DestroyIconHandle does not call DestroyIcon for an invalid handle.
    /// </summary>
    [Fact]
    public void DestroyIconHandle_DoesNotCallDestroyIcon_ForInvalidHandle()
    {
        // Act
        _iconHandler.DestroyIconHandle(InvalidIconHandle);

        // Assert
        _nativeApiMock.Verify(api => api.DestroyIcon(It.IsAny<IntPtr>()), Times.Never);
    }

    private static T GetPayloadValue<T>(KeyValuePair<string, object?>[] payload, string key)
        => (T)payload.Single(kv => kv.Key == key).Value!;

    /// <summary>
    /// Configures the native API mock for given parameters.
    /// </summary>
    private void SetupNativeApiMock(string filePath, nint iconHandle, bool destroyResult)
    {
        var shellFileInfo = default(ShellFileInfo);

        _nativeApiMock
            .Setup(api => api.GetFileInfo(filePath, It.IsAny<uint>(), ref shellFileInfo, It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(iconHandle);

        _nativeApiMock
            .Setup(api => api.DestroyIcon(iconHandle))
            .Returns(destroyResult);
    }
}
