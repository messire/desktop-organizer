using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="FileItem"/>.
/// </summary>
[Trait(UnitTests, nameof(Core))]
public class FileItemTests
{
    public static IEnumerable<object[]> FileItemTestData => new List<object[]>
    {
        new object[] { "Resources/File.txt", "File.txt", ".txt" },
        new object[] { "Resources/FileWithoutExtension", "FileWithoutExtension", string.Empty },
        new object[] { "FileWithoutPath.txt", "FileWithoutPath.txt", ".txt" },
        new object[] { string.Empty, string.Empty, string.Empty },
        new object[] { null!, string.Empty, string.Empty },
    };

    /// <summary>
    /// Ensures that <see cref="FileItem.Name"/> and <see cref="FileItem.Type"/> return correct values.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileItemTestData))]
    public void FileItem_Properties_ReturnCorrectValues(string? filePath, string expectedFileName, string expectedFileType)
    {
        // Arrange
        var fileItem = new FileItem(filePath ?? string.Empty, null);

        // Act & Assert
        Assert.Equal(expectedFileName, fileItem.Name);
        Assert.Equal(expectedFileType, fileItem.Type);
    }

    /// <summary>
    /// Ensures that <see cref="FileItem.ToString"/> returns the file name.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileItemTestData))]
    public void ToString_ReturnsCorrectFileName(string? filePath, string expectedFileName, string _)
    {
        // Arrange
        var fileItem = new FileItem(filePath ?? string.Empty, null);

        // Act
        var result = fileItem.ToString();

        // Assert
        Assert.Equal(expectedFileName, result);
    }
}
