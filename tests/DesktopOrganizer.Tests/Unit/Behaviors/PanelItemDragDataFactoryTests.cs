using System.Windows;
using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="PanelItemDragDataFactory"/> — the pure <see cref="DataObject"/> builder
/// backing a panel-item drag-out gesture (ADR-036, release-hardening-1.0.md §E).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelItemDragDataFactoryTests
{
    private const string ReorderFormat = "DesktopOrganizer.PanelItemReorder";
    private const string Path = @"C:\Users\someone\Desktop\file.txt";

    /// <summary>Verifies that create read write source returns all three formats.</summary>
    [Fact]
    public void Create_ReadWriteSource_ReturnsAllThreeFormats()
    {
        var data = PanelItemDragDataFactory.Create(ReorderFormat, Path, isReadOnly: false);

        Assert.NotNull(data);
        Assert.True(data!.GetDataPresent(ReorderFormat));
        Assert.Equal(Path, data.GetData(ReorderFormat));

        Assert.True(data.GetDataPresent(DataFormats.FileDrop));
        var files = Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop));
        Assert.Equal([Path], files);

        Assert.True(data.GetDataPresent(PanelItemDragDataFactory.PreferredDropEffectFormat));
    }

    /// <summary>Verifies that create preferred drop effect encodes move.</summary>
    [Fact]
    public void Create_PreferredDropEffect_EncodesMove()
    {
        var data = PanelItemDragDataFactory.Create(ReorderFormat, Path, isReadOnly: false);

        var bytes = Assert.IsType<byte[]>(data!.GetData(PanelItemDragDataFactory.PreferredDropEffectFormat));

        Assert.Equal((int)DragDropEffects.Move, BitConverter.ToInt32(bytes, 0));
    }

    /// <summary>Verifies that create read only source returns null.</summary>
    [Fact]
    public void Create_ReadOnlySource_ReturnsNull()
    {
        var data = PanelItemDragDataFactory.Create(ReorderFormat, Path, isReadOnly: true);

        Assert.Null(data);
    }

    /// <summary>Verifies that create blank path returns null.</summary>
    /// <param name="blankPath">A blank or whitespace-only path.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankPath_ReturnsNull(string blankPath)
    {
        var data = PanelItemDragDataFactory.Create(ReorderFormat, blankPath, isReadOnly: false);

        Assert.Null(data);
    }
}
