using System.Text.Json;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PageManagerService"/>.
/// Dependencies <see cref="IWindowManager"/> and <see cref="IAppSettings"/> are mocked.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PageManagerServiceTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();

    private PageManagerService BuildSut(
        IEnumerable<ChildWindowModel>? panels = null,
        string pagesJson = "",
        string currentPage = "")
    {
        _appSettingsMock.SetupProperty(s => s.Pages, pagesJson);
        _appSettingsMock.SetupProperty(s => s.CurrentPage, currentPage);
        _appSettingsMock.Setup(s => s.Save());

        var panelList = (panels ?? Enumerable.Empty<ChildWindowModel>()).ToList();
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns(panelList);
        _windowManagerMock.Setup(wm => wm.ApplyPanelVisibility(It.IsAny<ChildWindowModel>()));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>()));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>()));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()));
        _windowManagerMock.Setup(wm => wm.SaveWindowsStateNow());

        return new PageManagerService(_windowManagerMock.Object, _appSettingsMock.Object);
    }

    // ── AddPage ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AddPage_AddsToPagesList()
    {
        // Arrange
        var sut = BuildSut();

        // Act
        sut.AddPage("work");

        // Assert
        Assert.Contains("work", sut.Pages);
    }

    [Fact]
    public void AddPage_DuplicateName_DoesNotAdd()
    {
        // Arrange
        var sut = BuildSut();
        sut.AddPage("work");

        // Act
        sut.AddPage("work");

        // Assert
        Assert.Equal(1, sut.Pages.Count(p => p == "work"));
    }

    [Fact]
    public void AddPage_DefaultPageAlwaysPresent()
    {
        var sut = BuildSut();
        Assert.Contains("default", sut.Pages);
    }

    // ── RemovePage ───────────────────────────────────────────────────────────────

    [Fact]
    public void RemovePage_Default_IsIgnored()
    {
        // Arrange
        var sut = BuildSut();
        var pageCountBefore = sut.Pages.Count;

        // Act — "default" must never be removed
        sut.RemovePage("default");

        // Assert
        Assert.Equal(pageCountBefore, sut.Pages.Count);
        Assert.Contains("default", sut.Pages);
    }

    [Fact]
    public void RemovePage_ClosesAllPanelsOnPage()
    {
        // Arrange
        var panelId = Guid.NewGuid();
        var panel = new ChildWindowModel { Id = panelId, PageId = "work" };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");

        // Act
        sut.RemovePage("work");

        // Assert — RemoveChildWindow must have been called for the panel on that page, with
        // releaseManagedItems: true since deleting a page permanently destroys its panels, and
        // deferSave: true since RemovePage batches the removals and flushes once at the end (M4).
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelId, true, true), Times.Once);
    }

    [Fact]
    public void RemovePage_ClosesAllPanelsOnPage_FlushesStateOnceAfterTheLoop()
    {
        // Regression test for the Phase 7 / Block C M4 review finding: bulk panel removal must defer
        // the per-panel persistence write and flush synchronously exactly once for the whole page,
        // instead of once per removed panel.
        var panel1 = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work" };
        var panel2 = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work" };
        var sut = BuildSut(panels: [panel1, panel2]);
        sut.AddPage("work");

        sut.RemovePage("work");

        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), true, true), Times.Exactly(2));
        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Once);
    }

    [Fact]
    public void RemovePage_NoPanelsOnPage_DoesNotFlushState()
    {
        // No RemoveChildWindow calls happened, so there is nothing to flush.
        var sut = BuildSut();
        sut.AddPage("empty");

        sut.RemovePage("empty");

        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Never);
    }

    [Fact]
    public void RemovePage_RemovesPageFromList()
    {
        // Arrange
        var sut = BuildSut();
        sut.AddPage("work");

        // Act
        sut.RemovePage("work");

        // Assert
        Assert.DoesNotContain("work", sut.Pages);
    }

    // ── SwitchTo ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchTo_HidesPreviousPagePanels_ShowsNewPagePanels()
    {
        // Arrange
        var defaultPanel = new ChildWindowModel { PageId = "default", IsVisible = true };
        var workPanel = new ChildWindowModel { PageId = "work", IsVisible = false };
        var sut = BuildSut(panels: [defaultPanel, workPanel]);
        sut.AddPage("work");

        // Act
        sut.SwitchTo("work");

        // Assert
        Assert.False(defaultPanel.IsVisible);
        Assert.True(workPanel.IsVisible);
    }

    [Fact]
    public void CurrentPage_ChangesAfterSwitch()
    {
        // Arrange
        var sut = BuildSut();
        sut.AddPage("work");

        // Act
        sut.SwitchTo("work");

        // Assert
        Assert.Equal("work", sut.CurrentPage);
    }

    [Fact]
    public void SwitchTo_NonExistentPage_DoesNotChangeCurrentPage()
    {
        // Arrange
        var sut = BuildSut();

        // Act
        sut.SwitchTo("does-not-exist");

        // Assert
        Assert.Equal("default", sut.CurrentPage);
    }

    [Fact]
    public void SwitchTo_RaisesPageChangedEvent()
    {
        // Arrange
        var sut = BuildSut();
        sut.AddPage("work");
        string? received = null;
        sut.PageChanged += p => received = p;

        // Act
        sut.SwitchTo("work");

        // Assert
        Assert.Equal("work", received);
    }

    [Fact]
    public void SwitchTo_SamePage_DoesNotRaisePageChangedEvent()
    {
        // Arrange
        var sut = BuildSut();
        var fired = false;
        sut.PageChanged += _ => fired = true;

        // Act
        sut.SwitchTo("default"); // already current

        // Assert
        Assert.False(fired);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_LoadsPagesFromJson()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new List<string> { "default", "home", "work" });

        // Act
        var sut = BuildSut(pagesJson: json, currentPage: "home");

        // Assert
        Assert.Contains("home", sut.Pages);
        Assert.Contains("work", sut.Pages);
        Assert.Equal("home", sut.CurrentPage);
    }

    [Fact]
    public void Constructor_InvalidJson_FallsBackToDefaultPage()
    {
        // Arrange / Act
        var sut = BuildSut(pagesJson: "NOT_JSON");

        // Assert
        Assert.Contains("default", sut.Pages);
    }
}
