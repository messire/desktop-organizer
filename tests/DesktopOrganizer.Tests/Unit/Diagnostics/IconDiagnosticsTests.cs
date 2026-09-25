using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="IconDiagnostics.SafeExtension"/>.
/// </summary>
/// <remarks>
/// M2 fix (P08 fix cycle 1, 2026-09-22, ADR-027 Amendment): <see cref="System.IO.Path.GetExtension(string)"/>
/// treats anything after the last dot as an "extension", including a dot that's just part of a
/// directory or file name with no relation to a real file type (e.g. a username like
/// <c>ivan.petrov</c>) — the privacy invariant behind the <c>icon</c> diagnostics category
/// (docs/Refactoring/Testing/diagnostics-log-events.md) requires this helper to never let such a
/// fragment leak into the JSONL log testers attach to bug reports.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class IconDiagnosticsTests
{
    /// <summary>
    /// Verifies that <see cref="IconDiagnostics.SafeExtension"/> returns the real, lower-cased
    /// extension for a path whose fragment after the last dot is a recognized file extension.
    /// </summary>
    /// <param name="path">The path passed to <c>SafeExtension</c>.</param>
    /// <param name="expected">The expected lower-cased extension.</param>
    [Theory]
    [InlineData(@"C:\Windows\notepad.exe", ".exe")]
    [InlineData(@"C:\Users\me\Desktop\shortcut.lnk", ".lnk")]
    [InlineData(@"C:\Users\me\Desktop\READ.TXT", ".txt")]
    [InlineData(@"C:\Users\me\Desktop\archive.tar", ".tar")]
    public void SafeExtension_RecognizedExtension_ReturnsLowerCasedExtension(string path, string expected)
    {
        Assert.Equal(expected, IconDiagnostics.SafeExtension(path));
    }

    /// <summary>
    /// Verifies that <see cref="IconDiagnostics.SafeExtension"/> returns an empty string for a path
    /// with no extension at all.
    /// </summary>
    [Fact]
    public void SafeExtension_NoExtension_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, IconDiagnostics.SafeExtension(@"C:\Users\me\Desktop\README"));
    }

    /// <summary>
    /// Verifies that <see cref="IconDiagnostics.SafeExtension"/> returns an empty string for an empty
    /// path.
    /// </summary>
    [Fact]
    public void SafeExtension_EmptyPath_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, IconDiagnostics.SafeExtension(string.Empty));
    }

    // ── M2: privacy allow-list — a fragment that doesn't structurally look like a real extension ────
    // (punctuation, non-ASCII, or implausibly long) never leaks verbatim. NOTE: a SHORT, alphanumeric
    // personal-name-shaped fragment on a FILE's own name (e.g. "report.smith") is structurally
    // indistinguishable from a real (if obscure) extension and is NOT filtered by this allow-list —
    // that residual case is documented, not silently missed; see the Amendment in ADR-027 and this
    // class's own remarks. The DIRECTORY case below (ivan.petrov as a folder's own name) is the one
    // the original finding's exact example maps to, and IS fully closed — see
    // SafeExtension_DirectoryWithDotInOwnName_ReturnsEmpty_NeverTheNameFragment.

    /// <summary>
    /// Verifies that <see cref="IconDiagnostics.SafeExtension"/> maps a dotted fragment that does not
    /// structurally look like a real extension (punctuation, non-ASCII, or implausibly long) to
    /// <see cref="IconDiagnostics.OtherExtension"/> instead of leaking it verbatim.
    /// </summary>
    /// <param name="path">The path whose trailing dotted fragment is not a plausible real extension.</param>
    [Theory]
    [InlineData(@"C:\Users\me\Desktop\file.a-b")]
    [InlineData(@"C:\Users\me\Desktop\file.日本語")]
    [InlineData(@"C:\Users\me\Desktop\file.reallylongextensionthatisnotreal")]
    public void SafeExtension_UnrecognizedDottedFragment_DoesNotLeakItVerbatim_ReturnsOtherBucket(string path)
    {
        var result = IconDiagnostics.SafeExtension(path);

        Assert.Equal(IconDiagnostics.OtherExtension, result);
    }

    /// <summary>
    /// Documents the residual, accepted limitation of <see cref="IconDiagnostics.SafeExtension"/>: a
    /// short, alphanumeric fragment structurally indistinguishable from a real extension is accepted
    /// as if it were one.
    /// </summary>
    [Fact]
    public void SafeExtension_ShortAlphanumericFileNameFragment_IsAcceptedAsIfItWereARealExtension()
    {
        // Documents the residual, accepted limitation (see the Theory's remarks above): a fragment
        // that's structurally indistinguishable from a real extension (short, alphanumeric) is not —
        // and cannot be, without a maintained allow-list of "real" extensions — filtered out.
        Assert.Equal(".smith", IconDiagnostics.SafeExtension(@"report.smith"));
    }

    /// <summary>
    /// Verifies that <see cref="IconDiagnostics.SafeExtension"/> returns an empty string for a
    /// directory whose own name contains a dot (e.g. a Windows username folder like
    /// <c>ivan.petrov</c>), never leaking the name fragment as if it were an extension.
    /// </summary>
    [Fact]
    public void SafeExtension_DirectoryWithDotInOwnName_ReturnsEmpty_NeverTheNameFragment()
    {
        // A directory's own name can legitimately contain a dot (e.g. a Windows username folder like
        // "ivan.petrov") that has nothing to do with a file type at all.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"IconDiagnosticsTests.ivan.petrov.{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);

        try
        {
            var result = IconDiagnostics.SafeExtension(dir);

            Assert.Equal(string.Empty, result);
        }
        finally
        {
            System.IO.Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Verifies that <see cref="IconDiagnostics.SafeExtension"/> matches the real-extension allow list
    /// case-insensitively.
    /// </summary>
    [Fact]
    public void SafeExtension_RealExtensionIsCaseInsensitive_MatchesAllowList()
    {
        Assert.Equal(".exe", IconDiagnostics.SafeExtension(@"C:\Windows\NOTEPAD.EXE"));
    }
}
