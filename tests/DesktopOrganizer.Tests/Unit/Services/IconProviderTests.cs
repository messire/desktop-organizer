using System.IO;
using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Models;
using Moq;
using static DesktopOrganizer.Services.Enums.FileAttribute;
using static DesktopOrganizer.Services.Enums.FileFlag;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="IconProvider" />.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class IconProviderTests
{
    private const string InvalidFilePath = "InvalidFilePath.txt";
    private const string ValidFilePath = "Resources/TestFile.txt";
    private const FileFlag IconFlags = Icon | ShellIconSize | UseFileAttributes;
    private const FileAttribute FileAttributes = Normal;

    private static readonly IntPtr ValidIconHandle = new (12345);
    private static readonly IntPtr InvalidIconHandle = IntPtr.Zero;

    private readonly IconProvider _iconProvider;
    private readonly Mock<IIconHandler> _iconHandlerMock;
    private readonly Mock<IIconFactory> _iconFactoryMock;

    public IconProviderTests()
    {
        _iconHandlerMock = new Mock<IIconHandler>();
        _iconFactoryMock = new Mock<IIconFactory>();
        _iconProvider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object);
    }

    public static IEnumerable<object[]> GetIconTestData => new List<object[]>
    {
        new object[] { ValidFilePath, ValidIconHandle },
        new object[] { InvalidFilePath, InvalidIconHandle },
    };

    /// <summary>
    /// Ensures that GetIcon behaves correctly for various file paths.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetIconTestData))]
    public void GetIcon_BehavesCorrectly_ForVariousFilePaths(string filePath, IntPtr iconHandle)
    {
        // Arrange
        var expectedIcon = new DrawingImage();
        SetupMocks(filePath, iconHandle, expectedIcon);

        // Act
        var result = _iconProvider.GetIcon(filePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedIcon, result);
    }

    /// <summary>
    /// Ensures that GetIcon caches icons for the same file path.
    /// </summary>
    [Fact]
    public void GetIcon_CachesIcons_ForSameFilePath()
    {
        // Arrange
        var expectedIcon = new DrawingImage();
        var shellFileInfo = SetupMocks(ValidFilePath, ValidIconHandle, expectedIcon);

        // Act
        var firstResult = _iconProvider.GetIcon(ValidFilePath);
        var secondResult = _iconProvider.GetIcon(ValidFilePath);

        // Assert
        Assert.Same(firstResult, secondResult);
        _iconHandlerMock
            .Verify(iconHandler => iconHandler.GetIconHandle(ValidFilePath, ref shellFileInfo, FileAttributes, IconFlags), Times.Once);
    }

    [Fact]
    public void GetIcon_ThrowsException_WhenIconHandlerFails()
    {
        // Arrange
        var exceptionMessage = "Test exception";
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Throws(new InvalidOperationException(exceptionMessage));

        using var consoleOutput = new StringWriter();
        var originalOutput = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            var result = _iconProvider.GetIcon(ValidFilePath);

            // Assert
            _iconHandlerMock.Verify(api => api.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags), Times.Once);
            var output = consoleOutput.ToString();
            Assert.Contains($"Error retrieving icon for '{ValidFilePath}': {exceptionMessage}", output);
        }
        finally
        {
            Console.SetError(originalOutput);
        }
    }

    private ShellFileInfo SetupMocks(string filePath, IntPtr iconHandle, DrawingImage expectedIcon)
    {
        var shellFileInfo = default(ShellFileInfo);

        _iconHandlerMock
            .Setup(handler => handler.GetIconHandle(filePath, ref shellFileInfo, FileAttributes, IconFlags))
            .Returns(iconHandle);

        if (iconHandle == ValidIconHandle)
        {
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(expectedIcon);
        }
        else
        {
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap())
                .Returns(expectedIcon);
        }

        return shellFileInfo;
    }
}
