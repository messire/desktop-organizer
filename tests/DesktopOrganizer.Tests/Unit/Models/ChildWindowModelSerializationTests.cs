using System.Text.Json;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="ChildWindowModel.SortMode"/> JSON persistence (ADR-014 Decision 1),
/// serialized the same way <c>WindowManager</c> serializes the whole panel list — plain
/// <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions?)"/>/<see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions?)"/>
/// with no custom <see cref="JsonSerializerOptions"/> and no <c>JsonStringEnumConverter</c>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowModelSerializationTests
{
    /// <summary>
    /// Verifies that deserializing a pre-existing JSON document with no <c>SortMode</c> property leaves
    /// <see cref="ChildWindowModel.SortMode"/> at its field-initializer default of
    /// <see cref="PanelSortMode.Manual"/>, preserving legacy panels' item order.
    /// </summary>
    [Fact]
    public void Deserialize_JsonWithoutSortModeField_DefaultsToManual()
    {
        // A JSON document saved before this feature existed has no "SortMode" property at all —
        // System.Text.Json deserializes a missing property by leaving the field-initializer value in
        // place, which must be Manual to preserve pre-existing panels' item order untouched
        // (ADR-014 Decision 1 / PanelSortMode.Manual == 0).
        const string json = """
            {
                "Title": "Old Panel",
                "BackgroundColor": "#1E1E1E",
                "FilePaths": ["C:\\fake\\a.txt", "C:\\fake\\b.txt"]
            }
            """;

        var model = JsonSerializer.Deserialize<ChildWindowModel>(json);

        Assert.NotNull(model);
        Assert.Equal(PanelSortMode.Manual, model!.SortMode);
        Assert.Equal("Old Panel", model.Title);
        Assert.Equal(["C:\\fake\\a.txt", "C:\\fake\\b.txt"], model.FilePaths);
    }

    /// <summary>
    /// Verifies that a <see cref="ChildWindowModel"/> with <see cref="PanelSortMode.Name"/> round-trips
    /// through plain JSON serialization and deserialization without losing that sort mode.
    /// </summary>
    [Fact]
    public void RoundTrip_NameSortMode_DeserializesBackToName()
    {
        var model = new ChildWindowModel { SortMode = PanelSortMode.Name };

        var json = JsonSerializer.Serialize(model);
        var roundTripped = JsonSerializer.Deserialize<ChildWindowModel>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(PanelSortMode.Name, roundTripped!.SortMode);
    }

    /// <summary>
    /// Verifies that a <see cref="ChildWindowModel"/> with an explicitly set
    /// <see cref="PanelSortMode.Manual"/> round-trips through plain JSON serialization and
    /// deserialization without losing that sort mode.
    /// </summary>
    [Fact]
    public void RoundTrip_ManualSortMode_DeserializesBackToManual()
    {
        var model = new ChildWindowModel { SortMode = PanelSortMode.Manual };

        var json = JsonSerializer.Serialize(model);
        var roundTripped = JsonSerializer.Deserialize<ChildWindowModel>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(PanelSortMode.Manual, roundTripped!.SortMode);
    }

    /// <summary>
    /// Verifies that setting <see cref="ChildWindowModel.SortMode"/> to a new value raises
    /// <c>PropertyChanged</c> for that property.
    /// </summary>
    [Fact]
    public void SortMode_Set_RaisesPropertyChanged()
    {
        var model = new ChildWindowModel();
        var raisedProperties = new List<string?>();
        model.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        model.SortMode = PanelSortMode.Name;

        Assert.Contains(nameof(ChildWindowModel.SortMode), raisedProperties);
    }

    /// <summary>
    /// Verifies that re-assigning <see cref="ChildWindowModel.SortMode"/> to its current default value
    /// is a no-op that does not raise <c>PropertyChanged</c>.
    /// </summary>
    [Fact]
    public void SortMode_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        // Manual is already the default — re-applying it must be a no-op, matching the SetField
        // equality guard used by every other ChildWindowModel property (ViewMode, ContentFilter, ...).
        var model = new ChildWindowModel();
        var raised = false;
        model.PropertyChanged += (_, _) => raised = true;

        model.SortMode = PanelSortMode.Manual;

        Assert.False(raised);
    }

    // ── settings-window-redesign.md finding 2 (§3.1 R-9): SourceFolderPath moved from an
    //    auto-property to the same SetField-backed INPC pattern as every other ChildWindowModel
    //    property, so ChildWindowViewModel.HandleSourceFolderPathChanged can react to it directly. ──

    /// <summary>
    /// Verifies that setting <see cref="ChildWindowModel.SourceFolderPath"/> to a new value raises
    /// <c>PropertyChanged</c> for that property.
    /// </summary>
    [Fact]
    public void SourceFolderPath_Set_RaisesPropertyChanged()
    {
        var model = new ChildWindowModel();
        var raisedProperties = new List<string?>();
        model.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        model.SourceFolderPath = @"C:\fake\portal";

        Assert.Contains(nameof(ChildWindowModel.SourceFolderPath), raisedProperties);
    }

    /// <summary>
    /// Verifies that re-assigning <see cref="ChildWindowModel.SourceFolderPath"/> to its current value
    /// (including the <see langword="null"/> default) is a no-op that does not raise
    /// <c>PropertyChanged</c>, matching the <c>SetField</c> equality guard used by every other
    /// <see cref="ChildWindowModel"/> property.
    /// </summary>
    [Fact]
    public void SourceFolderPath_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var model = new ChildWindowModel { SourceFolderPath = @"C:\fake\portal" };
        var raised = false;
        model.PropertyChanged += (_, _) => raised = true;

        model.SourceFolderPath = @"C:\fake\portal";

        Assert.False(raised);
    }

    /// <summary>
    /// Verifies that re-assigning the field-initializer default (<see langword="null"/>) without ever
    /// having set <see cref="ChildWindowModel.SourceFolderPath"/> is also a no-op.
    /// </summary>
    [Fact]
    public void SourceFolderPath_SetNullOnFreshModel_DoesNotRaisePropertyChanged()
    {
        var model = new ChildWindowModel();
        var raised = false;
        model.PropertyChanged += (_, _) => raised = true;

        model.SourceFolderPath = null;

        Assert.False(raised);
    }

    // ── FIX round 2 MAJOR-1: FilePaths moved from a plain auto-property to the same SetField-backed
    //    INPC pattern as every other ChildWindowModel property, so the settings window's Panels list
    //    subtitle (PanelsSection.xaml's PanelSubtitleConverter, bound through FilePaths.Count) stays live
    //    while the settings window — a hidden DI singleton, never recreated — is open. ────────────────

    /// <summary>
    /// Verifies that assigning a new (reference-distinct) list to <see cref="ChildWindowModel.FilePaths"/>
    /// raises <c>PropertyChanged</c> for that property — the case every real caller
    /// (<c>ChildWindow.SyncToModel</c>, <c>ChildWindowViewModel</c>'s portal attach/detach paths) hits,
    /// since all of them build a fresh list rather than mutating the existing one in place.
    /// </summary>
    [Fact]
    public void FilePaths_SetToNewList_RaisesPropertyChanged()
    {
        var model = new ChildWindowModel();
        var raisedProperties = new List<string?>();
        model.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        model.FilePaths = ["C:\\fake\\a.txt"];

        Assert.Contains(nameof(ChildWindowModel.FilePaths), raisedProperties);
    }

    /// <summary>
    /// Verifies that re-assigning the EXACT SAME <see cref="List{T}"/> instance already held by
    /// <see cref="ChildWindowModel.FilePaths"/> is a no-op that does not raise <c>PropertyChanged</c> —
    /// <see cref="List{T}"/> has no value-equality override, so <c>SetField</c>'s
    /// <see cref="EqualityComparer{T}.Default"/> guard here is reference equality specifically.
    /// </summary>
    [Fact]
    public void FilePaths_SetToSameListInstance_DoesNotRaisePropertyChanged()
    {
        var list = new List<string> { "C:\\fake\\a.txt" };
        var model = new ChildWindowModel { FilePaths = list };
        var raised = false;
        model.PropertyChanged += (_, _) => raised = true;

        model.FilePaths = list;

        Assert.False(raised);
    }

    /// <summary>
    /// Verifies that assigning a DIFFERENT list instance with byte-for-byte identical CONTENTS still
    /// raises <c>PropertyChanged</c> — reference equality, not content equality, is what
    /// <see cref="ChildWindowModel.FilePaths"/>'s <c>SetField</c> guard checks, matching the "assigning
    /// FilePaths raises PropertyChanged; same-reference assignment does not" contract (FIX round 2
    /// MAJOR-1).
    /// </summary>
    [Fact]
    public void FilePaths_SetToDifferentListWithSameContents_RaisesPropertyChanged()
    {
        var model = new ChildWindowModel { FilePaths = ["C:\\fake\\a.txt"] };
        var raised = false;
        model.PropertyChanged += (_, _) => raised = true;

        model.FilePaths = ["C:\\fake\\a.txt"]; // A new List<string> instance, same contents.

        Assert.True(raised);
    }
}
