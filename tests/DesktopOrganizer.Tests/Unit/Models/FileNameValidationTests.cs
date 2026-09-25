using System.IO;
using DesktopOrganizer.Core;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="FileNameValidation.IsValid"/> — pure, file-system-free validation of a
/// candidate bare file/folder name, used to reject a rename as <c>RenameOutcome.InvalidName</c>
/// before any I/O is attempted (ADR-013 Decision 4).
/// </summary>
[Trait(UnitTests, nameof(Core))]
public class FileNameValidationTests
{
    // ── Empty / whitespace ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects an empty string.
    /// </summary>
    [Fact]
    public void IsValid_EmptyString_ReturnsFalse()
    {
        Assert.False(FileNameValidation.IsValid(string.Empty));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects a name consisting only of
    /// whitespace, whatever the whitespace character.
    /// </summary>
    /// <param name="name">The whitespace-only candidate name.</param>
    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsValid_WhitespaceOnly_ReturnsFalse(string name)
    {
        Assert.False(FileNameValidation.IsValid(name));
    }

    // ── Invalid characters, including path separators (traversal defense) ──

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects a name containing any character
    /// from <see cref="Path.GetInvalidFileNameChars"/>.
    /// </summary>
    [Fact]
    public void IsValid_EveryInvalidFileNameChar_ReturnsFalse()
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            var candidate = "a" + c + "b";
            Assert.False(FileNameValidation.IsValid(candidate), $"Expected '{candidate}' (contains U+{(int)c:X4}) to be invalid.");
        }
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects names containing directory
    /// separators or traversal segments — this is what stops a rename from smuggling a directory
    /// component or escaping the target directory.
    /// </summary>
    /// <param name="name">The candidate name containing path separators.</param>
    [Theory]
    [InlineData(@"..\..\evil.txt")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData(@"C:\Windows\System32")]
    public void IsValid_ContainsPathSeparators_ReturnsFalse(string name)
    {
        // Path.GetInvalidFileNameChars() already includes both directory separators — this is what
        // stops a rename from smuggling a directory component or escaping the target directory.
        Assert.False(FileNameValidation.IsValid(name));
    }

    // ── "." and ".." ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects the single-dot name <c>"."</c>.
    /// </summary>
    [Fact]
    public void IsValid_SingleDot_ReturnsFalse()
    {
        Assert.False(FileNameValidation.IsValid("."));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects the double-dot name <c>".."</c>.
    /// </summary>
    [Fact]
    public void IsValid_DoubleDot_ReturnsFalse()
    {
        Assert.False(FileNameValidation.IsValid(".."));
    }

    // ── Trailing space / trailing dot ────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects a name with a trailing space,
    /// since Windows silently strips it.
    /// </summary>
    [Fact]
    public void IsValid_TrailingSpace_ReturnsFalse()
    {
        Assert.False(FileNameValidation.IsValid("Report.txt "));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects a name with a trailing dot after
    /// an extension, since Windows silently strips it.
    /// </summary>
    [Fact]
    public void IsValid_TrailingDot_ReturnsFalse()
    {
        Assert.False(FileNameValidation.IsValid("Report.txt."));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects a name with a trailing dot and no
    /// extension, since Windows silently strips it.
    /// </summary>
    [Fact]
    public void IsValid_TrailingDotNoExtension_ReturnsFalse()
    {
        Assert.False(FileNameValidation.IsValid("Report."));
    }

    // ── Reserved Windows device names, case-insensitive, with/without extension ─

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects every reserved Windows device
    /// name (CON/PRN/AUX/NUL/COM1-9/LPT1-9) given bare, with no extension.
    /// </summary>
    /// <param name="reserved">The bare reserved device name.</param>
    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM2")]
    [InlineData("COM3")]
    [InlineData("COM4")]
    [InlineData("COM5")]
    [InlineData("COM6")]
    [InlineData("COM7")]
    [InlineData("COM8")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT2")]
    [InlineData("LPT3")]
    [InlineData("LPT4")]
    [InlineData("LPT5")]
    [InlineData("LPT6")]
    [InlineData("LPT7")]
    [InlineData("LPT8")]
    [InlineData("LPT9")]
    public void IsValid_ReservedDeviceName_Bare_ReturnsFalse(string reserved)
    {
        Assert.False(FileNameValidation.IsValid(reserved));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects reserved Windows device names
    /// regardless of letter casing.
    /// </summary>
    /// <param name="reserved">The reserved device name in a non-canonical casing.</param>
    [Theory]
    [InlineData("con")]
    [InlineData("Con")]
    [InlineData("CoN")]
    [InlineData("com1")]
    [InlineData("Lpt9")]
    public void IsValid_ReservedDeviceName_DifferentCase_ReturnsFalse(string reserved)
    {
        Assert.False(FileNameValidation.IsValid(reserved));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> rejects a reserved Windows device name
    /// even when it carries a file extension.
    /// </summary>
    /// <param name="reserved">The reserved device name with an extension appended.</param>
    [Theory]
    [InlineData("CON.txt")]
    [InlineData("con.txt")]
    [InlineData("COM1.docx")]
    [InlineData("lpt9.log")]
    public void IsValid_ReservedDeviceName_WithExtension_ReturnsFalse(string reserved)
    {
        Assert.False(FileNameValidation.IsValid(reserved));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> accepts a name that merely starts with a
    /// reserved device name's letters (e.g. "CONTACT") without being an exact match.
    /// </summary>
    [Fact]
    public void IsValid_NotAReservedNameButLooksSimilar_ReturnsTrue()
    {
        // "CONTACT" starts with "CON" but is not itself the reserved stem — only an exact
        // (extension-stripped) match to a reserved name should be rejected.
        Assert.True(FileNameValidation.IsValid("CONTACT.txt"));
    }

    // ── Valid names — with and without extension ─────────────────────────────

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> accepts an ordinary name with an
    /// extension.
    /// </summary>
    [Fact]
    public void IsValid_PlainNameWithExtension_ReturnsTrue()
    {
        Assert.True(FileNameValidation.IsValid("Report.docx"));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> accepts an ordinary folder-style name
    /// with no extension.
    /// </summary>
    [Fact]
    public void IsValid_PlainNameNoExtension_ReturnsTrue()
    {
        Assert.True(FileNameValidation.IsValid("MyFolder"));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> accepts a name containing interior
    /// spaces.
    /// </summary>
    [Fact]
    public void IsValid_NameWithSpacesInMiddle_ReturnsTrue()
    {
        Assert.True(FileNameValidation.IsValid("My Report Final.docx"));
    }

    /// <summary>
    /// Verifies that <see cref="FileNameValidation.IsValid"/> accepts a name with a leading space —
    /// only a trailing space/dot is rejected, since only those are silently stripped by Windows.
    /// </summary>
    [Fact]
    public void IsValid_NameWithLeadingSpace_ReturnsTrue()
    {
        // Only a TRAILING space/dot is rejected (Windows silently strips those) — a leading space is
        // preserved as-is and is a legal (if unusual) file name.
        Assert.True(FileNameValidation.IsValid(" Report.docx"));
    }
}
