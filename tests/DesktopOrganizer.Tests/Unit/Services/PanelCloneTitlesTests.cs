using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PanelCloneTitles"/> (ADR-045, «Panel clone to page», backlog 1.x §8b): the
/// Explorer-style <c>"{0} — copy"</c>/<c>"{0} — copy (2)"</c> collision chain, localized once at the
/// moment the clone is created (ADR-032 D-5).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCloneTitlesTests
{
    private readonly ILocalizationService _english = TestLocalization.English;
    private readonly ILocalizationService _russian = TestLocalization.Russian;

    /// <summary>Without a collision on the destination page, the clone keeps the source's own title verbatim.</summary>
    [Fact]
    public void Resolve_NoCollisionOnTargetPage_ReturnsSourceTitleVerbatim()
    {
        var title = PanelCloneTitles.Resolve("Notes", ["Other", "Something Else"], _english);

        Assert.Equal("Notes", title);
    }

    /// <summary>An empty destination page never collides.</summary>
    [Fact]
    public void Resolve_EmptyTargetPage_ReturnsSourceTitleVerbatim()
    {
        var title = PanelCloneTitles.Resolve("Notes", [], _english);

        Assert.Equal("Notes", title);
    }

    /// <summary>An exact collision resolves to the localized "copy" suffix.</summary>
    [Fact]
    public void Resolve_ExactCollision_ReturnsCopyFormat()
    {
        var title = PanelCloneTitles.Resolve("Notes", ["Notes"], _english);

        Assert.Equal("Notes — copy", title);
    }

    /// <summary>
    /// Comparison against existing titles is trimmed and case-insensitive, so a differently-cased,
    /// padded duplicate still counts as a collision — this is guaranteed for a same-page clone, where the
    /// source's own title is always among <c>titlesOnTargetPage</c>.
    /// </summary>
    [Fact]
    public void Resolve_CollisionCaseInsensitiveAndTrimmed_TreatsAsCollision()
    {
        var title = PanelCloneTitles.Resolve("Notes", [" notes "], _english);

        Assert.Equal("Notes — copy", title);
    }

    /// <summary>If the "copy" title itself also collides, the chain continues to "copy (2)".</summary>
    [Fact]
    public void Resolve_CopyTitleAlsoCollides_ReturnsNumberedCopy2()
    {
        var title = PanelCloneTitles.Resolve("Notes", ["Notes", "Notes — copy"], _english);

        Assert.Equal("Notes — copy (2)", title);
    }

    /// <summary>The chain keeps counting up past "(2)" as long as each candidate collides.</summary>
    [Fact]
    public void Resolve_Copy2AlsoCollides_ReturnsNumberedCopy3()
    {
        var title = PanelCloneTitles.Resolve("Notes", ["Notes", "Notes — copy", "Notes — copy (2)"], _english);

        Assert.Equal("Notes — copy (3)", title);
    }

    /// <summary>A gap in the numbered chain (e.g. "(2)" was renamed away) is still filled from the lowest free number, not skipped.</summary>
    [Fact]
    public void Resolve_NumberedChainHasGap_StillStartsFromTwo()
    {
        var title = PanelCloneTitles.Resolve("Notes", ["Notes", "Notes — copy (3)"], _english);

        Assert.Equal("Notes — copy", title);
    }

    /// <summary>Localizes through the given <see cref="ILocalizationService"/> — Russian produces the Russian suffix.</summary>
    [Fact]
    public void Resolve_RussianLocalization_UsesRussianCopySuffix()
    {
        var title = PanelCloneTitles.Resolve("Заметки", ["Заметки"], _russian);

        Assert.Equal("Заметки — копия", title);
    }

    /// <summary>Russian numbered chain, same shape as the English one.</summary>
    [Fact]
    public void Resolve_RussianLocalization_NumberedChain()
    {
        var title = PanelCloneTitles.Resolve("Заметки", ["Заметки", "Заметки — копия"], _russian);

        Assert.Equal("Заметки — копия (2)", title);
    }
}
