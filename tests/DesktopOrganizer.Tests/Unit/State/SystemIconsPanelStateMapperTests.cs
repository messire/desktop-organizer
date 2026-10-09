using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.Tests.Unit.State;

/// <summary>
/// Unit tests for <see cref="SystemIconsPanelStateMapper"/> (ADR-024 C6). Covers exactly the concerns
/// that used to be exercised end-to-end (JSON string round trip) inside
/// <c>SystemIconsPanelManagerTests</c> before <c>SystemIconsPanelManager.Save()</c> became a thin
/// <c>IApplicationStateService.SaveNow()</c> delegate (see that file's "Save: delegates to
/// IApplicationStateService.SaveNow()" region).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SystemIconsPanelStateMapperTests
{
    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelModel.IsEnabled"/> survives a
    /// <see cref="SystemIconsPanelStateMapper.ToDto"/> -&gt; <see cref="SystemIconsPanelStateMapper.ApplyTo"/> round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesIsEnabled()
    {
        var model = new SystemIconsPanelModel { IsEnabled = true };

        var roundTripped = RoundTrip(model);

        Assert.True(roundTripped.IsEnabled);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelModel.ShowOnAllVirtualDesktops"/> survives a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesShowOnAllVirtualDesktops()
    {
        var model = new SystemIconsPanelModel { IsEnabled = true, ShowOnAllVirtualDesktops = true };

        var roundTripped = RoundTrip(model);

        Assert.True(roundTripped.ShowOnAllVirtualDesktops);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelModel.IconSize"/> survives a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesIconSize()
    {
        var model = new SystemIconsPanelModel { IconSize = SystemIconSize.Large };

        var roundTripped = RoundTrip(model);

        Assert.Equal(SystemIconSize.Large, roundTripped.IconSize);
    }

    /// <summary>
    /// Verifies that a non-null <see cref="SystemIconsPanelModel.AnchorVirtualDesktopId"/> survives a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesAnchorVirtualDesktopId()
    {
        var anchor = Guid.NewGuid();
        var model = new SystemIconsPanelModel { AnchorVirtualDesktopId = anchor };

        var roundTripped = RoundTrip(model);

        Assert.Equal(anchor, roundTripped.AnchorVirtualDesktopId);
    }

    /// <summary>
    /// Verifies that a null <see cref="SystemIconsPanelModel.AnchorVirtualDesktopId"/> stays null across a round trip.
    /// </summary>
    [Fact]
    public void RoundTrip_NullAnchor_StaysNull()
    {
        var model = new SystemIconsPanelModel { AnchorVirtualDesktopId = null };

        var roundTripped = RoundTrip(model);

        Assert.Null(roundTripped.AnchorVirtualDesktopId);
    }

    /// <summary>
    /// Verifies that an unparsable anchor virtual desktop id string in the DTO applies as a null
    /// <see cref="SystemIconsPanelModel.AnchorVirtualDesktopId"/> without preventing the other fields
    /// (<c>IsEnabled</c>, <c>IconSize</c>) from applying normally.
    /// </summary>
    [Fact]
    public void ApplyTo_GarbageAnchorValue_AnchorIsNull_ButOtherFieldsApplyNormally()
    {
        var dto = new SystemIconsPanelStateDto
        {
            IsEnabled = true,
            IconSize = "Large",
            AnchorVirtualDesktopId = "not-a-guid",
        };
        var model = new SystemIconsPanelModel();

        SystemIconsPanelStateMapper.ApplyTo(dto, model);

        Assert.Null(model.AnchorVirtualDesktopId);
        Assert.True(model.IsEnabled);
        Assert.Equal(SystemIconSize.Large, model.IconSize);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelStateMapper.ApplyTo"/> restores each item's visibility
    /// and order from the DTO while still yielding one model item per known <see cref="SystemIconKind"/>.
    /// </summary>
    [Fact]
    public void ApplyTo_RestoresItemVisibilityAndOrder()
    {
        var dto = new SystemIconsPanelStateDto
        {
            Items =
            [
                new SystemIconsPanelItemStateDto { Kind = nameof(SystemIconKind.ControlPanel), IsVisible = false, Order = 1 },
                new SystemIconsPanelItemStateDto { Kind = nameof(SystemIconKind.ThisPc), IsVisible = true, Order = 0 },
            ],
        };
        var model = new SystemIconsPanelModel();

        SystemIconsPanelStateMapper.ApplyTo(dto, model);

        var controlPanel = model.Items.Single(i => i.Kind == SystemIconKind.ControlPanel);
        Assert.False(controlPanel.IsVisible);
        Assert.Equal(1, controlPanel.Order);
        Assert.Equal(Enum.GetValues<SystemIconKind>().Length, model.Items.Count);
    }

    /// <summary>
    /// Verifies that an empty <c>Items</c> list in the DTO leaves the model's default item collection
    /// (all visible) untouched instead of clearing it.
    /// </summary>
    [Fact]
    public void ApplyTo_EmptyItemsList_LeavesModelItemsUntouched()
    {
        var dto = new SystemIconsPanelStateDto { Items = [] };
        var model = new SystemIconsPanelModel();
        var defaultCount = model.Items.Count;

        SystemIconsPanelStateMapper.ApplyTo(dto, model);

        Assert.Equal(defaultCount, model.Items.Count);
        Assert.All(model.Items, item => Assert.True(item.IsVisible));
    }

    /// <summary>
    /// Verifies that an item whose <c>kind</c> this build does not know is skipped when applying to the model, kept
    /// by the ledger, and written back (after the known items) by the next <c>ToDto</c>, together with section-level
    /// and item-level unknown keys and an unrecognised icon size.
    /// </summary>
    [Fact]
    public void UnknownSystemIconKind_Preserved()
    {
        var document = JsonSerializer.Deserialize<ApplicationStateDocumentDto>(
            """
            {
              "systemIconsPanel": {
                "isEnabled": true,
                "iconSize": "Gigantic",
                "futureSection": { "a": 1 },
                "items": [
                  { "kind": "RecycleBin", "isVisible": false, "order": 0, "futureItem": "keep" },
                  { "kind": "Printers", "isVisible": true, "order": 9, "futureKindField": 2 },
                  { "kind": "3", "isVisible": true, "order": 8 }
                ]
              }
            }
            """,
            StateJson.Options)!;
        var ledger = UnknownFieldLedger.Capture(document);
        var model = new SystemIconsPanelModel();

        SystemIconsPanelStateMapper.ApplyTo(document.SystemIconsPanel, model);
        var dto = SystemIconsPanelStateMapper.ToDto(model, ledger);

        Assert.Equal(2, ledger.UnknownSystemIconItemCount);
        Assert.Equal(SystemIconSize.Medium, model.IconSize);
        Assert.False(model.Items.Single(i => i.Kind == SystemIconKind.RecycleBin).IsVisible);
        Assert.Equal("Gigantic", dto.IconSize);
        Assert.Equal(["futureSection"], dto.Extras!.Keys);
        Assert.Equal(Enum.GetValues<SystemIconKind>().Length + 2, dto.Items.Count);
        var recycleBin = dto.Items.Single(i => i.Kind == nameof(SystemIconKind.RecycleBin));
        Assert.Equal("keep", recycleBin.Extras!["futureItem"].GetString());
        var printers = dto.Items.Single(i => i.Kind == "Printers");
        Assert.Equal(9, printers.Order);
        Assert.Equal(2, printers.Extras!["futureKindField"].GetInt32());
        Assert.Contains(dto.Items, i => i.Kind == "3");
        Assert.True(dto.Items.Take(Enum.GetValues<SystemIconKind>().Length).All(i => Enum.IsDefined(Enum.Parse<SystemIconKind>(i.Kind))));
    }

    /// <summary>Verifies that once the user picks a different icon size, the unrecognised raw size is no longer written.</summary>
    [Fact]
    public void UnrecognisedIconSize_UserChangedValue_WritesNewValue()
    {
        var document = JsonSerializer.Deserialize<ApplicationStateDocumentDto>(
            """{ "systemIconsPanel": { "iconSize": "Gigantic" } }""",
            StateJson.Options)!;
        var ledger = UnknownFieldLedger.Capture(document);
        var model = new SystemIconsPanelModel();
        SystemIconsPanelStateMapper.ApplyTo(document.SystemIconsPanel, model);

        model.IconSize = SystemIconSize.Large;

        Assert.Equal("Large", SystemIconsPanelStateMapper.ToDto(model, ledger).IconSize);
    }

    private static SystemIconsPanelModel RoundTrip(SystemIconsPanelModel model)
    {
        var dto = SystemIconsPanelStateMapper.ToDto(model);
        var result = new SystemIconsPanelModel();
        SystemIconsPanelStateMapper.ApplyTo(dto, result);
        return result;
    }
}
