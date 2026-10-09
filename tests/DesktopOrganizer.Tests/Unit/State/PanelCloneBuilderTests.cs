using System.Windows;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.State;

/// <summary>
/// Unit tests for <see cref="PanelCloneBuilder"/> (ADR-045, «Panel clone to page», backlog 1.x §8b): the
/// user's core requirement is that a clone is fully independent from its source, so these tests focus on
/// two things — every OTHER persisted field really is copied (compared through <see cref="PanelStateMapper.ToDto"/>,
/// the same round-trip <see cref="PanelStateMapperTests"/> already trusts), and the mutable collections
/// (<see cref="ChildWindowModel.FilePaths"/>/<see cref="ChildWindowModel.FileItems"/>) are genuinely
/// separate instances, not aliases of the source's own.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCloneBuilderTests
{
    /// <summary>
    /// Verifies that every persisted field except <see cref="ChildWindowModel.Id"/>,
    /// <see cref="ChildWindowModel.PageId"/>, <see cref="ChildWindowModel.Title"/> and
    /// <see cref="ChildWindowModel.FilePaths"/> is copied verbatim from the source — compared via
    /// <see cref="PanelStateMapper.ToDto"/> on both, which covers today's AND any future persisted field
    /// with no second "which fields does a clone need" list to maintain.
    /// </summary>
    [Fact]
    public void Build_CopiesEveryPersistedFieldExceptIdPageIdTitleAndFilePaths()
    {
        var source = BuildFullyPopulatedModel();
        var cloneFilePaths = new List<string> { @"C:\clone\only.txt" };

        var clone = PanelCloneBuilder.Build(source, "work", "Cloned Title", cloneFilePaths);

        var sourceDto = PanelStateMapper.ToDto(source);
        var cloneDto = PanelStateMapper.ToDto(clone);

        // The four fields PanelCloneBuilder.Build is explicitly responsible for setting.
        Assert.NotEqual(sourceDto.Id, cloneDto.Id);
        Assert.Equal("work", clone.PageId);
        Assert.Equal("Cloned Title", clone.Title);
        Assert.Equal(cloneFilePaths, clone.FilePaths);

        // Geometry (everything but Position/Size, which the caller offsets afterward for a same-page
        // clone — PanelCloneGeometryTests covers that; Build itself must leave them at the source's own
        // values verbatim, asserted below via Position/Size directly).
        Assert.Equal(sourceDto.Geometry.MonitorDeviceId, cloneDto.Geometry.MonitorDeviceId);
        Assert.Equal(sourceDto.Geometry.WindowState, cloneDto.Geometry.WindowState);
        Assert.Equal(sourceDto.Geometry.IsRolledUp, cloneDto.Geometry.IsRolledUp);
        Assert.Equal(sourceDto.Geometry.GeometryMode, cloneDto.Geometry.GeometryMode);
        Assert.Equal(sourceDto.Geometry.WidthSpanUnits, cloneDto.Geometry.WidthSpanUnits);
        Assert.Equal(sourceDto.Geometry.HeightSpanUnits, cloneDto.Geometry.HeightSpanUnits);
        Assert.Equal(sourceDto.Geometry.SizingMode, cloneDto.Geometry.SizingMode);
        Assert.Equal(source.Position, clone.Position);
        Assert.Equal(source.Size, clone.Size);

        // Appearance.
        Assert.Equal(sourceDto.Appearance.AccentColor, cloneDto.Appearance.AccentColor);
        Assert.Equal(sourceDto.Appearance.SurfaceOpacity, cloneDto.Appearance.SurfaceOpacity);
        Assert.Equal(sourceDto.Appearance.SurfaceStyle, cloneDto.Appearance.SurfaceStyle);
        Assert.Equal(sourceDto.Appearance.TextColor, cloneDto.Appearance.TextColor);
        Assert.Equal(sourceDto.Appearance.IconSize, cloneDto.Appearance.IconSize);
        Assert.Equal(sourceDto.Appearance.IconLayoutPolicy, cloneDto.Appearance.IconLayoutPolicy);

        // Content (except FilePaths).
        Assert.Equal(sourceDto.Content.ViewMode, cloneDto.Content.ViewMode);
        Assert.Equal(sourceDto.Content.ContentFilter, cloneDto.Content.ContentFilter);
        Assert.Equal(sourceDto.Content.SortMode, cloneDto.Content.SortMode);
        Assert.Equal(sourceDto.Content.SourceFolderPath, cloneDto.Content.SourceFolderPath);

        // IsVisible is deliberately the CALLER's responsibility (WindowManager.ClonePanelToPage) — Build
        // itself copies it verbatim from the source, same as everything else in this method.
        Assert.Equal(source.IsVisible, clone.IsVisible);
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowModel.Id"/> is always a fresh <see cref="Guid"/>, never the
    /// source's own — the single most important invariant for two panels to ever be considered independent.
    /// </summary>
    [Fact]
    public void Build_AlwaysAssignsAFreshId()
    {
        var source = BuildFullyPopulatedModel();

        var clone = PanelCloneBuilder.Build(source, source.PageId, source.Title, [.. source.FilePaths]);

        Assert.NotEqual(Guid.Empty, clone.Id);
        Assert.NotEqual(source.Id, clone.Id);
    }

    /// <summary>
    /// Verifies that the clone's <see cref="ChildWindowModel.FilePaths"/> is a genuinely separate list
    /// instance from the source's own — mutating one afterward must never affect the other, in either
    /// direction.
    /// </summary>
    [Fact]
    public void Build_FilePathsIsAnIndependentInstance_MutatingEitherNeverAffectsTheOther()
    {
        var source = BuildFullyPopulatedModel();
        source.FilePaths = ["C:\\a.txt", "C:\\b.txt"];
        var cloneFilePaths = new List<string>(source.FilePaths);

        var clone = PanelCloneBuilder.Build(source, source.PageId, source.Title, cloneFilePaths);

        Assert.NotSame(source.FilePaths, clone.FilePaths);
        Assert.NotSame(cloneFilePaths, clone.FilePaths); // FromDto also re-spreads its own input list.
        Assert.Equal(cloneFilePaths, clone.FilePaths);

        clone.FilePaths.Add("C:\\clone-only.txt");
        Assert.DoesNotContain("C:\\clone-only.txt", source.FilePaths);

        source.FilePaths.Add("C:\\source-only.txt");
        Assert.DoesNotContain("C:\\source-only.txt", clone.FilePaths);
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowModel.FileItems"/> — the source's own runtime-only projection —
    /// is never copied: the clone starts with a fresh, empty, independent collection of its own (it fills
    /// itself in from its own <see cref="ChildWindowModel.FilePaths"/> once created, like any panel loaded
    /// from disk).
    /// </summary>
    [Fact]
    public void Build_FileItemsIsAlwaysAFreshEmptyIndependentCollection()
    {
        var source = BuildFullyPopulatedModel();
        source.FileItems.Add(new FileItem(@"C:\a.txt", null));

        var clone = PanelCloneBuilder.Build(source, source.PageId, source.Title, [.. source.FilePaths]);

        Assert.NotSame(source.FileItems, clone.FileItems);
        Assert.Empty(clone.FileItems);
        Assert.NotEmpty(source.FileItems); // Build must never clear the source's own projection either.
    }

    /// <summary>
    /// Verifies that a Folder Portal's <see cref="ChildWindowModel.SourceFolderPath"/> is copied
    /// verbatim — the clone binds to the SAME live folder (its own watcher is wired up by the caller once
    /// the clone is created), never a copy of the folder's contents.
    /// </summary>
    [Fact]
    public void Build_FolderPortal_SourceFolderPathCopiedVerbatim()
    {
        var source = BuildFullyPopulatedModel();
        source.SourceFolderPath = @"D:\Projects\Shared";

        var clone = PanelCloneBuilder.Build(source, source.PageId, source.Title, [.. source.FilePaths]);

        Assert.Equal(@"D:\Projects\Shared", clone.SourceFolderPath);
    }

    /// <summary>
    /// Verifies that a <see cref="PanelGeometryMode.GridAligned"/> source's span pair survives the
    /// <c>ToDto</c>/<c>FromDto</c> round trip <see cref="PanelCloneBuilder.Build"/> goes through.
    /// </summary>
    [Fact]
    public void Build_GridAlignedSpansPreserved()
    {
        var source = BuildFullyPopulatedModel();
        source.GeometryMode = PanelGeometryMode.GridAligned;
        source.WidthSpanUnits = 4;
        source.HeightSpanUnits = 3;

        var clone = PanelCloneBuilder.Build(source, source.PageId, source.Title, [.. source.FilePaths]);

        Assert.Equal(PanelGeometryMode.GridAligned, clone.GeometryMode);
        Assert.Equal(4, clone.WidthSpanUnits);
        Assert.Equal(3, clone.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that a clone is a new entity: unknown fields and preserved unknown enum strings of the source are not
    /// carried over (ADR-024 Amendment "Additive revisions"), while the source itself is left untouched.
    /// </summary>
    [Fact]
    public void PanelCloneBuilder_DropsPersistedExtras()
    {
        var sourceDto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        sourceDto.Extras = new Dictionary<string, System.Text.Json.JsonElement> { ["futureKey"] = System.Text.Json.JsonSerializer.SerializeToElement("x") };
        sourceDto.Geometry.Extras = new Dictionary<string, System.Text.Json.JsonElement> { ["futureGeometry"] = System.Text.Json.JsonSerializer.SerializeToElement(1) };
        sourceDto.Appearance.SurfaceStyle = "Mica";
        var source = PanelStateMapper.FromDto(sourceDto);
        Assert.NotNull(source.PersistedExtras);

        var clone = PanelCloneBuilder.Build(source, source.PageId, "Clone", [.. source.FilePaths]);

        Assert.Null(clone.PersistedExtras);
        var cloneDto = PanelStateMapper.ToDto(clone);
        Assert.Null(cloneDto.Extras);
        Assert.Null(cloneDto.Geometry.Extras);
        Assert.Equal("Ambient", cloneDto.Appearance.SurfaceStyle);
        Assert.NotNull(source.PersistedExtras);
        Assert.Equal("Mica", PanelStateMapper.ToDto(source).Appearance.SurfaceStyle);
    }

    private static ChildWindowModel BuildFullyPopulatedModel() => new()
    {
        Id = Guid.NewGuid(),
        Title = "My Panel",
        AccentColor = "#101010",
        SurfaceOpacity = 0.55,
        SurfaceStyle = PanelSurfaceStyle.Glass,
        TextColor = "#EEEEEE",
        IconSize = 40,
        ViewMode = PanelViewMode.Grid,
        ContentFilter = PanelContentFilter.FoldersOnly,
        SortMode = PanelSortMode.Name,
        MonitorDeviceId = "\\\\.\\DISPLAY2",
        Position = new Point(12, 34),
        Size = new Size(320, 240),
        IsVisible = false,
        WindowState = WindowState.Maximized,
        IsRolledUp = true,
        PageId = "work",
        SourceFolderPath = "D:\\Projects",
        FilePaths = ["D:\\Projects\\a.txt", "D:\\Projects\\b.txt"],
        GeometryMode = PanelGeometryMode.LegacyFreeform,
        SizingMode = PanelSizingMode.AutoContent,
        IconLayoutPolicy = IconLayoutPolicy.FixedGap,
    };
}
