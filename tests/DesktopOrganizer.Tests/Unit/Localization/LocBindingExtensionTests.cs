using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="LocBindingExtension"/> and the internal <c>LocalizedValueConverter</c>
/// pipeline it wires up (docs/PLANS/localization.md §2.4, WP-0U DoD 1). See
/// <c>LocalizedValueConverterTests</c> for direct coverage of the CONVERT pipeline itself (inner
/// converter/<c>FormatKey</c> ordering, exception→fallback) — the tests here instead pin how
/// <see cref="LocBindingExtension.ProvideValue"/> WIRES that pipeline up: which bindings/converter/
/// parameter the produced <see cref="MultiBinding"/> carries, and that it integrates correctly with real
/// WPF placement contexts (<see cref="ControlTemplate"/> + <c>RelativeSource TemplatedParent</c>,
/// <see cref="Style"/> <see cref="Setter"/>).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LocBindingExtensionTests
{
    /// <summary>Only <see cref="LocBindingExtension.Path"/> set: the built <see cref="MultiBinding"/> has a plain <see cref="Binding"/> for <c>[0]</c> and no inner converter/format key.</summary>
    [Fact]
    public void ProvideValue_PathOnly_BuildsMultiBindingWithPlainValueBinding()
    {
        var extension = new LocBindingExtension("EffectiveSortMode");

        var multiBinding = (MultiBinding)extension.ProvideValue(new FakeProvideValueTargetServiceProvider());

        Assert.Equal(2, multiBinding.Bindings.Count);
        var valueBinding = Assert.IsType<Binding>(multiBinding.Bindings[0]);
        Assert.Equal("EffectiveSortMode", valueBinding.Path.Path);
        Assert.Equal(BindingMode.OneWay, valueBinding.Mode);

        var conversion = Assert.IsType<LocBindingConversion>(multiBinding.ConverterParameter);
        Assert.Null(conversion.Converter);
        Assert.Null(conversion.FormatKey);
    }

    /// <summary>No <see cref="LocBindingExtension.Path"/> at all: the value binding has no <see cref="Binding.Path"/> set, i.e. binds to the element's own <c>DataContext</c> object directly.</summary>
    [Fact]
    public void ProvideValue_NoPath_BuildsValueBindingOnDataContextItself()
    {
        var extension = new LocBindingExtension();

        var multiBinding = (MultiBinding)extension.ProvideValue(new FakeProvideValueTargetServiceProvider());

        var valueBinding = Assert.IsType<Binding>(multiBinding.Bindings[0]);
        Assert.Null(valueBinding.Path);
    }

    /// <summary><see cref="LocBindingExtension.Converter"/> and <see cref="LocBindingExtension.ConverterParameter"/> are carried through to the pipeline's <c>LocBindingConversion</c>, unmodified.</summary>
    [Fact]
    public void ProvideValue_PathAndConverterAndParameter_CarriesThemIntoConversion()
    {
        var innerConverter = new PassThroughConverter();
        var extension = new LocBindingExtension("Tag") { Converter = innerConverter, ConverterParameter = "p" };

        var multiBinding = (MultiBinding)extension.ProvideValue(new FakeProvideValueTargetServiceProvider());
        var conversion = Assert.IsType<LocBindingConversion>(multiBinding.ConverterParameter);

        Assert.Same(innerConverter, conversion.Converter);
        Assert.Equal("p", conversion.ConverterParameter);
    }

    /// <summary><see cref="LocBindingExtension.FormatKey"/> alone (no <see cref="LocBindingExtension.Converter"/>) is carried through.</summary>
    [Fact]
    public void ProvideValue_PathAndFormatKey_CarriesFormatKeyIntoConversion()
    {
        var extension = new LocBindingExtension("Tag") { FormatKey = "Language_FollowWindows_Format" };

        var multiBinding = (MultiBinding)extension.ProvideValue(new FakeProvideValueTargetServiceProvider());
        var conversion = Assert.IsType<LocBindingConversion>(multiBinding.ConverterParameter);

        Assert.Null(conversion.Converter);
        Assert.Equal("Language_FollowWindows_Format", conversion.FormatKey);
    }

    /// <summary>Both <see cref="LocBindingExtension.Converter"/> and <see cref="LocBindingExtension.FormatKey"/> together are carried through, in the same conversion payload.</summary>
    [Fact]
    public void ProvideValue_ConverterAndFormatKey_CarriesBothIntoConversion()
    {
        var innerConverter = new PassThroughConverter();
        var extension = new LocBindingExtension("Tag") { Converter = innerConverter, FormatKey = "Language_FollowWindows_Format" };

        var multiBinding = (MultiBinding)extension.ProvideValue(new FakeProvideValueTargetServiceProvider());
        var conversion = Assert.IsType<LocBindingConversion>(multiBinding.ConverterParameter);

        Assert.Same(innerConverter, conversion.Converter);
        Assert.Equal("Language_FollowWindows_Format", conversion.FormatKey);
    }

    /// <summary>The second child binding is always the live-culture binding, sourced from <see cref="LocalizationSource.Current"/>.</summary>
    [Fact]
    public void ProvideValue_SecondChildBinding_IsCultureBindingOnLocalizationSourceCurrent()
    {
        var extension = new LocBindingExtension("Tag");

        var multiBinding = (MultiBinding)extension.ProvideValue(new FakeProvideValueTargetServiceProvider());
        var cultureBinding = Assert.IsType<Binding>(multiBinding.Bindings[1]);

        Assert.Equal(nameof(DesktopOrganizer.Services.Abstractions.ILocalizationService.CurrentCulture), cultureBinding.Path.Path);
        Assert.Same(LocalizationSource.Current, cultureBinding.Source);
    }

    /// <summary>
    /// For a Setter/template target (no real dependency property in <see cref="IProvideValueTarget"/>),
    /// <see cref="LocBindingExtension.ProvideValue"/> returns the raw, unevaluated <see cref="MultiBinding"/>.
    /// </summary>
    [Fact]
    public void ProvideValue_ForNonDependencyPropertyTarget_ReturnsRawMultiBindingUnevaluated()
    {
        var extension = new LocBindingExtension("Tag");
        var provider = new FakeProvideValueTargetServiceProvider
        {
            TargetObject = new Setter(),
            TargetProperty = typeof(Setter).GetProperty(nameof(Setter.Value)),
        };

        var result = extension.ProvideValue(provider);

        Assert.IsType<MultiBinding>(result);
    }

    /// <summary>
    /// <c>{loc:LocBinding Content, RelativeSource={RelativeSource TemplatedParent}}</c> inside a real
    /// <see cref="ControlTemplate"/> reaches the templated parent's own property.
    /// </summary>
    [Fact]
    public void ProvideValue_InControlTemplate_RelativeSourceTemplatedParent_ReadsTemplatedParentProperty()
    {
        StaTestRunner.Run(() =>
        {
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                  xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI"
                                  TargetType="Button">
                    <TextBlock x:Name="Text1"
                               Text="{loc:LocBinding Content, RelativeSource={RelativeSource TemplatedParent}}" />
                </ControlTemplate>
                """;

            var template = (ControlTemplate)XamlReader.Parse(xaml);
            var button = new Button { Content = "hello", Template = template };
            button.ApplyTemplate();

            var text = (TextBlock)template.FindName("Text1", button)!;

            Assert.Equal("hello", text.Text);
        });
    }

    /// <summary>
    /// <c>{loc:LocBinding Tag}</c> in a <see cref="Style"/> <see cref="Setter"/> applies correctly once
    /// the <see cref="Style"/> is assigned to a real <see cref="TextBlock"/>.
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
                    <Setter Property="Text" Value="{loc:LocBinding Tag, RelativeSource={RelativeSource Self}}" />
                </Style>
                """;

            var style = (Style)XamlReader.Parse(xaml);
            var textBlock = new TextBlock { Tag = "hi", Style = style };

            Assert.Equal("hi", textBlock.Text);
        });
    }

    private sealed class PassThroughConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value;

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
