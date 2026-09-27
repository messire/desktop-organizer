using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for the internal <c>LocalizedValueConverter</c>/<c>LocBindingConversion</c> pipeline every
/// <c>{loc:LocBinding}</c> builds (docs/PLANS/localization.md §2.4/L-9). Reached only via
/// <c>InternalsVisibleTo</c> — exercises the CONVERT pipeline directly rather than through a full WPF
/// binding, which is cheaper and pins the exact conveyor-belt order the plan specifies: inner
/// <c>Converter</c> first, then <c>FormatKey</c>, with the LIVE culture (<c>values[1]</c>) used
/// throughout, never the <c>culture</c> argument WPF itself would pass.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LocalizedValueConverterTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>No inner converter, no format key: the source value passes through unchanged.</summary>
    [Fact]
    public void Convert_NoConverterNoFormatKey_PassesValueThrough()
    {
        var sut = LocalizedValueConverter.Instance;
        var conversion = new LocBindingConversion(Converter: null, ConverterParameter: null, FormatKey: null, FallbackValue: null);

        var result = sut.Convert(new object[] { "hello", English }, typeof(object), conversion, CultureInfo.InvariantCulture);

        Assert.Equal("hello", result);
    }

    /// <summary>The inner converter runs with the LIVE culture (<c>values[1]</c>), not WPF's own <c>culture</c> argument.</summary>
    [Fact]
    public void Convert_WithConverter_RunsInnerConverterWithLiveCulture()
    {
        var sut = LocalizedValueConverter.Instance;
        var innerConverter = new RecordingConverter();
        var conversion = new LocBindingConversion(innerConverter, ConverterParameter: "param", FormatKey: null, FallbackValue: null);

        var result = sut.Convert(new object[] { "value", Russian }, typeof(object), conversion, English);

        Assert.Equal("value!", result);
        Assert.Equal(Russian, innerConverter.LastCulture);
        Assert.Equal("param", innerConverter.LastParameter);
    }

    /// <summary>A <c>FormatKey</c> is applied AFTER the inner converter, with the live culture.</summary>
    [Fact]
    public void Convert_ConverterAndFormatKey_AppliesFormatKeyAfterConverter()
    {
        var sut = LocalizedValueConverter.Instance;
        var innerConverter = new RecordingConverter();
        var conversion = new LocBindingConversion(innerConverter, ConverterParameter: null, FormatKey: "Language_FollowWindows_Format", FallbackValue: null);

        var result = sut.Convert(new object[] { "Русский", Russian }, typeof(object), conversion, English);

        Assert.Equal("Как в Windows (Русский!)", result);
    }

    /// <summary>A <c>FormatKey</c> with no inner converter formats the raw source value.</summary>
    [Fact]
    public void Convert_FormatKeyOnly_FormatsSourceValue()
    {
        var sut = LocalizedValueConverter.Instance;
        var conversion = new LocBindingConversion(Converter: null, ConverterParameter: null, FormatKey: "Language_FollowWindows_Format", FallbackValue: null);

        var result = sut.Convert(new object[] { "English", English }, typeof(object), conversion, Russian);

        Assert.Equal("Windows default (English)", result);
    }

    /// <summary><see cref="DependencyProperty.UnsetValue"/> on the source value returns <c>FallbackValue</c>.</summary>
    [Fact]
    public void Convert_UnsetSourceValue_ReturnsFallback()
    {
        var sut = LocalizedValueConverter.Instance;
        var conversion = new LocBindingConversion(Converter: null, ConverterParameter: null, FormatKey: null, FallbackValue: "fallback");

        var result = sut.Convert(new object[] { DependencyProperty.UnsetValue, English }, typeof(object), conversion, English);

        Assert.Equal("fallback", result);
    }

    /// <summary>With no <c>FallbackValue</c>, an unset source value returns <see cref="DependencyProperty.UnsetValue"/> itself.</summary>
    [Fact]
    public void Convert_UnsetSourceValue_NoFallback_ReturnsUnsetValue()
    {
        var sut = LocalizedValueConverter.Instance;
        var conversion = new LocBindingConversion(Converter: null, ConverterParameter: null, FormatKey: null, FallbackValue: null);

        var result = sut.Convert(new object[] { DependencyProperty.UnsetValue, English }, typeof(object), conversion, English);

        Assert.Equal(DependencyProperty.UnsetValue, result);
    }

    /// <summary>Never throws: an inner converter that throws is swallowed and turned into <c>FallbackValue</c>.</summary>
    [Fact]
    public void Convert_InnerConverterThrows_ReturnsFallback_DoesNotThrow()
    {
        var sut = LocalizedValueConverter.Instance;
        var conversion = new LocBindingConversion(new ThrowingConverter(), ConverterParameter: null, FormatKey: null, FallbackValue: "safe-fallback");

        var result = sut.Convert(new object[] { "value", English }, typeof(object), conversion, English);

        Assert.Equal("safe-fallback", result);
    }

    /// <summary>No values at all returns <c>FallbackValue</c>, never throws.</summary>
    [Fact]
    public void Convert_EmptyValues_ReturnsFallback()
    {
        var sut = LocalizedValueConverter.Instance;
        var conversion = new LocBindingConversion(Converter: null, ConverterParameter: null, FormatKey: null, FallbackValue: "empty-fallback");

        var result = sut.Convert(Array.Empty<object>(), typeof(object), conversion, English);

        Assert.Equal("empty-fallback", result);
    }

    /// <summary><see cref="IMultiValueConverter.ConvertBack"/> is one-way only.</summary>
    [Fact]
    public void ConvertBack_Throws()
    {
        var sut = LocalizedValueConverter.Instance;

        Assert.Throws<NotSupportedException>(() => sut.ConvertBack("x", new[] { typeof(object) }, null, English));
    }

    private sealed class RecordingConverter : IValueConverter
    {
        public CultureInfo? LastCulture { get; private set; }

        public object? LastParameter { get; private set; }

        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            LastCulture = culture;
            LastParameter = parameter;
            return $"{value}!";
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new InvalidOperationException("boom");

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
