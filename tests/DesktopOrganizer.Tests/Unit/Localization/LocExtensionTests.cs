using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="LocExtension"/> (docs/PLANS/localization.md §2.4, WP-0U DoD 1).
/// </summary>
/// <remarks>
/// Every test that constructs a real WPF element (<see cref="Window"/>, <see cref="ContextMenu"/>, ...)
/// runs on a dedicated STA thread (<see cref="StaTestRunner"/>) — <see cref="XamlReader.Parse(string)"/>
/// itself does not strictly require it, but constructing a <see cref="ContextMenu"/>/<see cref="ToolTip"/>
/// does (touches <see cref="System.Windows.Input.InputManager"/> — see
/// <c>ResourceDictionaryMergeTests</c>'s own remarks), so every scenario here uses the same convention
/// uniformly. Every raw XAML string below hardcodes the <c>loc</c> namespace declaration
/// (<c>clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI</c>) rather than
/// interpolating it — plain (non-<c>$</c>) raw string literals need no brace-escaping at all, which keeps
/// the actual <c>{loc:Loc ...}</c> markup-extension syntax below exactly as a consumer would write it in
/// real production XAML.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LocExtensionTests
{
    /// <summary><c>{loc:Loc Key}</c> resolves against <see cref="LocalizationSource.Current"/>'s default (English) instance inside a real <see cref="Window"/>.</summary>
    [Fact]
    public void ProvideValue_InWindow_BindsTextToCurrentCultureString()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI">
                    <TextBlock x:Name="Text1" Text="{loc:Loc Common_Ok_Button}" />
                </Window>
                """;

            var window = (Window)XamlReader.Parse(xaml);
            var text = (TextBlock)window.FindName("Text1")!;

            Assert.Equal("OK", text.Text);
        });
    }

    /// <summary><c>{loc:Loc Key}</c> works the same way inside a <see cref="UserControl"/>.</summary>
    [Fact]
    public void ProvideValue_InUserControl_BindsTextToCurrentCultureString()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI">
                    <TextBlock x:Name="Text1" Text="{loc:Loc Common_Ok_Button}" />
                </UserControl>
                """;

            var userControl = (UserControl)XamlReader.Parse(xaml);
            var text = (TextBlock)userControl.FindName("Text1")!;

            Assert.Equal("OK", text.Text);
        });
    }

    /// <summary><c>{loc:Loc Key}</c> works inside a <see cref="DataTemplate"/>'s deferred content.</summary>
    [Fact]
    public void ProvideValue_InDataTemplate_BindsTextToCurrentCultureString()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                              xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI">
                    <TextBlock Text="{loc:Loc Common_Ok_Button}" />
                </DataTemplate>
                """;

            var template = (DataTemplate)XamlReader.Parse(xaml);
            var content = (TextBlock)template.LoadContent();

            Assert.Equal("OK", content.Text);
        });
    }

    /// <summary><c>{loc:Loc Key}</c> works inside a <see cref="ContextMenu"/> item's <c>Header</c> — the explicit <c>Source</c> means it does not depend on ambient <c>DataContext</c> inheritance.</summary>
    [Fact]
    public void ProvideValue_InContextMenu_BindsHeaderToCurrentCultureString()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <ContextMenu xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI">
                    <MenuItem x:Name="Item1" Header="{loc:Loc Common_Ok_Button}" />
                </ContextMenu>
                """;

            var menu = (ContextMenu)XamlReader.Parse(xaml);
            var item = (MenuItem)menu.Items[0]!;

            Assert.Equal("OK", item.Header);
        });
    }

    /// <summary><c>{loc:Loc Key}</c> works inside a <see cref="ToolTip"/>'s content.</summary>
    [Fact]
    public void ProvideValue_InToolTip_BindsTextToCurrentCultureString()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <ToolTip xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI">
                    <TextBlock x:Name="Text1" Text="{loc:Loc Common_Ok_Button}" />
                </ToolTip>
                """;

            var tooltip = (ToolTip)XamlReader.Parse(xaml);
            var text = (TextBlock)tooltip.Content;

            Assert.Equal("OK", text.Text);
        });
    }

    /// <summary>
    /// For a Setter/template target (no real dependency property in <see cref="IProvideValueTarget"/>),
    /// <see cref="LocExtension.ProvideValue"/> returns the raw, unevaluated <see cref="Binding"/> instead
    /// of a resolved value (docs/PLANS/localization.md §2.4).
    /// </summary>
    [Fact]
    public void ProvideValue_ForNonDependencyPropertyTarget_ReturnsRawBindingUnevaluated()
    {
        var extension = new LocExtension("Common_Ok_Button");
        var provider = new FakeProvideValueTargetServiceProvider
        {
            TargetObject = new Setter(),
            TargetProperty = typeof(Setter).GetProperty(nameof(Setter.Value)),
        };

        var result = extension.ProvideValue(provider);

        var binding = Assert.IsType<Binding>(result);
        Assert.Equal("[Common_Ok_Button]", binding.Path.Path);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
    }

    /// <summary>
    /// Confirms that raw <see cref="Binding"/> genuinely works once WPF applies it to a real target: a
    /// <see cref="Style"/> built from the SAME XAML shape hot-switches a real <see cref="TextBlock"/>'s
    /// <c>Text</c> once the <see cref="Style"/> is assigned.
    /// </summary>
    [Fact]
    public void ProvideValue_InStyleSetter_AppliesToRealTextBlockOnceStyleIsAssigned()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                       xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI"
                       TargetType="TextBlock">
                    <Setter Property="Text" Value="{loc:Loc Common_Ok_Button}" />
                </Style>
                """;

            var style = (Style)XamlReader.Parse(xaml);
            var textBlock = new TextBlock { Style = style };

            Assert.Equal("OK", textBlock.Text);
        });
    }

    /// <summary>
    /// An empty/never-set <see cref="LocExtension.Key"/> throws <see cref="InvalidOperationException"/>
    /// at <see cref="LocExtension.ProvideValue"/> time (fix-cycle NIT) — a forgotten <c>{loc:Loc}</c>
    /// argument fails loudly instead of silently resolving to the empty key.
    /// </summary>
    [Fact]
    public void ProvideValue_EmptyKey_ThrowsInvalidOperationException()
    {
        var extension = new LocExtension();
        var provider = new FakeProvideValueTargetServiceProvider
        {
            TargetObject = new Setter(),
            TargetProperty = typeof(Setter).GetProperty(nameof(Setter.Value)),
        };

        Assert.Throws<InvalidOperationException>(() => extension.ProvideValue(provider));
    }

    /// <summary>
    /// The binding <see cref="LocExtension"/> produces (built via the SAME internal factory it itself
    /// uses) hot-switches text when the LOCAL <see cref="LocalizationService"/> instance it is sourced
    /// from changes language — without ever touching the process-wide <see cref="LocalizationSource.Current"/>.
    /// </summary>
    [Fact]
    public void CreateKeyBinding_LocalService_HotSwitchesTextOnCultureChanged()
    {
        StaTestRunner.Run(() =>
        {
            var localService = TestLocalization.English;
            var textBlock = new TextBlock();
            var binding = LocBindingFactory.CreateKeyBinding("Common_Ok_Button", localService);

            BindingOperations.SetBinding(textBlock, TextBlock.TextProperty, binding);
            Assert.Equal("OK", textBlock.Text);

            localService.ApplyLanguage("ru");

            Assert.Equal("ОК", textBlock.Text);
        });
    }
}
