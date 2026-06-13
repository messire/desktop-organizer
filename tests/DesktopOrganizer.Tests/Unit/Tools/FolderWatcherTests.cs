using System.IO;
using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="FolderWatcher"/> — filter logic and debounce behaviour.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class FolderWatcherTests
{
    // ── ShouldIgnore ────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldIgnore_DesktopIni_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\desktop.ini");
        Assert.True(result);
    }

    [Fact]
    public void ShouldIgnore_DesktopIni_CaseInsensitive_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\Desktop.INI");
        Assert.True(result);
    }

    [Fact]
    public void ShouldIgnore_ThumbsDb_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\thumbs.db");
        Assert.True(result);
    }

    [Fact]
    public void ShouldIgnore_ThumbsDb_CaseInsensitive_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\Thumbs.DB");
        Assert.True(result);
    }

    [Fact]
    public void ShouldIgnore_HiddenFile_ReturnsTrue()
    {
        // Arrange — create a real hidden temp file so GetAttributes works.
        var path = Path.Combine(Path.GetTempPath(), $"do_hidden_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hidden");
        File.SetAttributes(path, FileAttributes.Hidden);
        try
        {
            // Act
            var result = FolderWatcher.ShouldIgnore(path);

            // Assert
            Assert.True(result);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void ShouldIgnore_RegularFile_ReturnsFalse()
    {
        // Arrange — create a real non-hidden temp file.
        var path = Path.Combine(Path.GetTempPath(), $"do_regular_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "regular");
        try
        {
            // Act
            var result = FolderWatcher.ShouldIgnore(path);

            // Assert
            Assert.False(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShouldIgnore_DeletedFile_ReturnsFalse()
    {
        // A file that no longer exists should not be suppressed (Deleted event).
        var path = @"C:\NonExistentPath\ghost_file.txt";
        var result = FolderWatcher.ShouldIgnore(path);
        Assert.False(result);
    }

    // ── Debounce — integration tests with real FileSystemWatcher ────────────────

    [Fact]
    public void FolderChanged_IsRaised_AfterDebounceWindow()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        try
        {
            using var watcher = new FolderWatcher();
            var raised = new ManualResetEventSlim(false);
            watcher.Changed += (_, _) => raised.Set();

            watcher.Start(tempDir);

            // Act — create a file to trigger FSW
            File.WriteAllText(Path.Combine(tempDir, "file.txt"), "hello");

            // Assert — event must arrive within 2 seconds (debounce is ~300 ms)
            var fired = raised.Wait(TimeSpan.FromSeconds(2));
            Assert.True(fired, "Changed event was not raised within the timeout.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FolderChanged_NotRaised_DuringDebounceWindow()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        try
        {
            using var watcher = new FolderWatcher();
            var eventCount = 0;
            var firstFired = new ManualResetEventSlim(false);

            watcher.Changed += (_, _) =>
            {
                Interlocked.Increment(ref eventCount);
                firstFired.Set();
            };

            watcher.Start(tempDir);

            // Act — fire several FSW events within the 300 ms debounce window
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(tempDir, $"file_{i}.txt"), "x");
                Thread.Sleep(30); // well within debounce
            }

            // Wait for the debounced event to fire
            firstFired.Wait(TimeSpan.FromSeconds(2));

            // Give extra time to ensure no additional events arrive
            Thread.Sleep(600);

            // Assert — all rapid events should have been collapsed into exactly one
            Assert.Equal(1, eventCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"do_fw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
