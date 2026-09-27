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

    /// <summary>
    /// Gets the file-path/expected-handle pairs exercised by
    /// <see cref="GetIconHandle_ReturnsExpectedHandle_ForGivenFilePath"/>.
    /// </summary>
    public static IEnumerable<object[]> GetIconHandlerData => new List<object[]>
    {
        new object[] { ValidFilePath, ValidIconHandle },
        new object[] { InvalidFilePath, InvalidIconHandle },
    };

    /// <summary>
    /// Ensures that GetIconHandle returns the expected handle for valid and invalid file paths.
    /// </summary>
    /// <param name="filePath">The file path passed to <see cref="IconHandler.GetIconHandle"/>.</param>
    /// <param name="expectedHandle">The icon handle the native API mock is set up to return for that path.</param>
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
    /// <param name="destroyResult">The value the mocked native <c>DestroyIcon</c> call is set up to return.</param>
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

    // ── release-polish 1.0: high-resolution stages ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a successful <c>IShellItemImageFactory</c> call hands the bitmap through untouched and
    /// writes no diagnostics event.
    /// </summary>
    [Fact]
    public void GetShellItemBitmap_Success_ReturnsHandle_WithoutDiagnostics()
    {
        var bitmap = new IntPtr(555);
        _nativeApiMock.Setup(api => api.GetShellItemImage(ValidFilePath, 96, out bitmap)).Returns(0);

        var result = _iconHandler.GetShellItemBitmap(ValidFilePath, 96);

        Assert.Equal(bitmap, result);
        Assert.Empty(_diagnosticsWrites);
        _nativeApiMock.Verify(api => api.DeleteBitmap(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that a failing <c>HRESULT</c> yields no handle and a privacy-safe <c>ShellItemImageFailed</c>
    /// event (extension + hex HRESULT, never the path).
    /// </summary>
    [Fact]
    public void GetShellItemBitmap_FailureHResult_ReturnsZero_AndWritesDiagnostics()
    {
        var none = IntPtr.Zero;
        _nativeApiMock
            .Setup(api => api.GetShellItemImage(ValidFilePath, 48, out none))
            .Returns(unchecked((int)0x80004005));

        var result = _iconHandler.GetShellItemBitmap(ValidFilePath, 48);

        Assert.Equal(IntPtr.Zero, result);
        var write = Assert.Single(_diagnosticsWrites, w => w.Name == "ShellItemImageFailed");
        Assert.Equal("icon", write.Category);
        Assert.Equal(".txt", GetPayloadValue<string>(write.Payload, "extension"));
        Assert.Equal("0x80004005", GetPayloadValue<string>(write.Payload, "hresult"));
        Assert.DoesNotContain(write.Payload, kv => kv.Value is string s && s.Contains(ValidFilePath));
    }

    /// <summary>
    /// Verifies that a native layer reporting failure while still producing a bitmap does not leak it.
    /// </summary>
    [Fact]
    public void GetShellItemBitmap_FailureWithStrayHandle_DeletesIt()
    {
        var stray = new IntPtr(321);
        _nativeApiMock
            .Setup(api => api.GetShellItemImage(ValidFilePath, 48, out stray))
            .Returns(unchecked((int)0x80004005));

        var result = _iconHandler.GetShellItemBitmap(ValidFilePath, 48);

        Assert.Equal(IntPtr.Zero, result);
        _nativeApiMock.Verify(api => api.DeleteBitmap(stray), Times.Once);
    }

    /// <summary>
    /// Verifies that <c>S_OK</c> with a null bitmap is treated as a failure, not as success.
    /// </summary>
    [Fact]
    public void GetShellItemBitmap_SuccessWithNullHandle_IsAFailure()
    {
        var none = IntPtr.Zero;
        _nativeApiMock.Setup(api => api.GetShellItemImage(ValidFilePath, 48, out none)).Returns(0);

        var result = _iconHandler.GetShellItemBitmap(ValidFilePath, 48);

        Assert.Equal(IntPtr.Zero, result);
        Assert.Single(_diagnosticsWrites, w => w.Name == "ShellItemImageFailed");
    }

    /// <summary>
    /// Verifies that the system image list stage asks <c>SHGetFileInfo</c> for the icon index WITHOUT
    /// <c>SHGFI_USEFILEATTRIBUTES</c> (so the shell reads the item itself) and without <c>SHGFI_ICON</c>, then
    /// extracts that index from the list matching the requested size.
    /// </summary>
    [Fact]
    public void GetSystemImageListIconHandle_UsesItemOwnIndex_FromMatchingImageList()
    {
        const int iconIndex = 17;
        var icon = new IntPtr(888);
        uint capturedFlags = 0;
        _nativeApiMock
            .Setup(api => api.GetFileInfo(ValidFilePath, It.IsAny<uint>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(new GetFileInfoCallback((string _, uint _, ref ShellFileInfo info, uint _, uint flags) =>
            {
                capturedFlags = flags;
                info.IconIndex = iconIndex;
                return new IntPtr(1);
            }));
        _nativeApiMock.Setup(api => api.GetSystemImageListIcon(IconHandler.ShilExtraLarge, iconIndex)).Returns(icon);

        var result = _iconHandler.GetSystemImageListIconHandle(ValidFilePath, 48);

        Assert.Equal(icon, result);
        Assert.Equal((uint)FileFlag.SysIconIndex, capturedFlags);
        Assert.Empty(_diagnosticsWrites);
    }

    /// <summary>
    /// Verifies that a failed index lookup never reaches the image list and is logged.
    /// </summary>
    [Fact]
    public void GetSystemImageListIconHandle_IndexLookupFails_ReturnsZero_AndWritesDiagnostics()
    {
        var result = _iconHandler.GetSystemImageListIconHandle(ValidFilePath, 48);

        Assert.Equal(IntPtr.Zero, result);
        _nativeApiMock.Verify(api => api.GetSystemImageListIcon(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        var write = Assert.Single(_diagnosticsWrites, w => w.Name == "SystemIconIndexFailed");
        Assert.Equal(".txt", GetPayloadValue<string>(write.Payload, "extension"));
    }

    /// <summary>
    /// Verifies that a failed extraction from the image list is logged with the list id.
    /// </summary>
    [Fact]
    public void GetSystemImageListIconHandle_ExtractionFails_WritesDiagnostics()
    {
        _nativeApiMock
            .Setup(api => api.GetFileInfo(ValidFilePath, It.IsAny<uint>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(new IntPtr(1));

        var result = _iconHandler.GetSystemImageListIconHandle(ValidFilePath, 256);

        Assert.Equal(IntPtr.Zero, result);
        var write = Assert.Single(_diagnosticsWrites, w => w.Name == "ImageListIconFailed");
        Assert.Equal(IconHandler.ShilJumbo, GetPayloadValue<int>(write.Payload, "imageList"));
    }

    /// <summary>
    /// Verifies the pixel size → system image list mapping.
    /// </summary>
    /// <param name="pixelSize">Requested size.</param>
    /// <param name="expected">Expected <c>SHIL_*</c> value.</param>
    [Theory]
    [InlineData(16, IconHandler.ShilSmall)]
    [InlineData(20, IconHandler.ShilLarge)]
    [InlineData(32, IconHandler.ShilLarge)]
    [InlineData(40, IconHandler.ShilExtraLarge)]
    [InlineData(48, IconHandler.ShilExtraLarge)]
    [InlineData(96, IconHandler.ShilExtraLarge)]
    [InlineData(128, IconHandler.ShilJumbo)]
    [InlineData(256, IconHandler.ShilJumbo)]
    public void SystemImageListFor_MapsSizeToList(int pixelSize, int expected)
        => Assert.Equal(expected, IconHandler.SystemImageListFor(pixelSize));

    /// <summary>
    /// Verifies bitmap release: a zero handle is never passed to <c>DeleteObject</c>, a failed release is logged.
    /// </summary>
    [Fact]
    public void DestroyBitmapHandle_SkipsZero_AndLogsFailure()
    {
        var bitmap = new IntPtr(42);
        _nativeApiMock.Setup(api => api.DeleteBitmap(bitmap)).Returns(false);

        _iconHandler.DestroyBitmapHandle(IntPtr.Zero);
        _iconHandler.DestroyBitmapHandle(bitmap);

        _nativeApiMock.Verify(api => api.DeleteBitmap(IntPtr.Zero), Times.Never);
        _nativeApiMock.Verify(api => api.DeleteBitmap(bitmap), Times.Once);
        Assert.Single(_diagnosticsWrites, w => w.Name == "IconBitmapDestroyFailed");
    }

    /// <summary>
    /// Verifies that an unreadable bitmap reports failure and is logged, without ever deleting the handle
    /// (the caller owns it).
    /// </summary>
    [Fact]
    public void TryReadBitmapPixels_Failure_ReturnsFalse_LogsAndDoesNotDelete()
    {
        var bitmap = new IntPtr(43);

        var ok = _iconHandler.TryReadBitmapPixels(bitmap, out _);

        Assert.False(ok);
        Assert.Single(_diagnosticsWrites, w => w.Name == "IconBitmapReadFailed");
        _nativeApiMock.Verify(api => api.DeleteBitmap(It.IsAny<IntPtr>()), Times.Never);
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

    /// <summary>
    /// Mirrors <see cref="INativeApi.GetFileInfo"/> so a Moq setup can write into its <c>ref</c> parameter.
    /// </summary>
    private delegate IntPtr GetFileInfoCallback(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);
}
