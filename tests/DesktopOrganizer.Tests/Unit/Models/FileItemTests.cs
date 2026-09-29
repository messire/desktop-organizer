using System.IO;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Core;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="FileItem"/>.
/// </summary>
[Trait(UnitTests, nameof(Core))]
public class FileItemTests
{
    /// <summary>
    /// Gets a shared set of (filePath, expectedFileName, expectedFileType) cases covering a normal
    /// path, an extensionless path, a path with no directory, an empty path, and a <see langword="null"/>
    /// path, reused by both the <see cref="FileItem.Name"/>/<see cref="FileItem.Type"/> and
    /// <see cref="FileItem.ToString"/> theories below.
    /// </summary>
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
    /// <param name="filePath">The path passed to the <see cref="FileItem"/> constructor.</param>
    /// <param name="expectedFileName">The expected value of <see cref="FileItem.Name"/>.</param>
    /// <param name="expectedFileType">The expected value of <see cref="FileItem.Type"/>.</param>
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
    /// <param name="filePath">The path passed to the <see cref="FileItem"/> constructor.</param>
    /// <param name="expectedFileName">The expected value returned by <see cref="FileItem.ToString"/>.</param>
    /// <param name="_">The expected file type from <see cref="FileItemTestData"/>, unused here.</param>
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
    // DisplayName hides a shortcut's .lnk extension, matching Explorer's default
    // "hide extensions" behavior — Name/Path/Type stay unaffected (they reflect the
    // real on-disk state), only the rendering-only DisplayName is trimmed.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A shortcut's <see cref="FileItem.DisplayName"/> omits the trailing <c>.lnk</c>, while
    /// <see cref="FileItem.Name"/> still carries it — only the rendering concern is affected.
    /// </summary>
    [Fact]
    public void DisplayName_Shortcut_OmitsLnkExtension()
    {
        var item = new FileItem(@"C:\fake\Chrome.lnk", null);

        Assert.True(item.IsShortcut);
        Assert.Equal("Chrome", item.DisplayName);
        Assert.Equal("Chrome.lnk", item.Name);
    }

    /// <summary>
    /// The <c>.lnk</c> match is case-insensitive, same as <see cref="FileItem.IsShortcut"/> itself.
    /// </summary>
    [Fact]
    public void DisplayName_ShortcutWithUppercaseExtension_OmitsExtension()
    {
        var item = new FileItem(@"C:\fake\Chrome.LNK", null);

        Assert.True(item.IsShortcut);
        Assert.Equal("Chrome", item.DisplayName);
    }

    /// <summary>
    /// A non-shortcut whose name merely contains <c>.lnk</c> in the middle (not as its actual
    /// extension) must not have anything stripped — <see cref="FileItem.IsShortcut"/> is false, so
    /// <see cref="FileItem.DisplayName"/> falls back to the plain <see cref="FileItem.Name"/>-equal path.
    /// </summary>
    [Fact]
    public void DisplayName_NonShortcutNameContainingLnk_IsUnaffected()
    {
        var item = new FileItem(@"C:\fake\file.lnk.txt", null);

        Assert.False(item.IsShortcut);
        Assert.Equal("file.lnk.txt", item.DisplayName);
    }

    /// <summary>
    /// A cosmetic <c>displayName</c> override (see ADR-013 Decision 3, legacy GUID-suffix stripping)
    /// on a managed shortcut still gets its <c>.lnk</c> stripped — the override only replaces
    /// <see cref="FileItem.Name"/> as the base, the shortcut-extension trim applies on top of it.
    /// </summary>
    [Fact]
    public void DisplayName_ShortcutWithDisplayNameOverride_OmitsLnkExtension()
    {
        var item = new FileItem(@"C:\fake\Chrome_9f3c1a2b3c4d5e6f7a8b9c0d1e2f3a4b.lnk", null, displayName: "Chrome.lnk");

        Assert.Equal("Chrome", item.DisplayName);
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

    // ---------------------------------------------------------------------------
    // Relocate (ADR-013 Decision 4/6): updates a FileItem in place after a physical rename, instead
    // of the caller discarding and recreating it — so bound UI state (selection, container) survives.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Every derived property (<see cref="FileItem.Path"/>, <see cref="FileItem.Icon"/>,
    /// <see cref="FileItem.Name"/>, <see cref="FileItem.DisplayName"/>, <see cref="FileItem.Type"/>,
    /// <see cref="FileItem.IsShortcut"/>) reflects the new path after <see cref="FileItem.Relocate"/>,
    /// and each raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>.
    /// </summary>
    [Fact]
    public void Relocate_UpdatesAllDerivedProperties_AndRaisesPropertyChangedForEach()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), $"DOrgRelocateOld_{Guid.NewGuid():N}.txt");
        var newPath = Path.Combine(Path.GetTempPath(), $"DOrgRelocateNew_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(newPath, string.Empty); // exists at the destination, unlike the old path

        try
        {
            var item = new FileItem(oldPath, null);
            Assert.False(item.Exists);

            var newIcon = new BitmapImage();
            var raised = new List<string?>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            item.Relocate(newPath, newIcon);

            Assert.Equal(newPath, item.Path);
            Assert.Same(newIcon, item.Icon);
            Assert.Equal(Path.GetFileName(newPath), item.Name);
            Assert.Equal(".lnk", item.Type);
            Assert.True(item.IsShortcut);

            // DisplayName hides the .lnk extension for shortcuts (Explorer parity) — Name still has it.
            Assert.Equal(Path.GetFileNameWithoutExtension(newPath), item.DisplayName);
            Assert.NotEqual(item.Name, item.DisplayName);
            Assert.True(item.Exists);
            Assert.False(item.IsDirectory);

            Assert.Contains(nameof(FileItem.Path), raised);
            Assert.Contains(nameof(FileItem.Icon), raised);
            Assert.Contains(nameof(FileItem.Name), raised);
            Assert.Contains(nameof(FileItem.DisplayName), raised);
            Assert.Contains(nameof(FileItem.Type), raised);
            Assert.Contains(nameof(FileItem.IsShortcut), raised);
        }
        finally
        {
            File.Delete(newPath);
        }
    }

    /// <summary>
    /// A cosmetic <c>displayName</c> override passed at construction (see ADR-013 Decision 3, legacy
    /// GUID-suffix stripping) must be cleared by <see cref="FileItem.Relocate"/> — after a rename the
    /// on-disk name IS the name the user chose, so <see cref="FileItem.DisplayName"/> must reflect the
    /// new path directly rather than keep showing a stale override computed from the old path.
    /// </summary>
    [Fact]
    public void Relocate_ClearsDisplayNameOverride_DisplayNameFallsBackToNewName()
    {
        var oldPath = @"C:\fake\Report_9f3c1a2b3c4d5e6f7a8b9c0d1e2f3a4b.docx";
        var newPath = @"C:\fake\Renamed.docx";
        var item = new FileItem(oldPath, null, displayName: "Report.docx");
        Assert.Equal("Report.docx", item.DisplayName); // override active before Relocate

        item.Relocate(newPath, null);

        Assert.Equal("Renamed.docx", item.DisplayName);
        Assert.Equal(item.Name, item.DisplayName);
    }

    /// <summary>
    /// <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> are re-evaluated against the
    /// new path, not merely copied from the old one — relocating into a live directory must flip
    /// <see cref="FileItem.IsDirectory"/> to <see langword="true"/>.
    /// </summary>
    [Fact]
    public void Relocate_ToExistingDirectory_UpdatesExistsAndIsDirectory()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), $"DOrgRelocateOldFile_{Guid.NewGuid():N}.txt");
        var newDir = Path.Combine(Path.GetTempPath(), $"DOrgRelocateNewDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(newDir);

        try
        {
            var item = new FileItem(oldPath, null);
            Assert.False(item.Exists);
            Assert.False(item.IsDirectory);

            item.Relocate(newDir, null);

            Assert.True(item.Exists);
            Assert.True(item.IsDirectory);
        }
        finally
        {
            Directory.Delete(newDir);
        }
    }

    /// <summary>
    /// Relocating to a path that doesn't exist on disk must flip <see cref="FileItem.Exists"/> back
    /// to <see langword="false"/> if the old path had existed — Relocate re-checks the file system,
    /// it doesn't assume the new path is necessarily live.
    /// </summary>
    [Fact]
    public void Relocate_ToNonExistentPath_ExistsBecomesFalse()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), $"DOrgRelocateOldExists_{Guid.NewGuid():N}.txt");
        File.WriteAllText(oldPath, string.Empty);
        var newPath = Path.Combine(Path.GetTempPath(), $"DOrgRelocateGhost_{Guid.NewGuid():N}.txt");

        try
        {
            var item = new FileItem(oldPath, null);
            Assert.True(item.Exists);

            item.Relocate(newPath, null);

            Assert.False(item.Exists);
        }
        finally
        {
            File.Delete(oldPath);
        }
    }

    // ---------------------------------------------------------------------------
    // UpdateIcon (P08, file-based invalidation): re-resolves Icon in place without touching
    // Path/Name/DisplayName/Type/IsShortcut the way Relocate does.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A genuinely different icon instance is applied and raises <c>PropertyChanged(Icon)</c>, while
    /// every other derived property (Path/Name/DisplayName/Type/IsShortcut) is left untouched.
    /// </summary>
    [Fact]
    public void UpdateIcon_DifferentInstance_AppliesIconAndRaisesPropertyChanged()
    {
        var item = new FileItem(@"C:\fake\Chrome.lnk", null);
        var newIcon = new BitmapImage();

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var changed = item.UpdateIcon(newIcon);

        Assert.True(changed);
        Assert.Same(newIcon, item.Icon);
        Assert.Equal([nameof(FileItem.Icon)], raised);
        Assert.Equal(@"C:\fake\Chrome.lnk", item.Path);
        Assert.Equal("Chrome.lnk", item.Name);
    }

    /// <summary>
    /// Re-applying the same icon instance (reference equality) is a no-op: no
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> is raised.
    /// </summary>
    [Fact]
    public void UpdateIcon_SameInstance_IsNoOp()
    {
        var icon = new BitmapImage();
        var item = new FileItem(@"C:\fake\Chrome.lnk", icon);

        var raised = 0;
        item.PropertyChanged += (_, _) => raised++;

        var changed = item.UpdateIcon(icon);

        Assert.False(changed);
        Assert.Equal(0, raised);
    }

    // ---------------------------------------------------------------------------
    // CreatePending/ApplyProbe/RelocateInPlace/IsProbePending (ADR-046 D-5): an item can appear on the
    // UI thread without ever touching the file system — the real Exists/IsDirectory observation is
    // applied later, off-thread, via ApplyProbe.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="FileItem.CreatePending"/> never touches the file system: a path on a drive that
    /// cannot possibly exist neither throws nor blocks, and the item is optimistically
    /// <see cref="FileItem.Exists"/> <see langword="true"/> with <see cref="FileItem.IsProbePending"/>
    /// <see langword="true"/> until <see cref="FileItem.ApplyProbe"/> runs.
    /// </summary>
    [Fact]
    public void CreatePending_NonExistentDrive_DoesNotThrow_OptimisticallyExists_ProbePending()
    {
        var item = FileItem.CreatePending(@"Q:\nope\ghost.txt", null);

        Assert.True(item.Exists);
        Assert.True(item.IsProbePending);
        Assert.Equal(@"Q:\nope\ghost.txt", item.Path);
    }

    /// <summary>
    /// A regular (non-<see cref="FileItem.CreatePending"/>) constructor call leaves
    /// <see cref="FileItem.IsProbePending"/> <see langword="false"/> — the file-system-probing
    /// constructor already performed a real observation.
    /// </summary>
    [Fact]
    public void RegularConstructor_IsProbePendingIsFalse()
    {
        var item = new FileItem(@"C:\fake\A.txt", null);

        Assert.False(item.IsProbePending);
    }

    /// <summary>
    /// <see cref="FileItem.CreatePending"/>'s directory-ness heuristic: an extensionless path is guessed
    /// to be a directory, matching <c>IconProvider</c>'s own missing-path heuristic.
    /// </summary>
    [Fact]
    public void CreatePending_NoExtension_GuessesIsDirectoryTrue()
    {
        var item = FileItem.CreatePending(@"Q:\nope\SomeFolder", null);

        Assert.True(item.IsDirectory);
    }

    /// <summary>
    /// <see cref="FileItem.CreatePending"/>'s directory-ness heuristic: a path with an extension is
    /// guessed to be a file.
    /// </summary>
    [Fact]
    public void CreatePending_WithExtension_GuessesIsDirectoryFalse()
    {
        var item = FileItem.CreatePending(@"Q:\nope\ghost.txt", null);

        Assert.False(item.IsDirectory);
    }

    /// <summary>
    /// An explicit <c>isDirectoryHint</c> always wins over the extension heuristic — a portal that
    /// already knows an entry's directory-ness (e.g. from enumerating with attributes) shouldn't be
    /// second-guessed by <see cref="FileItem.CreatePending"/>.
    /// </summary>
    [Fact]
    public void CreatePending_IsDirectoryHint_TakesPrecedenceOverHeuristic()
    {
        // Would normally guess "file" (has an extension) — the explicit hint overrides that.
        var item = FileItem.CreatePending(@"Q:\nope\Weird.folder", null, isDirectoryHint: true);

        Assert.True(item.IsDirectory);
    }

    /// <summary>
    /// <see cref="FileItem.ApplyProbe"/> clears <see cref="FileItem.IsProbePending"/> and only raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for the values that
    /// actually flipped from their optimistic <see cref="FileItem.CreatePending"/> defaults.
    /// </summary>
    [Fact]
    public void ApplyProbe_OnlyRaisesPropertyChangedForValuesThatFlipped()
    {
        // CreatePending's optimistic defaults: Exists = true, IsDirectory = true (no extension).
        var item = FileItem.CreatePending(@"Q:\nope\SomeFolder", null);

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The real probe confirms both optimistic guesses — nothing actually changed.
        var directoryChanged = item.ApplyProbe(exists: true, isDirectory: true);

        Assert.False(item.IsProbePending);
        Assert.False(directoryChanged);
        Assert.Empty(raised);
    }

    /// <summary>
    /// <see cref="FileItem.ApplyProbe"/> raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// for <see cref="FileItem.Exists"/> when the probe contradicts the optimistic guess, and returns
    /// <see langword="true"/> when <see cref="FileItem.IsDirectory"/> also flipped.
    /// </summary>
    [Fact]
    public void ApplyProbe_ContradictsOptimisticGuess_RaisesPropertyChangedAndReturnsDirectoryChanged()
    {
        // Optimistic guess: Exists = true, IsDirectory = true (no extension). Real probe: missing.
        var item = FileItem.CreatePending(@"Q:\nope\SomeFolder", null);

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var directoryChanged = item.ApplyProbe(exists: false, isDirectory: false);

        Assert.False(item.Exists);
        Assert.False(item.IsDirectory);
        Assert.True(directoryChanged);
        Assert.Contains(nameof(FileItem.Exists), raised);
        Assert.Contains(nameof(FileItem.IsDirectory), raised);
    }

    /// <summary>
    /// A second <see cref="FileItem.ApplyProbe"/> call behaves like any other re-observation — it is not
    /// restricted to only the first, pending one.
    /// </summary>
    [Fact]
    public void ApplyProbe_CalledTwice_SecondCallStillApplies()
    {
        var item = FileItem.CreatePending(@"Q:\nope\ghost.txt", null);
        item.ApplyProbe(exists: true, isDirectory: false);
        Assert.False(item.IsProbePending);

        var directoryChanged = item.ApplyProbe(exists: false, isDirectory: false);

        Assert.False(item.Exists);
        Assert.False(directoryChanged);
    }

    /// <summary>
    /// <see cref="FileItem.RelocateInPlace"/> raises the exact same <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// set as <see cref="FileItem.Relocate"/>, without ever touching the file system: a path on a
    /// non-existent drive still ends up <see cref="FileItem.Exists"/> <see langword="true"/> (the rename
    /// service's own success is already the existence proof).
    /// </summary>
    [Fact]
    public void RelocateInPlace_UpdatesAllDerivedProperties_WithoutFileSystemAccess()
    {
        var item = new FileItem(@"Q:\nope\Old.txt", null);

        var newIcon = new BitmapImage();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.RelocateInPlace(@"Q:\nope\New.lnk", newIcon);

        Assert.Equal(@"Q:\nope\New.lnk", item.Path);
        Assert.Same(newIcon, item.Icon);
        Assert.Equal("New.lnk", item.Name);
        Assert.Equal(".lnk", item.Type);
        Assert.True(item.IsShortcut);
        Assert.True(item.Exists); // no fs access — the rename's own success is the proof

        Assert.Contains(nameof(FileItem.Path), raised);
        Assert.Contains(nameof(FileItem.Icon), raised);
        Assert.Contains(nameof(FileItem.Name), raised);
        Assert.Contains(nameof(FileItem.DisplayName), raised);
        Assert.Contains(nameof(FileItem.Type), raised);
        Assert.Contains(nameof(FileItem.IsShortcut), raised);
    }

    /// <summary>
    /// <see cref="FileItem.RelocateInPlace"/> leaves <see cref="FileItem.IsDirectory"/> untouched — a
    /// rename can never change what a path resolves to on disk beyond its name.
    /// </summary>
    [Fact]
    public void RelocateInPlace_DoesNotChangeIsDirectory()
    {
        var item = FileItem.CreatePending(@"Q:\nope\SomeFolder", null); // IsDirectory guessed true

        item.RelocateInPlace(@"Q:\nope\RenamedFolder", null);

        Assert.True(item.IsDirectory);
    }

    /// <summary>
    /// A cosmetic <c>displayName</c> override is cleared by <see cref="FileItem.RelocateInPlace"/>, same
    /// as <see cref="FileItem.Relocate"/>.
    /// </summary>
    [Fact]
    public void RelocateInPlace_ClearsDisplayNameOverride()
    {
        var item = new FileItem(@"C:\fake\Old.docx", null, displayName: "Override.docx");
        Assert.Equal("Override.docx", item.DisplayName);

        item.RelocateInPlace(@"C:\fake\New.docx", null);

        Assert.Equal("New.docx", item.DisplayName);
    }
}
