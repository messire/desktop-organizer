using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Storage;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Tests for <see cref="StorageRootReadme"/>: the bilingual <c>README.txt</c> left in the Store-channel
/// storage root on first run. It is a courtesy file, not persisted state.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class StorageRootReadmeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "DesktopOrganizerReadmeTests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Gets every combination of packaged flag, load status and writability.
    /// </summary>
    /// <returns>Rows of (packaged, status, writable).</returns>
    public static TheoryData<bool, StateLoadStatus, bool> AllCombinations()
    {
        var data = new TheoryData<bool, StateLoadStatus, bool>();
        foreach (var packaged in new[] { false, true })
        {
            foreach (var status in Enum.GetValues<StateLoadStatus>())
            {
                foreach (var writable in new[] { false, true })
                    data.Add(packaged, status, writable);
            }
        }

        return data;
    }

    /// <summary>
    /// Verifies the decision table: the README is written for exactly one combination, packaged,
    /// <see cref="StateLoadStatus.FirstRun"/>, writable, and for none of the others.
    /// </summary>
    /// <param name="packaged">Whether the process has package identity.</param>
    /// <param name="status">How the state was loaded.</param>
    /// <param name="writable">Whether storage is writable.</param>
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void ShouldWrite_OnlyForPackagedWritableFirstRun(bool packaged, StateLoadStatus status, bool writable)
    {
        var expected = packaged && status == StateLoadStatus.FirstRun && writable;

        Assert.Equal(expected, StorageRootReadme.ShouldWrite(packaged, status, writable));
    }

    /// <summary>
    /// Verifies that exactly one of all combinations writes (guards against the table silently widening).
    /// </summary>
    [Fact]
    public void ShouldWrite_IsTrueForExactlyOneCombination()
    {
        var trueCount = 0;
        foreach (var packaged in new[] { false, true })
        {
            foreach (var status in Enum.GetValues<StateLoadStatus>())
            {
                foreach (var writable in new[] { false, true })
                {
                    if (StorageRootReadme.ShouldWrite(packaged, status, writable)) trueCount++;
                }
            }
        }

        Assert.Equal(1, trueCount);
    }

    /// <summary>
    /// Verifies that the embedded text carries both languages, names the user-files folder and uses
    /// Windows line endings throughout.
    /// </summary>
    [Fact]
    public void ReadText_ContainsEnglishRussianAndPanelItems()
    {
        var text = StorageRootReadme.ReadText();

        Assert.NotNull(text);
        Assert.Contains("[English]", text, StringComparison.Ordinal);
        Assert.Contains("[Русский]", text, StringComparison.Ordinal);
        Assert.Contains("PanelItems", text, StringComparison.Ordinal);
        Assert.Contains("does NOT", text, StringComparison.Ordinal);
        Assert.Contains("НЕ удаляют", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the "how to get your files back" advice is truthful: it must not tell the user to
    /// "remove the item" from a panel (Delete on a managed item sends the file to the Recycle Bin), and it
    /// must name the real ways back (drag out of the panel, the "Return" button in the Backup settings) in both languages.
    /// </summary>
    [Fact]
    public void ReadText_RecoveryAdvice_DoesNotPromiseRemoveReturnsFile()
    {
        var text = StorageRootReadme.ReadText();

        Assert.NotNull(text);
        Assert.DoesNotContain("remove the item", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("удалите элемент", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drag", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("перетащите", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recycle Bin", text, StringComparison.Ordinal);
        Assert.Contains("Корзин", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <see cref="StorageRootReadme.TryWrite"/> creates the file (and the root) as UTF-8 with
    /// a BOM, with both language markers and the word <c>PanelItems</c>, and logs nothing.
    /// </summary>
    [Fact]
    public void TryWrite_CreatesBilingualUtf8File()
    {
        var log = new Mock<IAppLog>(MockBehavior.Strict);

        var written = StorageRootReadme.TryWrite(_tempRoot, log.Object);

        Assert.True(written);
        var path = Path.Combine(_tempRoot, StorageRootReadme.FileName);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = File.ReadAllText(path);
        Assert.Contains("[English]", text, StringComparison.Ordinal);
        Assert.Contains("[Русский]", text, StringComparison.Ordinal);
        Assert.Contains("PanelItems", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that an existing README (even a user-edited one) is never overwritten, without a warning.
    /// </summary>
    [Fact]
    public void TryWrite_ExistingFile_IsNotOverwritten()
    {
        Directory.CreateDirectory(_tempRoot);
        var path = Path.Combine(_tempRoot, StorageRootReadme.FileName);
        File.WriteAllText(path, "user edited");
        var log = new Mock<IAppLog>(MockBehavior.Strict);

        var written = StorageRootReadme.TryWrite(_tempRoot, log.Object);

        Assert.False(written);
        Assert.Equal("user edited", File.ReadAllText(path));
    }

    /// <summary>
    /// Verifies that a path occupied by something that is not a writable file (a directory named
    /// <c>README.txt</c>) neither throws nor leaves the log silent: a <c>Warn</c> is written.
    /// </summary>
    [Fact]
    public void TryWrite_PathOccupiedByDirectory_DoesNotThrowAndWarns()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, StorageRootReadme.FileName));
        var log = new Mock<IAppLog>();

        var written = StorageRootReadme.TryWrite(_tempRoot, log.Object);

        Assert.False(written);
        log.Verify(l => l.Warn("StorageRootReadme", "WriteFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>
    /// Verifies that a storage root that is itself a file (the directory cannot be created) neither throws
    /// nor stays silent.
    /// </summary>
    [Fact]
    public void TryWrite_RootIsAFile_DoesNotThrowAndWarns()
    {
        Directory.CreateDirectory(_tempRoot);
        var rootAsFile = Path.Combine(_tempRoot, "root-is-a-file");
        File.WriteAllText(rootAsFile, "x");
        var log = new Mock<IAppLog>();

        var written = StorageRootReadme.TryWrite(rootAsFile, log.Object);

        Assert.False(written);
        log.Verify(l => l.Warn("StorageRootReadme", "WriteFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }
    }
}
