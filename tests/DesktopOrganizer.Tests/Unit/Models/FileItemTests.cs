using System.IO;
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

    // ---------------------------------------------------------------------------
    // Regression: Exists/IsDirectory must live-update via Refresh(), not just at
    // construction time. Root cause of the "dropped shortcut looks alive after the
    // original is deleted" bug — FileItem used to be a record with Exists computed
    // fresh on every access, but its WPF binding only re-pulls on PropertyChanged
    // (or container regeneration), so a file deleted after the item was already
    // rendered never greyed out until the panel reloaded from disk.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A file that exists at construction time reports <c>Exists == true</c>.
    /// </summary>
    [Fact]
    public void Exists_FileExistsAtConstruction_ReturnsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgFileItem_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            var item = new FileItem(path, null);
            Assert.True(item.Exists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// If the underlying file is deleted after construction, <see cref="FileItem.Exists"/> stays
    /// stale (<c>true</c>) until <see cref="FileItem.Refresh"/> is called — this is the documented
    /// contract, not a bug: liveness is a snapshot, and callers that need live tracking must poll.
    /// </summary>
    [Fact]
    public void Exists_FileDeletedAfterConstruction_StaysStaleUntilRefresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgFileItem_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        var item = new FileItem(path, null);
        Assert.True(item.Exists);

        File.Delete(path);

        Assert.True(item.Exists); // stale snapshot, by design
        item.Refresh();
        Assert.False(item.Exists); // now reflects reality
    }

    /// <summary>
    /// <see cref="FileItem.Refresh"/> raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// for <c>Exists</c> when the file disappears — this is what lets the panel's
    /// <c>DataTrigger Binding="{Binding Exists}"</c> grey the item out live, without a restart.
    /// </summary>
    [Fact]
    public void Refresh_FileDeleted_RaisesPropertyChangedForExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgFileItem_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        var item = new FileItem(path, null);

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        File.Delete(path);
        item.Refresh();

        Assert.Contains(nameof(FileItem.Exists), raised);
    }

    /// <summary>
    /// <see cref="FileItem.Refresh"/> raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// for <c>Exists</c> again when a previously-deleted file reappears (e.g. restored from Recycle Bin).
    /// </summary>
    [Fact]
    public void Refresh_FileRestoredAfterDeletion_RaisesPropertyChangedAndReflectsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgFileItem_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        var item = new FileItem(path, null);

        File.Delete(path);
        item.Refresh();
        Assert.False(item.Exists);

        File.WriteAllText(path, string.Empty);
        try
        {
            var raised = new List<string?>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            item.Refresh();

            Assert.True(item.Exists);
            Assert.Contains(nameof(FileItem.Exists), raised);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <see cref="FileItem.Refresh"/> must not raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// when nothing changed — repeated polling shouldn't cause needless UI churn/re-render.
    /// </summary>
    [Fact]
    public void Refresh_NothingChanged_DoesNotRaisePropertyChanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgFileItem_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            var item = new FileItem(path, null);
            var raised = 0;
            item.PropertyChanged += (_, _) => raised++;

            item.Refresh();

            Assert.Equal(0, raised);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
