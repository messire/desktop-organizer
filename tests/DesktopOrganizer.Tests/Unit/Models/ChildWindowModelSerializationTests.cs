using System.Text.Json;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="ChildWindowModel.SortMode"/> JSON persistence (ADR-014 Decision 1),
/// serialized the same way <c>WindowManager</c> serializes the whole panel list — plain
/// <see cref="JsonSerializer.Serialize{TValue}(TValue, Type?)"/>/<see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions?)"/>
/// with no custom <see cref="JsonSerializerOptions"/> and no <c>JsonStringEnumConverter</c>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowModelSerializationTests
{
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

    [Fact]
    public void RoundTrip_NameSortMode_DeserializesBackToName()
    {
        var model = new ChildWindowModel { SortMode = PanelSortMode.Name };

        var json = JsonSerializer.Serialize(model);
        var roundTripped = JsonSerializer.Deserialize<ChildWindowModel>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(PanelSortMode.Name, roundTripped!.SortMode);
    }

    [Fact]
    public void RoundTrip_ManualSortMode_DeserializesBackToManual()
    {
        var model = new ChildWindowModel { SortMode = PanelSortMode.Manual };

        var json = JsonSerializer.Serialize(model);
        var roundTripped = JsonSerializer.Deserialize<ChildWindowModel>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(PanelSortMode.Manual, roundTripped!.SortMode);
    }

    [Fact]
    public void SortMode_Set_RaisesPropertyChanged()
    {
        var model = new ChildWindowModel();
        var raisedProperties = new List<string?>();
        model.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        model.SortMode = PanelSortMode.Name;

        Assert.Contains(nameof(ChildWindowModel.SortMode), raisedProperties);
    }

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
