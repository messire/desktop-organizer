using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

// ---------------------------------------------------------------------------
// WPF-free stub exposing BaseViewModel's `protected` (not `protected internal`) mutation surface
// for direct testing. AddFiles/RemoveFileByPath/ReplaceAll are `protected internal` and already
// callable directly on a BaseViewModel reference from this assembly (see AssemblyInfo.cs
// InternalsVisibleTo), mirroring Unit/ContentFilter/ContentFilterTests.cs's FilterViewModel. RemoveFile
// is plain `protected`, so it needs this tiny subclass wrapper.
// ---------------------------------------------------------------------------
file sealed class MutableViewModel(IIconProvider iconProvider) : BaseViewModel(iconProvider)
{
    public void RemoveFileItem(FileItem item) => RemoveFile(item);
}

/// <summary>
/// Unit tests for Phase 7 Block D (docs/PLANS/phase-7-performance-hardening.md — audit findings 5, 6):
/// O(1) duplicate detection in <see cref="BaseViewModel.AddFiles"/>/<see cref="BaseViewModel.ReplaceAll"/>
/// and single-notification batch rebuilds of <see cref="BaseViewModel.FileItems"/> via
/// <see cref="ObservableRangeCollection{T}"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class BaseViewModelCollectionTests
{
    private const string PathA = @"C:\fake\A.txt";
    private const string PathB = @"C:\fake\B.txt";
    private const string PathC = @"C:\fake\C.txt";

    private static BaseViewModel BuildVm()
    {
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((System.Windows.Media.ImageSource?)null);
        return new MutableViewModel(iconMock.Object);
    }

    // ── D1: O(1) dedup index stays consistent with _allItems across all mutations ─────────────────

    [Fact]
    public void AddFiles_ExactDuplicatePath_AddedOnlyOnce()
    {
        var vm = BuildVm();

        vm.AddFiles([PathA, PathA]);

        Assert.Single(vm.AllItems);
    }

    [Fact]
    public void AddFiles_DifferingCasing_TreatedAsSameEntry()
    {
        // Windows paths are case-insensitive; the dedup index must match RemoveFileByPath's
        // pre-existing OrdinalIgnoreCase comparer (previously AddFiles compared ordinal/case-sensitive
        // while RemoveFileByPath compared case-insensitive — an inconsistency fixed by Block D).
        var vm = BuildVm();

        vm.AddFiles([@"C:\A.txt"]);
        vm.AddFiles([@"c:\a.txt"]);

        Assert.Single(vm.AllItems);
    }

    [Fact]
    public void AddFiles_ThenRemoveByPath_ThenReAdd_IndexAllowsReAdd()
    {
        // Removing must clear the path from the dedup index too, or a re-add of the same path would
        // be silently swallowed as a "duplicate" forever.
        var vm = BuildVm();

        vm.AddFiles([PathA]);
        vm.RemoveFileByPath(PathA);
        vm.AddFiles([PathA]);

        Assert.Single(vm.AllItems);
    }

    [Fact]
    public void RemoveFile_ThenReAddSamePath_IndexAllowsReAdd()
    {
        var vm = (MutableViewModel)BuildVm();

        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        vm.RemoveFileItem(item);
        vm.AddFiles([PathA]);

        Assert.Single(vm.AllItems);
    }

    [Fact]
    public void ReplaceAll_ClearsIndex_AllowsPreviousPathsToBeReAdded()
    {
        var vm = BuildVm();

        vm.AddFiles([PathA, PathB]);
        vm.ReplaceAll([PathA]); // PathB dropped, PathA re-submitted via the same (cleared) index

        var item = Assert.Single(vm.AllItems);
        Assert.Equal(PathA, item.Path);
    }

    [Fact]
    public void ReplaceAll_WithDuplicatePathsInInput_AddsEachPathOnce()
    {
        var vm = BuildVm();

        vm.ReplaceAll([PathA, PathA, PathB]);

        Assert.Equal(2, vm.AllItems.Count);
    }

    // ── D2: batch notification — exactly one CollectionChanged / FileCountText per rebuild ─────────

    [Fact]
    public void OnSourceChanged_FullRebuild_RaisesExactlyOneCollectionChangedReset()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.AddFiles([PathC]); // triggers OnSourceChanged -> full rebuild

        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, raised.Action);
    }

    [Fact]
    public void OnSourceChanged_FullRebuild_RaisesFileCountTextExactlyOnce()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA]);

        var raisedCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BaseViewModel.FileCountText)) raisedCount++;
        };

        vm.AddFiles([PathB, PathC]);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void ReplaceAll_FullRebuild_RaisesExactlyOneCollectionChangedReset()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.ReplaceAll([PathC]);

        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, raised.Action);
    }

    [Fact]
    public void FileItems_AfterRebuild_ContainsProjectedItems()
    {
        // Sanity check that the batched Reset actually carries the new contents (not left stale).
        var vm = BuildVm();
        vm.AddFiles([PathA]);
        vm.AddFiles([PathB, PathC]);

        Assert.Equal(3, vm.FileItems.Count);
        Assert.Contains(vm.FileItems, f => f.Path == PathA);
        Assert.Contains(vm.FileItems, f => f.Path == PathB);
        Assert.Contains(vm.FileItems, f => f.Path == PathC);
    }

    // ── D1 (early-exit optimization): AddFiles with nothing new added skips the rebuild ─────────────

    [Fact]
    public void AddFiles_AllPathsAlreadyTracked_DoesNotRaiseCollectionChanged()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);
        var raisedCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BaseViewModel.FileCountText)) raisedCount++;
        };

        vm.AddFiles([PathA, PathB]); // both already present — a no-op re-sync

        Assert.Empty(events);
        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void AddFiles_EmptyArray_DoesNotRaiseCollectionChanged()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.AddFiles([]);

        Assert.Empty(events);
    }

    [Fact]
    public void ReplaceAll_ToEmpty_StillClearsFileItems()
    {
        // Regression guard: ReplaceAll must always rebuild FileItems even when the new set is empty
        // (AddFiles' own early-exit must not suppress this — Folder Portal folder emptied out).
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);
        Assert.NotEmpty(vm.FileItems);

        vm.ReplaceAll([]);

        Assert.Empty(vm.AllItems);
        Assert.Empty(vm.FileItems);
    }

    // ── Scale smoke test (non-gating: generous threshold, documents the O(n) fix) ───────────────────

    [Fact]
    public void ReplaceAll_FiveThousandPaths_CompletesWithinGenerousThreshold()
    {
        var vm = BuildVm();
        var paths = Enumerable.Range(0, 5000).Select(i => $@"C:\fake\file_{i}.txt").ToArray();

        var stopwatch = Stopwatch.StartNew();
        vm.ReplaceAll(paths);
        stopwatch.Stop();

        Assert.Equal(5000, vm.AllItems.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"ReplaceAll(5000 paths) took {stopwatch.Elapsed} — expected the O(n) index-based dedup to keep this well under 2s.");
    }
}
