using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ContentFilter;

// ---------------------------------------------------------------------------
// Test-only ViewModel stub that mirrors ChildWindowViewModel's filter logic
// without any WPF dependency (no DataTemplate, no Application.Current).
// ---------------------------------------------------------------------------

/// <summary>
/// WPF-free stub of <see cref="BaseViewModel"/> that mirrors <c>ChildWindowViewModel.ApplyFilterAndSort</c>'s
/// filter predicate logic, so filter behaviour can be exercised without a <c>DataTemplate</c> or
/// <c>Application.Current</c>.
/// </summary>
internal sealed class FilterViewModel : BaseViewModel
{
    private ChildWindowModel _model = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">The icon provider forwarded to the base <see cref="BaseViewModel"/>.</param>
    public FilterViewModel(IIconProvider iconProvider)
        : base(iconProvider)
    {
    }

    /// <summary>
    /// Attaches the given model as the source of truth and rebuilds <see cref="BaseViewModel.FileItems"/>
    /// using the model's current <see cref="ChildWindowModel.ContentFilter"/>.
    /// </summary>
    /// <param name="model">The panel model to load and subscribe to.</param>
    public void LoadModel(ChildWindowModel model)
    {
        _model = model;
        _model.PropertyChanged += OnModelPropertyChanged;
        ApplyFilter();
    }

    /// <summary>
    /// Re-applies the active content filter whenever the underlying source collection changes.
    /// </summary>
    protected override void OnSourceChanged() => ApplyFilter();

    private static Func<FileItem, bool> BuildFilterPredicate(PanelContentFilter filter) =>
        filter switch
        {
            PanelContentFilter.HideAll => _ => false,
            PanelContentFilter.HideShortcuts => item => !item.IsShortcut,
            PanelContentFilter.FoldersOnly => item => item.IsDirectory,
            _ => _ => true,
        };

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChildWindowModel.ContentFilter))
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        var predicate = BuildFilterPredicate(_model.ContentFilter);
        FileItems.Clear();
        foreach (var item in AllItems.Where(predicate))
            FileItems.Add(item);
    }
}

// ---------------------------------------------------------------------------
// 1. FileItem (Core) — no external dependencies
// ---------------------------------------------------------------------------

/// <summary>
/// Unit tests for <see cref="FileItem.IsShortcut"/> and <see cref="FileItem.IsDirectory"/>.
/// </summary>
[Trait(UnitTests, "Core")]
public class FileItemContentFilterTests
{
    // ── IsShortcut ───────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a path ending in <c>.lnk</c> is recognized as a shortcut.
    /// </summary>
    [Fact]
    public void IsShortcut_LnkExtension_ReturnsTrue()
    {
        var item = new FileItem(@"C:\Links\MyApp.lnk", null);
        Assert.True(item.IsShortcut);
    }

    /// <summary>
    /// Verifies that an executable path is not recognized as a shortcut.
    /// </summary>
    [Fact]
    public void IsShortcut_ExeExtension_ReturnsFalse()
    {
        var item = new FileItem(@"C:\Apps\MyApp.exe", null);
        Assert.False(item.IsShortcut);
    }

    /// <summary>
    /// Verifies that a plain text file path is not recognized as a shortcut.
    /// </summary>
    [Fact]
    public void IsShortcut_TxtExtension_ReturnsFalse()
    {
        var item = new FileItem(@"C:\Docs\readme.txt", null);
        Assert.False(item.IsShortcut);
    }

    /// <summary>
    /// Verifies that shortcut detection is case-insensitive for an all-uppercase extension.
    /// </summary>
    [Fact]
    public void IsShortcut_UpperCaseLnk_ReturnsTrue()
    {
        var item = new FileItem(@"C:\Links\MyApp.LNK", null);
        Assert.True(item.IsShortcut);
    }

    /// <summary>
    /// Verifies that shortcut detection is case-insensitive for a mixed-case extension.
    /// </summary>
    [Fact]
    public void IsShortcut_MixedCaseLnk_ReturnsTrue()
    {
        var item = new FileItem(@"C:\Links\MyApp.Lnk", null);
        Assert.True(item.IsShortcut);
    }

    /// <summary>
    /// Verifies that a path without any extension is not recognized as a shortcut.
    /// </summary>
    [Fact]
    public void IsShortcut_NoExtension_ReturnsFalse()
    {
        var item = new FileItem(@"C:\Tools\mytool", null);
        Assert.False(item.IsShortcut);
    }

    // ── IsDirectory ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that an existing temporary directory is recognized as a directory.
    /// </summary>
    [Fact]
    public void IsDirectory_ExistingTempDir_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DOrgTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var item = new FileItem(dir, null);
            Assert.True(item.IsDirectory);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Verifies that a path that does not exist on disk is not recognized as a directory.
    /// </summary>
    [Fact]
    public void IsDirectory_NonExistentPath_ReturnsFalse()
    {
        var item = new FileItem(@"C:\Definitely\Does\Not\Exist\folder", null);
        Assert.False(item.IsDirectory);
    }

    /// <summary>
    /// Verifies that an existing regular file is not recognized as a directory.
    /// </summary>
    [Fact]
    public void IsDirectory_FilePath_ReturnsFalse()
    {
        var item = new FileItem(@"C:\Windows\System32\notepad.exe", null);

        // notepad.exe exists but is a file, not a directory
        Assert.False(item.IsDirectory);
    }
}

// ---------------------------------------------------------------------------
// 2. ChildWindowModel — ContentFilter INPC and default value
// ---------------------------------------------------------------------------

/// <summary>
/// Unit tests for <see cref="ChildWindowModel.ContentFilter"/> property.
/// </summary>
[Trait(UnitTests, "UI")]
public class ChildWindowModelContentFilterTests
{
    /// <summary>
    /// Verifies that a freshly constructed <see cref="ChildWindowModel"/> defaults to <see cref="PanelContentFilter.ShowAll"/>.
    /// </summary>
    [Fact]
    public void ContentFilter_DefaultValue_IsShowAll()
    {
        var model = new ChildWindowModel();
        Assert.Equal(PanelContentFilter.ShowAll, model.ContentFilter);
    }

    /// <summary>
    /// Verifies that assigning a different <see cref="PanelContentFilter"/> value raises <see cref="INotifyPropertyChanged.PropertyChanged"/>.
    /// </summary>
    [Fact]
    public void ContentFilter_WhenChanged_RaisesPropertyChanged()
    {
        var model = new ChildWindowModel();
        string? raisedProperty = null;
        model.PropertyChanged += (_, e) => raisedProperty = e.PropertyName;

        model.ContentFilter = PanelContentFilter.HideShortcuts;

        Assert.Equal(nameof(ChildWindowModel.ContentFilter), raisedProperty);
    }

    /// <summary>
    /// Verifies that re-assigning the same <see cref="PanelContentFilter"/> value does not raise <see cref="INotifyPropertyChanged.PropertyChanged"/>.
    /// </summary>
    [Fact]
    public void ContentFilter_WhenSetToSameValue_DoesNotRaisePropertyChanged()
    {
        var model = new ChildWindowModel();
        int raised = 0;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChildWindowModel.ContentFilter)) raised++;
        };

        model.ContentFilter = PanelContentFilter.ShowAll; // same as default

        Assert.Equal(0, raised);
    }
}

// ---------------------------------------------------------------------------
// 3. ChildWindowViewModel filter logic (via stub, no WPF)
// ---------------------------------------------------------------------------

/// <summary>
/// Unit tests for filter predicate application in the panel ViewModel.
/// Uses <see cref="FilterViewModel"/> — a WPF-free stub that mirrors the real
/// <c>ChildWindowViewModel.ApplyFilterAndSort</c> logic.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelFilterViewModelTests
{
    // Paths used across tests
    private const string ShortcutPath = @"C:\fake\MyShortcut.lnk";
    private const string ExePath = @"C:\fake\MyApp.exe";
    private const string TxtPath = @"C:\fake\readme.txt";

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), $"DOrgFilter_{Guid.NewGuid():N}");

    private static BaseViewModel BuildVm(PanelContentFilter filter = PanelContentFilter.ShowAll)
    {
        Directory.CreateDirectory(TempDir);
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((ImageSource)null!);

        var model = new ChildWindowModel { ContentFilter = filter };
        var vm = new FilterViewModel(iconMock.Object);
        vm.LoadModel(model);
        return vm;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelFilterViewModelTests"/> class.
    /// </summary>
    public PanelFilterViewModelTests()
    {
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="PanelFilterViewModelTests"/> class.
    /// Removes the shared temporary directory used by the fixture.
    /// </summary>
    ~PanelFilterViewModelTests()
    {
        if (Directory.Exists(TempDir))
            Directory.Delete(TempDir, recursive: true);
    }

    // ── ShowAll ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <see cref="PanelContentFilter.ShowAll"/> filter keeps every added item visible.
    /// </summary>
    [Fact]
    public void ShowAll_AllItemsVisible()
    {
        var vm = BuildVm(PanelContentFilter.ShowAll);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TempDir });

        Assert.Equal(3, vm.FileItems.Count);
    }

    // ── HideAll ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <see cref="PanelContentFilter.HideAll"/> filter leaves <see cref="BaseViewModel.FileItems"/> empty.
    /// </summary>
    [Fact]
    public void HideAll_FileItemsIsEmpty()
    {
        var vm = BuildVm(PanelContentFilter.HideAll);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TempDir });

        Assert.Empty(vm.FileItems);
    }

    // ── HideShortcuts ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <see cref="PanelContentFilter.HideShortcuts"/> filter excludes shortcut items.
    /// </summary>
    [Fact]
    public void HideShortcuts_ShortcutsNotVisible()
    {
        var vm = BuildVm(PanelContentFilter.HideShortcuts);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TxtPath });

        Assert.DoesNotContain(vm.FileItems, f => f.IsShortcut);
    }

    /// <summary>
    /// Verifies that the <see cref="PanelContentFilter.HideShortcuts"/> filter keeps non-shortcut items visible.
    /// </summary>
    [Fact]
    public void HideShortcuts_NonShortcutsRemainVisible()
    {
        var vm = BuildVm(PanelContentFilter.HideShortcuts);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TxtPath });

        Assert.Contains(vm.FileItems, f => f.Path == ExePath);
        Assert.Contains(vm.FileItems, f => f.Path == TxtPath);
    }

    // ── FoldersOnly ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <see cref="PanelContentFilter.FoldersOnly"/> filter shows only directory items.
    /// </summary>
    [Fact]
    public void FoldersOnly_OnlyDirectoriesVisible()
    {
        var vm = BuildVm(PanelContentFilter.FoldersOnly);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TempDir });

        Assert.All(vm.FileItems, f => Assert.True(f.IsDirectory));
    }

    /// <summary>
    /// Verifies that the <see cref="PanelContentFilter.FoldersOnly"/> filter excludes non-directory items.
    /// </summary>
    [Fact]
    public void FoldersOnly_FilesNotVisible()
    {
        var vm = BuildVm(PanelContentFilter.FoldersOnly);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TempDir });

        Assert.DoesNotContain(vm.FileItems, f => f.Path == ShortcutPath);
        Assert.DoesNotContain(vm.FileItems, f => f.Path == ExePath);
    }

    // ── Regression: ShowAll → HideShortcuts → ShowAll restores items ─────────

    /// <summary>
    /// Verifies that switching back to <see cref="PanelContentFilter.ShowAll"/> after
    /// <see cref="PanelContentFilter.HideShortcuts"/> restores the previously hidden shortcut item.
    /// </summary>
    [Fact]
    public void ShowAll_AfterHideShortcuts_RestoredItemsVisible()
    {
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((ImageSource)null!);

        var model = new ChildWindowModel { ContentFilter = PanelContentFilter.ShowAll };
        var vm = new FilterViewModel(iconMock.Object);
        vm.LoadModel(model);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TxtPath });

        // Verify all visible initially
        Assert.Equal(3, vm.FileItems.Count);

        // Apply HideShortcuts
        model.ContentFilter = PanelContentFilter.HideShortcuts;
        Assert.DoesNotContain(vm.FileItems, f => f.IsShortcut);
        Assert.Equal(2, vm.FileItems.Count);

        // Restore ShowAll — shortcut must reappear
        model.ContentFilter = PanelContentFilter.ShowAll;
        Assert.Equal(3, vm.FileItems.Count);
        Assert.Contains(vm.FileItems, f => f.Path == ShortcutPath);
    }

    // ── Model.ContentFilter change → FileItems updates automatically ─────────

    /// <summary>
    /// Verifies that changing <see cref="ChildWindowModel.ContentFilter"/> directly on the model
    /// automatically re-applies the filter and updates <see cref="BaseViewModel.FileItems"/>.
    /// </summary>
    [Fact]
    public void ModelContentFilterChange_TriggersFileItemsUpdate()
    {
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((ImageSource)null!);

        var model = new ChildWindowModel { ContentFilter = PanelContentFilter.ShowAll };
        var vm = new FilterViewModel(iconMock.Object);
        vm.LoadModel(model);
        vm.AddFiles(new[] { ShortcutPath, ExePath });

        Assert.Equal(2, vm.FileItems.Count);

        // Change filter on model — ViewModel must react
        model.ContentFilter = PanelContentFilter.HideAll;

        Assert.Empty(vm.FileItems);
    }
}

// ---------------------------------------------------------------------------
// 4. Regression: SyncToModel must read AllItems, not FileItems
// ---------------------------------------------------------------------------

/// <summary>
/// Regression tests verifying that persistence (SyncToModel equivalent) always
/// reads <see cref="BaseViewModel.AllItems"/> — the full unfiltered list — and
/// never the filtered <see cref="BaseViewModel.FileItems"/> projection.
/// Fixes the data-loss bug where active filters caused hidden items to be dropped
/// from the saved state.
/// </summary>
[Trait(UnitTests, "UI")]
public class SyncToModelPersistenceRegressionTests
{
    private const string ShortcutPath = @"C:\fake\MyShortcut.lnk";
    private const string ExePath = @"C:\fake\MyApp.exe";
    private const string TxtPath = @"C:\fake\readme.txt";

    private static readonly string TempDir =
        Path.Combine(Path.GetTempPath(), $"DOrgSync_{Guid.NewGuid():N}");

    /// <summary>
    /// Finalizes an instance of the <see cref="SyncToModelPersistenceRegressionTests"/> class.
    /// Removes the shared temporary directory used by the fixture.
    /// </summary>
    ~SyncToModelPersistenceRegressionTests()
    {
        if (Directory.Exists(TempDir))
            Directory.Delete(TempDir, recursive: true);
    }

    /// <summary>
    /// AllItems must contain all paths even when FoldersOnly filter hides non-directories.
    /// </summary>
    [Fact]
    public void AllItems_WithFoldersOnlyFilter_ContainsAllPaths()
    {
        Directory.CreateDirectory(TempDir);
        var vm = BuildVm(PanelContentFilter.FoldersOnly);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TempDir });

        // FileItems (filtered) shows only the directory
        Assert.Single(vm.FileItems);

        // AllItems (persistence source) must contain all three
        Assert.Equal(3, vm.AllItems.Count);
        Assert.Contains(vm.AllItems, f => f.Path == ShortcutPath);
        Assert.Contains(vm.AllItems, f => f.Path == ExePath);
        Assert.Contains(vm.AllItems, f => f.Path == TempDir);
    }

    /// <summary>
    /// AllItems must contain all paths when HideAll filter makes FileItems empty.
    /// </summary>
    [Fact]
    public void AllItems_WithHideAllFilter_ContainsAllPaths()
    {
        var vm = BuildVm(PanelContentFilter.HideAll);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TxtPath });

        // FileItems is empty due to filter
        Assert.Empty(vm.FileItems);

        // AllItems must still hold all three — no data loss on save
        Assert.Equal(3, vm.AllItems.Count);
    }

    /// <summary>
    /// AllItems must contain all paths when HideShortcuts filter is active.
    /// </summary>
    [Fact]
    public void AllItems_WithHideShortcutsFilter_ContainsAllPaths()
    {
        var vm = BuildVm(PanelContentFilter.HideShortcuts);
        vm.AddFiles(new[] { ShortcutPath, ExePath, TxtPath });

        // Shortcut is hidden in FileItems
        Assert.Equal(2, vm.FileItems.Count);

        // AllItems must still hold the shortcut
        Assert.Equal(3, vm.AllItems.Count);
        Assert.Contains(vm.AllItems, f => f.Path == ShortcutPath);
    }

    /// <summary>
    /// Simulates the exact SyncToModel read: AllItems.Select(f => f.Path) must
    /// equal the original set of added paths regardless of the active filter.
    /// </summary>
    [Fact]
    public void SyncToModel_ViaAllItems_PreservesAllPathsUnderFoldersOnlyFilter()
    {
        Directory.CreateDirectory(TempDir);
        var vm = BuildVm(PanelContentFilter.FoldersOnly);
        var added = new[] { ShortcutPath, ExePath, TempDir };
        vm.AddFiles(added);

        // Simulate exactly what ChildWindow.SyncToModel now does:
        var persisted = vm.AllItems.Select(f => f.Path).ToList();

        Assert.Equal(added.Length, persisted.Count);
        foreach (var path in added)
            Assert.Contains(path, persisted);
    }

    private static BaseViewModel BuildVm(PanelContentFilter filter)
    {
        Directory.CreateDirectory(TempDir);
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((ImageSource)null!);

        var model = new ChildWindowModel { ContentFilter = filter };
        var vm = new FilterViewModel(iconMock.Object);
        vm.LoadModel(model);
        return vm;
    }

    private static (BaseViewModel vm, ChildWindowModel model) BuildVmWithModel(PanelContentFilter filter)
    {
        Directory.CreateDirectory(TempDir);
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((ImageSource)null!);

        var model = new ChildWindowModel { ContentFilter = filter };
        var fvm = new FilterViewModel(iconMock.Object);
        fvm.LoadModel(model);
        return (fvm, model);
    }
}
