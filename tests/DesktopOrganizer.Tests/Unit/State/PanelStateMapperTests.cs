using System.Text.Json;
using System.Windows;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.State.Dto;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.State;

/// <summary>
/// Round-trip tests for <see cref="PanelStateMapper"/> (ADR-024 §2.3, C6): every persisted
/// <see cref="ChildWindowModel"/> property must survive <c>ToDto</c> -&gt; <c>FromDto</c> unchanged —
/// except <see cref="ChildWindowModel.FileItems"/>, which must stay untouched (it is a runtime-only
/// projection, already <c>[JsonIgnore]</c> on the model itself).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelStateMapperTests
{
    /// <summary>
    /// Verifies that every persisted <see cref="ChildWindowModel"/> property survives a
    /// <see cref="PanelStateMapper.ToDto"/> -&gt; <see cref="PanelStateMapper.FromDto"/> round trip unchanged.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesEveryPersistedProperty()
    {
        var original = BuildFullyPopulatedModel();

        var roundTripped = PanelStateMapper.FromDto(PanelStateMapper.ToDto(original));

        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.Title, roundTripped.Title);
        Assert.Equal(original.AccentColor, roundTripped.AccentColor);
        Assert.Equal(original.SurfaceOpacity, roundTripped.SurfaceOpacity);
        Assert.Equal(original.SurfaceStyle, roundTripped.SurfaceStyle);
        Assert.Equal(original.TextColor, roundTripped.TextColor);
        Assert.Equal(original.IconSize, roundTripped.IconSize);
        Assert.Equal(original.ViewMode, roundTripped.ViewMode);
        Assert.Equal(original.ContentFilter, roundTripped.ContentFilter);
        Assert.Equal(original.SortMode, roundTripped.SortMode);
        Assert.Equal(original.MonitorDeviceId, roundTripped.MonitorDeviceId);
        Assert.Equal(original.Position, roundTripped.Position);
        Assert.Equal(original.Size, roundTripped.Size);
        Assert.Equal(original.IsVisible, roundTripped.IsVisible);
        Assert.Equal(original.WindowState, roundTripped.WindowState);
        Assert.Equal(original.IsRolledUp, roundTripped.IsRolledUp);
        Assert.Equal(original.PageId, roundTripped.PageId);
        Assert.Equal(original.SourceFolderPath, roundTripped.SourceFolderPath);
        Assert.Equal(original.FilePaths, roundTripped.FilePaths);
        Assert.Equal(original.GeometryMode, roundTripped.GeometryMode);
        Assert.Equal(original.WidthSpanUnits, roundTripped.WidthSpanUnits);
        Assert.Equal(original.HeightSpanUnits, roundTripped.HeightSpanUnits);
        Assert.Equal(original.SizingMode, roundTripped.SizingMode);
        Assert.Equal(original.IconLayoutPolicy, roundTripped.IconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that a <see cref="PanelGeometryMode.LegacyFreeform"/> panel with null span units keeps
    /// both span units null across a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_LegacyFreeformGeometryMode_NullSpanUnitsStayNull()
    {
        var original = BuildFullyPopulatedModel();
        original.GeometryMode = PanelGeometryMode.LegacyFreeform;
        original.WidthSpanUnits = null;
        original.HeightSpanUnits = null;

        var roundTripped = PanelStateMapper.FromDto(PanelStateMapper.ToDto(original));

        Assert.Equal(PanelGeometryMode.LegacyFreeform, roundTripped.GeometryMode);
        Assert.Null(roundTripped.WidthSpanUnits);
        Assert.Null(roundTripped.HeightSpanUnits);
    }

    // ── MAJOR-1(a) fix-cycle 1 (docs/PLANS/workspace-grid-migration.md §10.5): a GridAligned panel
    // without a valid span pair must never round-trip as GridAligned. ─────────────────────────────────

    /// <summary>
    /// Verifies that a panel switched back to <see cref="PanelGeometryMode.LegacyFreeform"/> without its
    /// stale grid span units being cleared by the caller still writes null spans to the DTO, so a
    /// GridAligned panel without a valid span pair never round-trips as GridAligned (MAJOR-1(a), fix-cycle 1).
    /// </summary>
    [Fact]
    public void ToDto_LegacyFreeformWithStaleSpans_WritesNullSpans()
    {
        // A panel that was GridAligned and got switched back to LegacyFreeform without the spans being
        // explicitly cleared (today's model setters don't clear them for the caller) must not persist
        // the stale span pair — see the MINOR fix on PanelStateMapper.ToDto.
        var original = BuildFullyPopulatedModel();
        original.GeometryMode = PanelGeometryMode.LegacyFreeform;
        original.WidthSpanUnits = 20;
        original.HeightSpanUnits = 15;

        var dto = PanelStateMapper.ToDto(original);

        Assert.Null(dto.Geometry.WidthSpanUnits);
        Assert.Null(dto.Geometry.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that a DTO claiming <see cref="PanelGeometryMode.GridAligned"/> with null span units is
    /// normalized to <see cref="PanelGeometryMode.LegacyFreeform"/> on load.
    /// </summary>
    [Fact]
    public void FromDto_GridAlignedWithNullSpans_NormalizesToLegacyFreeform()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.GeometryMode = nameof(PanelGeometryMode.GridAligned);
        dto.Geometry.WidthSpanUnits = null;
        dto.Geometry.HeightSpanUnits = null;

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
        Assert.Null(model.WidthSpanUnits);
        Assert.Null(model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that a DTO claiming <see cref="PanelGeometryMode.GridAligned"/> with a zero or negative
    /// width or height span unit is normalized to <see cref="PanelGeometryMode.LegacyFreeform"/> on load.
    /// </summary>
    /// <param name="width">The width span units value under test.</param>
    /// <param name="height">The height span units value under test.</param>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public void FromDto_GridAlignedWithNonPositiveSpan_NormalizesToLegacyFreeform(int width, int height)
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.GeometryMode = nameof(PanelGeometryMode.GridAligned);
        dto.Geometry.WidthSpanUnits = width;
        dto.Geometry.HeightSpanUnits = height;

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
        Assert.Null(model.WidthSpanUnits);
        Assert.Null(model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that a DTO claiming <see cref="PanelGeometryMode.GridAligned"/> with a valid positive
    /// span pair stays GridAligned on load, with the spans preserved.
    /// </summary>
    [Fact]
    public void FromDto_GridAlignedWithValidSpans_StaysGridAligned()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.GeometryMode = nameof(PanelGeometryMode.GridAligned);
        dto.Geometry.WidthSpanUnits = 20;
        dto.Geometry.HeightSpanUnits = 15;

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(PanelGeometryMode.GridAligned, model.GeometryMode);
        Assert.Equal(20, model.WidthSpanUnits);
        Assert.Equal(15, model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that a geometry mode string not known to this build falls back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> instead of throwing.
    /// </summary>
    [Fact]
    public void FromDto_UnrecognisedGeometryModeString_FallsBackToLegacyFreeform()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.GeometryMode = "SomeFutureModeThisBuildDoesNotKnow";

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
    }

    /// <summary>
    /// Verifies that a sizing mode string not known to this build falls back to
    /// <see cref="PanelSizingMode.Manual"/> instead of throwing.
    /// </summary>
    [Fact]
    public void FromDto_UnrecognisedSizingModeString_FallsBackToManual()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.SizingMode = "not-a-real-value";

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(PanelSizingMode.Manual, model.SizingMode);
    }

    /// <summary>
    /// Verifies that an icon layout policy string not known to this build falls back to
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> instead of throwing.
    /// </summary>
    [Fact]
    public void FromDto_UnrecognisedIconLayoutPolicyString_FallsBackToLegacyStretch()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Appearance.IconLayoutPolicy = "not-a-real-value";

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(IconLayoutPolicy.LegacyStretch, model.IconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that a null <see cref="ChildWindowModel.SourceFolderPath"/> stays null across a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_NullSourceFolderPath_StaysNull()
    {
        var original = BuildFullyPopulatedModel();
        original.SourceFolderPath = null;

        var roundTripped = PanelStateMapper.FromDto(PanelStateMapper.ToDto(original));

        Assert.Null(roundTripped.SourceFolderPath);
    }

    /// <summary>
    /// Verifies that a null <see cref="ChildWindowModel.MonitorDeviceId"/> stays null across a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_NullMonitorDeviceId_StaysNull()
    {
        var original = BuildFullyPopulatedModel();
        original.MonitorDeviceId = null;

        var roundTripped = PanelStateMapper.FromDto(PanelStateMapper.ToDto(original));

        Assert.Null(roundTripped.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that an empty <see cref="ChildWindowModel.FilePaths"/> collection stays empty across a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_EmptyFilePaths_StaysEmpty()
    {
        var original = BuildFullyPopulatedModel();
        original.FilePaths = [];

        var roundTripped = PanelStateMapper.FromDto(PanelStateMapper.ToDto(original));

        Assert.Empty(roundTripped.FilePaths);
    }

    /// <summary>
    /// Verifies that <see cref="PanelStateMapper.ToDto"/> leaves the model's runtime-only
    /// <see cref="ChildWindowModel.FileItems"/> collection completely untouched, since it is
    /// <c>[JsonIgnore]</c> on the model and has no counterpart on the DTO.
    /// </summary>
    [Fact]
    public void ToDto_DoesNotTouchFileItems()
    {
        // FileItems is [JsonIgnore] on ChildWindowModel itself — this mapper must not reach for it at
        // all; ToDto's return type (PanelStateDto) has no such property in the first place, so this
        // is really asserting the model's own FileItems collection is left completely alone.
        var original = BuildFullyPopulatedModel();
        original.FileItems.Add(new FileItem("D:\\Projects\\a.txt", icon: null));

        PanelStateMapper.ToDto(original);

        Assert.Single(original.FileItems);
    }

    /// <summary>
    /// Verifies that <see cref="PanelStateMapper.FromDto"/> never populates
    /// <see cref="ChildWindowModel.FileItems"/>, since it is a runtime-only projection with no DTO source.
    /// </summary>
    [Fact]
    public void FromDto_NeverPopulatesFileItems()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());

        var model = PanelStateMapper.FromDto(dto);

        Assert.Empty(model.FileItems);
    }

    // ── Forward compatibility (ADR-024 Amendment "Additive revisions") ───────

    /// <summary>
    /// Verifies that unknown keys at the four panel levels travel from the DTO through the model's
    /// <see cref="ChildWindowModel.PersistedExtras"/> back into a DTO built from that model.
    /// </summary>
    [Fact]
    public void Extras_AllFourLevels_RoundTripThroughModel()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Extras = Extra("panelKey");
        dto.Geometry.Extras = Extra("geometryKey");
        dto.Appearance.Extras = Extra("appearanceKey");
        dto.Content.Extras = Extra("contentKey");

        var model = PanelStateMapper.FromDto(dto);
        var back = PanelStateMapper.ToDto(model);

        Assert.NotNull(model.PersistedExtras);
        Assert.Equal(4, model.PersistedExtras!.UnknownKeyCount);
        Assert.Equal(["panelKey"], back.Extras!.Keys);
        Assert.Equal(["geometryKey"], back.Geometry.Extras!.Keys);
        Assert.Equal(["appearanceKey"], back.Appearance.Extras!.Keys);
        Assert.Equal(["contentKey"], back.Content.Extras!.Keys);
        Assert.Equal(1, back.Content.Extras["contentKey"].GetInt32());
    }

    /// <summary>Verifies that a panel without any unknown field carries no <see cref="ChildWindowModel.PersistedExtras"/>.</summary>
    [Fact]
    public void FromDto_NothingUnknown_PersistedExtrasIsNull()
    {
        var model = PanelStateMapper.FromDto(PanelStateMapper.ToDto(BuildFullyPopulatedModel()));

        Assert.Null(model.PersistedExtras);
    }

    /// <summary>
    /// Verifies that every enum field degrades to its default on an unknown member name, nothing throws, and the
    /// raw strings are remembered and written back.
    /// </summary>
    [Fact]
    public void FromDto_UnknownEnumStrings_FallBackAndAreWrittenBack()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.WindowState = "Floating";
        dto.Geometry.GeometryMode = "Masonry";
        dto.Geometry.SizingMode = "AutoWidth";
        dto.Appearance.SurfaceStyle = "Mica";
        dto.Appearance.IconLayoutPolicy = "Packed";
        dto.Content.ViewMode = "Tiles";
        dto.Content.ContentFilter = "ImagesOnly";
        dto.Content.SortMode = "Newest";

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(WindowState.Normal, model.WindowState);
        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
        Assert.Equal(PanelSizingMode.Manual, model.SizingMode);
        Assert.Equal(PanelSurfaceStyle.Ambient, model.SurfaceStyle);
        Assert.Equal(IconLayoutPolicy.LegacyStretch, model.IconLayoutPolicy);
        Assert.Equal(PanelViewMode.List, model.ViewMode);
        Assert.Equal(PanelContentFilter.ShowAll, model.ContentFilter);
        Assert.Equal(PanelSortMode.Manual, model.SortMode);
        Assert.Equal(8, model.PersistedExtras!.UnknownEnumCount);

        var back = PanelStateMapper.ToDto(model);
        Assert.Equal("Floating", back.Geometry.WindowState);
        Assert.Equal("Masonry", back.Geometry.GeometryMode);
        Assert.Equal("AutoWidth", back.Geometry.SizingMode);
        Assert.Equal("Mica", back.Appearance.SurfaceStyle);
        Assert.Equal("Packed", back.Appearance.IconLayoutPolicy);
        Assert.Equal("Tiles", back.Content.ViewMode);
        Assert.Equal("ImagesOnly", back.Content.ContentFilter);
        Assert.Equal("Newest", back.Content.SortMode);
    }

    /// <summary>
    /// Verifies that an unknown <c>geometryMode</c> written back verbatim keeps the span units it came with, so a
    /// future mode that relies on spans is not silently zeroed by a round trip through this build.
    /// </summary>
    [Fact]
    public void RoundTrip_UnknownGeometryModeWithSpans_KeepsRawModeAndSpans()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.GeometryMode = "Hex";
        dto.Geometry.WidthSpanUnits = 7;
        dto.Geometry.HeightSpanUnits = 5;

        var model = PanelStateMapper.FromDto(dto);
        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);

        var back = PanelStateMapper.ToDto(model);

        Assert.Equal("Hex", back.Geometry.GeometryMode);
        Assert.Equal(7, back.Geometry.WidthSpanUnits);
        Assert.Equal(5, back.Geometry.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that once the user explicitly changes an unknown preserved <c>geometryMode</c> to a known
    /// grid mode the new value is written together with the spans.
    /// </summary>
    [Fact]
    public void ToDto_UnknownGeometryModeReplacedByGridAligned_WritesGridAlignedWithSpans()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.GeometryMode = "Hex";
        dto.Geometry.WidthSpanUnits = 7;
        dto.Geometry.HeightSpanUnits = 5;
        var model = PanelStateMapper.FromDto(dto);

        model.GeometryMode = PanelGeometryMode.GridAligned;
        var back = PanelStateMapper.ToDto(model);

        Assert.Equal(nameof(PanelGeometryMode.GridAligned), back.Geometry.GeometryMode);
        Assert.Equal(7, back.Geometry.WidthSpanUnits);
        Assert.Equal(5, back.Geometry.HeightSpanUnits);
    }

    /// <summary>Verifies that changing a field whose raw value was preserved makes the new value win.</summary>
    [Fact]
    public void ToDto_UnknownEnum_UserChangedValue_WritesNewValue()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Appearance.SurfaceStyle = "Mica";
        var model = PanelStateMapper.FromDto(dto);

        model.SurfaceStyle = PanelSurfaceStyle.Solid;

        Assert.Equal("Solid", PanelStateMapper.ToDto(model).Appearance.SurfaceStyle);
    }

    /// <summary>
    /// Verifies the documented limitation: explicitly choosing the very member the field was degraded to is
    /// indistinguishable from not touching it, so the raw string is kept.
    /// </summary>
    [Fact]
    public void ToDto_UnknownEnum_UserPicksTheFallbackMember_RawIsKept()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Appearance.SurfaceStyle = "Mica";
        var model = PanelStateMapper.FromDto(dto);

        model.SurfaceStyle = PanelSurfaceStyle.Ambient;

        Assert.Equal("Mica", PanelStateMapper.ToDto(model).Appearance.SurfaceStyle);
    }

    /// <summary>Verifies that numeric strings, comma lists and whitespace are unknown values, not members.</summary>
    /// <param name="raw">The persisted enum string.</param>
    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData(" 1 ")]
    [InlineData("Normal,Maximized")]
    [InlineData("  ")]
    public void FromDto_NonMemberStrings_AreUnknown(string raw)
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.WindowState = raw;

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(WindowState.Normal, model.WindowState);
        Assert.Equal(raw, PanelStateMapper.ToDto(model).Geometry.WindowState);
    }

    /// <summary>Verifies that known members are matched case-insensitively and are not reported as unknown.</summary>
    [Fact]
    public void FromDto_KnownEnumStrings_CaseInsensitive_NothingPreserved()
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Geometry.WindowState = "maximized";
        dto.Content.SortMode = "NAME";

        var model = PanelStateMapper.FromDto(dto);

        Assert.Equal(WindowState.Maximized, model.WindowState);
        Assert.Equal(PanelSortMode.Name, model.SortMode);
        Assert.Null(model.PersistedExtras);
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowModel.PersistedExtras"/> survives the JSON round trip layout snapshots keep
    /// panels in (<c>PanelJsonItems</c>), with both option sets in use (<c>ApplicationStateService</c> and
    /// <c>LayoutSnapshotService</c> both use plain, non-indented <see cref="JsonSerializerOptions"/>).
    /// </summary>
    /// <param name="indented">Whether the serializer writes indented JSON.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelJsonItems_RoundTrip_PreservesPersistedExtras(bool indented)
    {
        var dto = PanelStateMapper.ToDto(BuildFullyPopulatedModel());
        dto.Extras = Extra("panelKey");
        dto.Geometry.Extras = Extra("geometryKey");
        dto.Appearance.SurfaceStyle = "Mica";
        var model = PanelStateMapper.FromDto(dto);
        var options = new JsonSerializerOptions { WriteIndented = indented };

        var json = JsonSerializer.Serialize(model, options);
        var restored = JsonSerializer.Deserialize<ChildWindowModel>(json, options)!;
        var back = PanelStateMapper.ToDto(restored);

        Assert.Equal(["panelKey"], back.Extras!.Keys);
        Assert.Equal(["geometryKey"], back.Geometry.Extras!.Keys);
        Assert.Equal("Mica", back.Appearance.SurfaceStyle);
    }

    /// <summary>Verifies that a model without extras serializes without a <c>PersistedExtras</c> member at all.</summary>
    [Fact]
    public void PanelJson_WithoutExtras_OmitsPersistedExtras()
    {
        var json = JsonSerializer.Serialize(BuildFullyPopulatedModel());

        Assert.DoesNotContain("PersistedExtras", json);
    }

    private static Dictionary<string, JsonElement> Extra(string key) =>
        new() { [key] = JsonSerializer.SerializeToElement(1) };

    // ── helpers ──────────────────────────────────────────────────────────────
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
        GeometryMode = PanelGeometryMode.GridAligned,
        WidthSpanUnits = 20,
        HeightSpanUnits = 15,
        SizingMode = PanelSizingMode.AutoContent,
        IconLayoutPolicy = IconLayoutPolicy.FixedGap,
    };
}
