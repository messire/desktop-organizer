using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="ManagedItemName.StripLegacyGuidSuffix"/> (ADR-013 Decision 3):
/// pure string-only stripping of a legacy flat-layout managed-storage GUID suffix
/// (<c>_&lt;32 hex&gt;</c>) from a bare file or folder name, with no file-system access.
/// </summary>
[Trait(UnitTests, nameof(Core))]
public class ManagedItemNameTests
{
    private const string LowerGuid = "9f3c1a2b3c4d5e6f7a8b9c0d1e2f3a4b";
    private const string UpperGuid = "9F3C1A2B3C4D5E6F7A8B9C0D1E2F3A4B";
    private const string MixedGuid = "9f3C1a2B3c4D5e6F7a8B9c0D1e2F3a4B";

    // ── With extension (file) ───────────────────────────────────────────────

    [Fact]
    public void StripLegacyGuidSuffix_FileWithExtension_StripsSuffixKeepsExtension()
    {
        var input = $"Report_{LowerGuid}.docx";
        Assert.Equal("Report.docx", ManagedItemName.StripLegacyGuidSuffix(input));
    }

    [Fact]
    public void StripLegacyGuidSuffix_FileWithMultiDotExtension_StripsOnlyTheGuidSuffix()
    {
        var input = $"archive.tar_{LowerGuid}.gz";
        Assert.Equal("archive.tar.gz", ManagedItemName.StripLegacyGuidSuffix(input));
    }

    // ── Without extension (folder) ──────────────────────────────────────────

    [Fact]
    public void StripLegacyGuidSuffix_FolderNameNoExtension_StripsSuffix()
    {
        var input = $"MyFolder_{LowerGuid}";
        Assert.Equal("MyFolder", ManagedItemName.StripLegacyGuidSuffix(input));
    }

    // ── Not a GUID suffix at all ─────────────────────────────────────────────

    [Fact]
    public void StripLegacyGuidSuffix_TrailingUnderscoreNotHex_ReturnsUnchanged()
    {
        var input = "Report_finalversion.docx";
        Assert.Equal(input, ManagedItemName.StripLegacyGuidSuffix(input));
    }

    [Fact]
    public void StripLegacyGuidSuffix_NoUnderscoreAtAll_ReturnsUnchanged()
    {
        var input = "PlainName.docx";
        Assert.Equal(input, ManagedItemName.StripLegacyGuidSuffix(input));
    }

    // ── Boundary: 31 vs 33 hex characters ────────────────────────────────────

    [Fact]
    public void StripLegacyGuidSuffix_31HexChars_ReturnsUnchanged()
    {
        var hex31 = LowerGuid[..31];
        var input = $"Report_{hex31}.docx";
        Assert.Equal(input, ManagedItemName.StripLegacyGuidSuffix(input));
    }

    [Fact]
    public void StripLegacyGuidSuffix_33HexChars_ReturnsUnchanged()
    {
        // 33 hex chars after the underscore: the regex is anchored to the END of the string and
        // requires EXACTLY 32 hex chars preceded by "_" — with 33 hex chars, the character
        // immediately before the last 32 of them is itself a hex digit, not "_", so there's no match.
        var hex33 = LowerGuid + "a";
        var input = $"Report_{hex33}.docx";
        Assert.Equal(input, ManagedItemName.StripLegacyGuidSuffix(input));
    }

    [Fact]
    public void StripLegacyGuidSuffix_Exactly32HexChars_Strips()
    {
        var input = $"Report_{LowerGuid}.docx";
        Assert.Equal("Report.docx", ManagedItemName.StripLegacyGuidSuffix(input));
    }

    // ── Case-insensitivity of the hex characters ─────────────────────────────

    [Fact]
    public void StripLegacyGuidSuffix_UpperCaseHex_Strips()
    {
        var input = $"Report_{UpperGuid}.docx";
        Assert.Equal("Report.docx", ManagedItemName.StripLegacyGuidSuffix(input));
    }

    [Fact]
    public void StripLegacyGuidSuffix_MixedCaseHex_Strips()
    {
        var input = $"Report_{MixedGuid}.docx";
        Assert.Equal("Report.docx", ManagedItemName.StripLegacyGuidSuffix(input));
    }

    // ── Degenerate case: stripping would leave an empty base name ───────────

    [Fact]
    public void StripLegacyGuidSuffix_NameIsOnlyTheGuidSuffix_ReturnsInputUnchanged()
    {
        // "_<32 hex>.docx" — stripping the matched suffix would leave an empty base name, which
        // isn't a plausible original name, so the whole input is returned unchanged instead.
        var input = $"_{LowerGuid}.docx";
        Assert.Equal(input, ManagedItemName.StripLegacyGuidSuffix(input));
    }

    [Fact]
    public void StripLegacyGuidSuffix_NameIsOnlyTheGuidSuffixNoExtension_ReturnsInputUnchanged()
    {
        var input = $"_{LowerGuid}";
        Assert.Equal(input, ManagedItemName.StripLegacyGuidSuffix(input));
    }

    // ── Null-free / empty-input safety ───────────────────────────────────────

    [Fact]
    public void StripLegacyGuidSuffix_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ManagedItemName.StripLegacyGuidSuffix(string.Empty));
    }
}
