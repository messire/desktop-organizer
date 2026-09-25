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
}
