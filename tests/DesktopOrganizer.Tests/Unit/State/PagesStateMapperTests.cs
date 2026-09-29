using System.Text.Json;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.Tests.Unit.State;

/// <summary>
/// Unit tests for <see cref="PagesStateMapper"/> (ADR-043, docs/PLANS/page-display-names.md §3.3):
/// schema-v6 <see cref="PagesStateDto"/> &lt;-&gt; live <see cref="PageDescriptor"/> list round-tripping,
/// and every degrade-instead-of-throw path for corrupted/hand-edited data.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PagesStateMapperTests
{
    // ── FromDto ──────────────────────────────────────────────────────────────────

    /// <summary>Verifies that a <see langword="null"/> DTO falls back to a lone unnamed "default" page.</summary>
    [Fact]
    public void FromDto_NullDto_FallsBackToDefault()
    {
        var (pages, currentPageId) = PagesStateMapper.FromDto(null);

        Assert.Equal([new PageDescriptor("default", null)], pages);
        Assert.Equal("default", currentPageId);
    }

    /// <summary>Verifies that a DTO with a <see langword="null"/> <c>Items</c> list falls back to a lone unnamed "default" page.</summary>
    [Fact]
    public void FromDto_NullItems_FallsBackToDefault()
    {
        var dto = new PagesStateDto { Items = null!, CurrentPageId = "default" };

        var (pages, currentPageId) = PagesStateMapper.FromDto(dto);

        Assert.Equal([new PageDescriptor("default", null)], pages);
        Assert.Equal("default", currentPageId);
    }

    /// <summary>Verifies that a <see langword="null"/> element in <c>Items</c> is skipped rather than throwing.</summary>
    [Fact]
    public void FromDto_NullElement_IsSkipped()
    {
        var dto = new PagesStateDto { Items = [null!, new PageStateDto { Id = "work", Name = "Work" }], CurrentPageId = "work" };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        Assert.Equal([new PageDescriptor("work", "Work")], pages);
    }

    /// <summary>Verifies that an item with a blank/whitespace id is skipped.</summary>
    /// <param name="blankId">A blank/whitespace-only id.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromDto_BlankId_IsSkipped(string blankId)
    {
        var dto = new PagesStateDto { Items = [new PageStateDto { Id = blankId }, new PageStateDto { Id = "default" }] };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        Assert.Equal([new PageDescriptor("default", null)], pages);
    }

    /// <summary>Verifies that duplicate ids (ordinal) keep only the first entry.</summary>
    [Fact]
    public void FromDto_DuplicateIds_FirstWins()
    {
        var dto = new PagesStateDto
        {
            Items = [new PageStateDto { Id = "work", Name = "First" }, new PageStateDto { Id = "work", Name = "Second" }],
        };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        var page = Assert.Single(pages);
        Assert.Equal("First", page.Name);
    }

    /// <summary>Verifies that a non-default page with no stored name (corrupted/hand-edited data) is repaired to <c>Name = Id</c>.</summary>
    [Fact]
    public void FromDto_NonDefaultPageWithNoName_RepairsNameToId()
    {
        var dto = new PagesStateDto { Items = [new PageStateDto { Id = "work", Name = null }] };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        Assert.Equal("work", Assert.Single(pages).Name);
    }

    /// <summary>Verifies that a whitespace-only stored name is treated the same as an absent name.</summary>
    [Fact]
    public void FromDto_WhitespaceName_TreatedAsAbsent()
    {
        var defaultDto = new PagesStateDto { Items = [new PageStateDto { Id = "default", Name = "   " }] };
        var otherDto = new PagesStateDto { Items = [new PageStateDto { Id = "work", Name = "   " }] };

        var (defaultPages, _) = PagesStateMapper.FromDto(defaultDto);
        var (otherPages, _) = PagesStateMapper.FromDto(otherDto);

        Assert.Null(Assert.Single(defaultPages).Name);
        Assert.Equal("work", Assert.Single(otherPages).Name);
    }

    /// <summary>Verifies that the "default" page is allowed to keep no stored name.</summary>
    [Fact]
    public void FromDto_DefaultPageWithNoName_StaysUnnamed()
    {
        var dto = new PagesStateDto { Items = [new PageStateDto { Id = "default", Name = null }] };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        Assert.Null(Assert.Single(pages).Name);
    }

    /// <summary>Verifies that an empty <c>Items</c> list (every entry filtered out, or genuinely empty) falls back to a lone unnamed "default" page.</summary>
    [Fact]
    public void FromDto_EmptyAfterFilter_FallsBackToDefault()
    {
        var dto = new PagesStateDto { Items = [new PageStateDto { Id = "  " }] };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        Assert.Equal([new PageDescriptor("default", null)], pages);
    }

    /// <summary>
    /// Verifies that a non-empty page list missing "default" is left as-is — inserting "default" is
    /// <c>PageManagerService.EnsureDefaultPage</c>'s job, not the mapper's.
    /// </summary>
    [Fact]
    public void FromDto_MissingDefaultInNonEmptyList_DoesNotInsertDefault()
    {
        var dto = new PagesStateDto { Items = [new PageStateDto { Id = "work", Name = "Work" }] };

        var (pages, _) = PagesStateMapper.FromDto(dto);

        Assert.Equal([new PageDescriptor("work", "Work")], pages);
    }

    /// <summary>Verifies that a blank <c>CurrentPageId</c> falls back to "default".</summary>
    /// <param name="blank">A <see langword="null"/>/blank/whitespace-only current page id.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromDto_BlankCurrentPageId_FallsBackToDefault(string? blank)
    {
        var dto = new PagesStateDto { Items = [new PageStateDto { Id = "default" }], CurrentPageId = blank! };

        var (_, currentPageId) = PagesStateMapper.FromDto(dto);

        Assert.Equal("default", currentPageId);
    }

    // ── ToDto ────────────────────────────────────────────────────────────────────

    /// <summary>Verifies that <see cref="PagesStateMapper.ToDto"/> maps every page's id/name and the current page id verbatim.</summary>
    [Fact]
    public void ToDto_MapsEveryFieldVerbatim()
    {
        IReadOnlyList<PageDescriptor> pages = [new PageDescriptor("default", null), new PageDescriptor("work", "Work")];

        var dto = PagesStateMapper.ToDto(pages, "work");

        Assert.Equal(2, dto.Items.Count);
        Assert.Equal("default", dto.Items[0].Id);
        Assert.Null(dto.Items[0].Name);
        Assert.Equal("work", dto.Items[1].Id);
        Assert.Equal("Work", dto.Items[1].Name);
        Assert.Equal("work", dto.CurrentPageId);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies a full <c>ToDto</c> -&gt; JSON (<see cref="StateJson.Options"/>) -&gt; <c>FromDto</c>
    /// round trip, and that an unnamed "default" page never writes a <c>name</c> key on disk.
    /// </summary>
    [Fact]
    public void RoundTrip_ThroughJson_PreservesPagesAndOmitsUnnamedDefaultNameKey()
    {
        IReadOnlyList<PageDescriptor> pages =
        [
            new PageDescriptor("default", null),
            new PageDescriptor("3f2a9c0e5b7d4e1a9c3b2d1e0f4a5b6c", "Проекты"),
        ];

        var dto = PagesStateMapper.ToDto(pages, "default");
        var json = JsonSerializer.SerializeToNode(dto, StateJson.Options)!.AsObject();

        var items = json["items"]!.AsArray();
        Assert.False(items[0]!.AsObject().ContainsKey("name"));
        Assert.Equal("Проекты", items[1]!["name"]!.GetValue<string>());

        var roundTrippedDto = json.Deserialize<PagesStateDto>(StateJson.Options);
        var (roundTrippedPages, roundTrippedCurrentPageId) = PagesStateMapper.FromDto(roundTrippedDto);

        Assert.Equal(pages, roundTrippedPages);
        Assert.Equal("default", roundTrippedCurrentPageId);
    }
}
