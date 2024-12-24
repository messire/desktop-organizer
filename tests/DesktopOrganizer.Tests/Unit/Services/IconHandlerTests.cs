using System.IO;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="IconHandlerTests"/> class.
    /// </summary>
    public IconHandlerTests()
    {
        _nativeApiMock = new Mock<INativeApi>();
        _iconHandler = new IconHandler(_nativeApiMock.Object);
    }

    public static IEnumerable<object[]> GetIconHandlerData => new List<object[]>
    {
        new object[] { ValidFilePath, ValidIconHandle },
        new object[] { InvalidFilePath, InvalidIconHandle },
    };

    public static IEnumerable<object[]> DestroyIconData => new List<object[]>
    {
        new object[] { true, string.Empty },
        new object[] { false, "Failed to destroy icon handle: {0}" },
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
    /// Ensures that DestroyIconHandle logs an error when DestroyIcon returns false.
    /// </summary>
    [Theory]
    [MemberData(nameof(DestroyIconData))]
    public void DestroyIconHandle_LogsError_WhenDestroyIconFails(bool destroyResult, string expectedMessage)
    {
        // Arrange
        SetupNativeApiMock(ValidFilePath, ValidIconHandle, destroyResult);
        using var consoleOutput = new StringWriter();
        var originalOutput = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            _iconHandler.DestroyIconHandle(ValidIconHandle);

            // Assert
            _nativeApiMock.Verify(api => api.DestroyIcon(ValidIconHandle), Times.Once);
            var output = consoleOutput.ToString();
            Assert.Contains(string.Format(expectedMessage, ValidIconHandle), output);
        }
        finally
        {
            Console.SetError(originalOutput);
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
