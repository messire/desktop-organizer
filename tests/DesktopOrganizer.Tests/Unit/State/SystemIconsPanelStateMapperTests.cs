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
    [Fact]
    public void RoundTrip_PreservesIsEnabled()
    {
        var model = new SystemIconsPanelModel { IsEnabled = true };

        var roundTripped = RoundTrip(model);

        Assert.True(roundTripped.IsEnabled);
    }

    [Fact]
    public void RoundTrip_PreservesShowOnAllVirtualDesktops()
    {
        var model = new SystemIconsPanelModel { IsEnabled = true, ShowOnAllVirtualDesktops = true };

        var roundTripped = RoundTrip(model);

        Assert.True(roundTripped.ShowOnAllVirtualDesktops);
    }

    [Fact]
    public void RoundTrip_PreservesIconSize()
    {
        var model = new SystemIconsPanelModel { IconSize = SystemIconSize.Large };

        var roundTripped = RoundTrip(model);

        Assert.Equal(SystemIconSize.Large, roundTripped.IconSize);
    }

    [Fact]
    public void RoundTrip_PreservesAnchorVirtualDesktopId()
    {
        var anchor = Guid.NewGuid();
        var model = new SystemIconsPanelModel { AnchorVirtualDesktopId = anchor };

        var roundTripped = RoundTrip(model);

        Assert.Equal(anchor, roundTripped.AnchorVirtualDesktopId);
    }

    [Fact]
    public void RoundTrip_NullAnchor_StaysNull()
    {
        var model = new SystemIconsPanelModel { AnchorVirtualDesktopId = null };

        var roundTripped = RoundTrip(model);

        Assert.Null(roundTripped.AnchorVirtualDesktopId);
    }

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

    [Fact]
    public void ApplyTo_RestoresItemVisibilityAndOrder()
    {
        var dto = new SystemIconsPanelStateDto
        {
            Items =
            [
                new SystemIconsPanelItemStateDto { Kind = SystemIconKind.ControlPanel, IsVisible = false, Order = 1 },
                new SystemIconsPanelItemStateDto { Kind = SystemIconKind.ThisPc, IsVisible = true, Order = 0 },
            ],
        };
        var model = new SystemIconsPanelModel();

        SystemIconsPanelStateMapper.ApplyTo(dto, model);

        var controlPanel = model.Items.Single(i => i.Kind == SystemIconKind.ControlPanel);
        Assert.False(controlPanel.IsVisible);
        Assert.Equal(1, controlPanel.Order);
        Assert.Equal(Enum.GetValues<SystemIconKind>().Length, model.Items.Count);
    }

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

    private static SystemIconsPanelModel RoundTrip(SystemIconsPanelModel model)
    {
        var dto = SystemIconsPanelStateMapper.ToDto(model);
        var result = new SystemIconsPanelModel();
        SystemIconsPanelStateMapper.ApplyTo(dto, result);
        return result;
    }
}
