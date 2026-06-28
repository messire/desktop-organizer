using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Commands;

namespace DesktopOrganizer.Tests.Unit.ContentFilter;

/// <summary>
/// Unit tests for <see cref="EnumEqualsConverter"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class EnumEqualsConverterTests
{
    private static readonly EnumEqualsConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    // ── Convert ─────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_SameEnumValues_ReturnsTrue()
    {
        var result = Converter.Convert(PanelContentFilter.ShowAll, typeof(bool), PanelContentFilter.ShowAll, AnyCulture);

        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_DifferentEnumValues_ReturnsFalse()
    {
        var result = Converter.Convert(PanelContentFilter.HideAll, typeof(bool), PanelContentFilter.ShowAll, AnyCulture);

        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_NullValue_ReturnsFalse()
    {
        var result = Converter.Convert(null, typeof(bool), PanelContentFilter.ShowAll, AnyCulture);

        Assert.Equal(false, result);
    }

    // ── ConvertBack ──────────────────────────────────────────────────────────

    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(true, typeof(PanelContentFilter), PanelContentFilter.ShowAll, AnyCulture));
    }
}

/// <summary>
/// Unit tests for the <c>SetContentFilterCommand</c> logic in <see cref="ChildWindowViewModel"/>.
/// Tests the command in isolation using <see cref="RelayCommand{T}"/> directly to avoid
/// WPF resource dependencies in <see cref="ChildWindowViewModel"/>'s constructor.
/// </summary>
[Trait(UnitTests, "UI")]
public class SetContentFilterCommandTests
{
    [Fact]
    public void Execute_FoldersOnly_SetsModelContentFilter()
    {
        var model = new ChildWindowModel();
        // Mirror the exact lambda used in ChildWindowViewModel ctor (line 129)
        var command = new RelayCommand<PanelContentFilter>(filter => model.ContentFilter = filter);

        command.Execute(PanelContentFilter.FoldersOnly);

        Assert.Equal(PanelContentFilter.FoldersOnly, model.ContentFilter);
    }

    [Fact]
    public void Execute_HideAll_SetsModelContentFilter()
    {
        var model = new ChildWindowModel();
        var command = new RelayCommand<PanelContentFilter>(filter => model.ContentFilter = filter);

        command.Execute(PanelContentFilter.HideAll);

        Assert.Equal(PanelContentFilter.HideAll, model.ContentFilter);
    }

    [Fact]
    public void Execute_ShowAll_SetsModelContentFilter()
    {
        var model = new ChildWindowModel { ContentFilter = PanelContentFilter.FoldersOnly };
        var command = new RelayCommand<PanelContentFilter>(filter => model.ContentFilter = filter);

        command.Execute(PanelContentFilter.ShowAll);

        Assert.Equal(PanelContentFilter.ShowAll, model.ContentFilter);
    }
}
