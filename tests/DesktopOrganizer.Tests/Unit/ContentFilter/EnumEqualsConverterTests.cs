using System.Globalization;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

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

    /// <summary>
    /// Verifies that converting two equal enum values returns <c>true</c>.
    /// </summary>
    [Fact]
    public void Convert_SameEnumValues_ReturnsTrue()
    {
        var result = Converter.Convert(PanelContentFilter.ShowAll, typeof(bool), PanelContentFilter.ShowAll, AnyCulture);

        Assert.Equal(true, result);
    }

    /// <summary>
    /// Verifies that converting two different enum values returns <c>false</c>.
    /// </summary>
    [Fact]
    public void Convert_DifferentEnumValues_ReturnsFalse()
    {
        var result = Converter.Convert(PanelContentFilter.HideAll, typeof(bool), PanelContentFilter.ShowAll, AnyCulture);

        Assert.Equal(false, result);
    }

    /// <summary>
    /// Verifies that a <c>null</c> bound value is treated as not equal to the parameter, returning <c>false</c>.
    /// </summary>
    [Fact]
    public void Convert_NullValue_ReturnsFalse()
    {
        var result = Converter.Convert(null, typeof(bool), PanelContentFilter.ShowAll, AnyCulture);

        Assert.Equal(false, result);
    }

    // ── ConvertBack ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="EnumEqualsConverter.ConvertBack"/> always throws <see cref="NotSupportedException"/>,
    /// since this converter is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(true, typeof(PanelContentFilter), PanelContentFilter.ShowAll, AnyCulture));
    }
}

/// <summary>
/// Unit tests for the <c>SetContentFilterCommand</c> logic in <see cref="DesktopOrganizer.UI.ViewModels.ChildWindowViewModel"/>.
/// Tests the command in isolation using <see cref="RelayCommand{T}"/> directly to avoid
/// WPF resource dependencies in <see cref="DesktopOrganizer.UI.ViewModels.ChildWindowViewModel"/>'s constructor.
/// </summary>
[Trait(UnitTests, "UI")]
public class SetContentFilterCommandTests
{
    /// <summary>
    /// Verifies that executing the command with <see cref="PanelContentFilter.FoldersOnly"/> writes that
    /// value to the model's <see cref="ChildWindowModel.ContentFilter"/>.
    /// </summary>
    [Fact]
    public void Execute_FoldersOnly_SetsModelContentFilter()
    {
        var model = new ChildWindowModel();

        // Mirror the exact lambda used in ChildWindowViewModel ctor (line 129)
        var command = new RelayCommand<PanelContentFilter>(filter => model.ContentFilter = filter);

        command.Execute(PanelContentFilter.FoldersOnly);

        Assert.Equal(PanelContentFilter.FoldersOnly, model.ContentFilter);
    }

    /// <summary>
    /// Verifies that executing the command with <see cref="PanelContentFilter.HideAll"/> writes that
    /// value to the model's <see cref="ChildWindowModel.ContentFilter"/>.
    /// </summary>
    [Fact]
    public void Execute_HideAll_SetsModelContentFilter()
    {
        var model = new ChildWindowModel();
        var command = new RelayCommand<PanelContentFilter>(filter => model.ContentFilter = filter);

        command.Execute(PanelContentFilter.HideAll);

        Assert.Equal(PanelContentFilter.HideAll, model.ContentFilter);
    }

    /// <summary>
    /// Verifies that executing the command with <see cref="PanelContentFilter.ShowAll"/> overwrites a
    /// previously set filter on the model's <see cref="ChildWindowModel.ContentFilter"/>.
    /// </summary>
    [Fact]
    public void Execute_ShowAll_SetsModelContentFilter()
    {
        var model = new ChildWindowModel { ContentFilter = PanelContentFilter.FoldersOnly };
        var command = new RelayCommand<PanelContentFilter>(filter => model.ContentFilter = filter);

        command.Execute(PanelContentFilter.ShowAll);

        Assert.Equal(PanelContentFilter.ShowAll, model.ContentFilter);
    }
}
