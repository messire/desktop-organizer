using System.Runtime.InteropServices;
using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Models;
using DesktopOrganizer.Tests.Resources;
using static DesktopOrganizer.Services.Enums.FileFlag;

namespace DesktopOrganizer.Tests.Integration.Services;


/// <summary>
/// Integration tests for <see cref="NativeApi"/>.
/// </summary>
[Trait(IntegrationTests, nameof(Services))]
public class NativeApiTests
{
    private readonly NativeApi _nativeApi = new ();
    private const FileAttribute Attributes = FileAttribute.Normal;

    public static IEnumerable<object[]> FileInfoTestData => new List<object[]>
    {
        new object[] { Path.GetFullPath("Resources/TestFile.txt"), true, Icon | UseFileAttributes},
        new object[] { Path.GetFullPath("Resources/NonExistentFile.txt"), false, Icon }
    };

    /// <summary>
    /// Ensures that GetFileInfo returns the expected result for valid and invalid file paths.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileInfoTestData))]
    public void GetFileInfo_ReturnsExpectedResult(string filePath, bool shouldReturnValidHandle, FileFlag flags)
    {
        // Arrange
        var fileInfo = default(ShellFileInfo);

        // Act
        var result = _nativeApi.GetFileInfo(filePath, (uint)Attributes, ref fileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), (uint)flags);

        // Assert
        if (shouldReturnValidHandle)
        {
            Assert.NotEqual(IntPtr.Zero, result);
            _nativeApi.DestroyIcon(result); // Cleanup
        }
        else
        {
            Assert.Equal(IntPtr.Zero, result);
        }
    }

    /// <summary>
    /// Ensures that DestroyIcon behaves correctly for valid and invalid handles.
    /// </summary>
    [Fact]
    public void DestroyIcon_BehavesAsExpected()
    {
        // Arrange
        var validHandle = User32.LoadIcon(IntPtr.Zero, User32.IDI_APPLICATION);
        var invalidHandle = IntPtr.Zero;

        // Act & Assert for valid handle
        Assert.NotEqual(IntPtr.Zero, validHandle);
        Assert.True(_nativeApi.DestroyIcon(validHandle));

        // Act & Assert for invalid handle
        Assert.False(_nativeApi.DestroyIcon(invalidHandle));
    }
}
