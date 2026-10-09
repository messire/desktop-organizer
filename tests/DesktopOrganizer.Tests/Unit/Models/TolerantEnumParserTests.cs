using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="TolerantEnumParser"/> (ADR-024 Amendment "Additive revisions",
/// docs/PLANS/schema-additive-revision-v7.md §3): case-insensitive member names only; numeric strings, comma lists and
/// unknown names are unrecognised and fall back to the supplied default; absent input is not "unknown".
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TolerantEnumParserTests
{
    /// <summary>Verifies that defined member names parse, ignoring case, and are reported as recognised.</summary>
    /// <param name="raw">The input string.</param>
    /// <param name="expected">The expected member.</param>
    [Theory]
    [InlineData("Maximized", WindowState.Maximized)]
    [InlineData("maximized", WindowState.Maximized)]
    [InlineData("MINIMIZED", WindowState.Minimized)]
    [InlineData("Normal", WindowState.Normal)]
    public void Parse_DefinedMember_IsRecognised(string raw, WindowState expected)
    {
        var parsed = TolerantEnumParser.Parse(raw, WindowState.Normal, out var recognized);

        Assert.Equal(expected, parsed);
        Assert.True(recognized);
    }

    /// <summary>Verifies that anything that is not a defined member name yields the fallback and is flagged unrecognised.</summary>
    /// <param name="raw">The input string.</param>
    [Theory]
    [InlineData("Floating")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("Normal,Maximized")]
    [InlineData("99")]
    [InlineData("   ")]
    public void Parse_NotAMember_ReturnsFallback_Unrecognised(string raw)
    {
        var parsed = TolerantEnumParser.Parse(raw, WindowState.Minimized, out var recognized);

        Assert.Equal(WindowState.Minimized, parsed);
        Assert.False(recognized);
    }

    /// <summary>Verifies that absent input is the fallback but not an "unknown value" to preserve.</summary>
    /// <param name="raw">The input string.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_AbsentInput_ReturnsFallback_Recognised(string? raw)
    {
        var parsed = TolerantEnumParser.Parse(raw, WindowState.Maximized, out var recognized);

        Assert.Equal(WindowState.Maximized, parsed);
        Assert.True(recognized);
    }

    /// <summary>
    /// Verifies <see cref="TolerantEnumParser.IsRecognized"/> for the pre-existing parsers' shape: a parse that did not
    /// fall back, a fallback caused by the input literally naming the fallback member, and absent input are
    /// recognised; a fallback caused by something else is not.
    /// </summary>
    [Fact]
    public void IsRecognized_DistinguishesFallbackCauses()
    {
        Assert.True(TolerantEnumParser.IsRecognized("GridAligned", PanelGeometryMode.GridAligned, PanelGeometryMode.LegacyFreeform));
        Assert.True(TolerantEnumParser.IsRecognized("legacyfreeform", PanelGeometryMode.LegacyFreeform, PanelGeometryMode.LegacyFreeform));
        Assert.True(TolerantEnumParser.IsRecognized(null, PanelGeometryMode.LegacyFreeform, PanelGeometryMode.LegacyFreeform));
        Assert.True(TolerantEnumParser.IsRecognized(string.Empty, PanelGeometryMode.LegacyFreeform, PanelGeometryMode.LegacyFreeform));
        Assert.False(TolerantEnumParser.IsRecognized("Masonry", PanelGeometryMode.LegacyFreeform, PanelGeometryMode.LegacyFreeform));
        Assert.False(TolerantEnumParser.IsRecognized("LegacyFreeform,GridAligned", PanelGeometryMode.LegacyFreeform, PanelGeometryMode.LegacyFreeform));
    }
}
