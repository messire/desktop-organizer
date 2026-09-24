using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PageManagerService"/>.
/// Dependencies <see cref="IWindowManager"/> and <see cref="IApplicationStateService"/> are mocked.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PageManagerServiceTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IApplicationStateService> _applicationStateServiceMock = new();
    private readonly Mock<IConfirmationService> _confirmationMock = new();
    private readonly Mock<IPanelItemStorageService> _itemStorageMock = new();

    private PageManagerService BuildSut(
        IEnumerable<ChildWindowModel>? panels = null,
        IReadOnlyList<string>? pageIds = null,
        string currentPageId = "default")
    {
        _applicationStateServiceMock.SetupGet(s => s.PageIds).Returns(pageIds ?? ["default"]);
        _applicationStateServiceMock.SetupGet(s => s.CurrentPageId).Returns(currentPageId);
        _applicationStateServiceMock.Setup(s => s.SaveNow());

        var panelList = (panels ?? Enumerable.Empty<ChildWindowModel>()).ToList();
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns(panelList);
        _windowManagerMock.Setup(wm => wm.ApplyPanelVisibility(It.IsAny<ChildWindowModel>()));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>()));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>()));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()));
        _windowManagerMock.Setup(wm => wm.SaveWindowsStateNow());
        _windowManagerMock.Setup(wm => wm.SyncAllWindowsToModels());

        // Default: no managed files anywhere, and the confirmation gate (CRITICAL C1, P08 fix cycle 1)
        // is granted — matches the pre-existing (pre-fix) behavior for every test that doesn't care
        // about the confirm gate itself. Tests that DO care override these explicitly.
        _itemStorageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);

        return new PageManagerService(
            _windowManagerMock.Object,
            _applicationStateServiceMock.Object,
            _confirmationMock.Object,
            _itemStorageMock.Object);
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
        var removed = sut.RemovePage("default");

        // Assert
        Assert.False(removed); // CRITICAL-1 (P08 fix cycle 2): callers must be able to tell nothing happened
        Assert.Equal(pageCountBefore, sut.Pages.Count);
        Assert.Contains("default", sut.Pages);
    }

    [Fact]
    public void RemovePage_UnknownPageId_ReturnsFalse()
    {
        var sut = BuildSut();

        var removed = sut.RemovePage("does-not-exist");

        Assert.False(removed);
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
        var removed = sut.RemovePage("work");

        // Assert
        Assert.True(removed); // CRITICAL-1 (P08 fix cycle 2): callers rely on this to know it actually happened
        Assert.DoesNotContain("work", sut.Pages);
    }

    // ── CRITICAL C1 (P08 fix cycle 1, 2026-09-22): confirmation gate mirrors RemovePanel's own ──────

    [Fact]
    public void RemovePage_HasManagedFiles_AsksForWarningConfirmation()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\managed\\a.txt"] };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");
        _itemStorageMock.Setup(s => s.IsManaged("C:\\managed\\a.txt")).Returns(true);

        sut.RemovePage("work");

        _confirmationMock.Verify(
            c => c.Confirm(
                It.Is<string>(m => m.Contains("deleted from disk")),
                It.IsAny<string>(),
                MessageBoxImage.Warning),
            Times.Once);
    }

    [Fact]
    public void RemovePage_NoManagedFiles_UsesNeutralConfirmationWithoutWarningIcon()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\desktop\\shortcut.lnk"] };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");

        sut.RemovePage("work");

        _confirmationMock.Verify(
            c => c.Confirm(
                It.Is<string>(m => !m.Contains("deleted from disk")),
                It.IsAny<string>(),
                MessageBoxImage.Question),
            Times.Once);
        _confirmationMock.Verify(
            c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), MessageBoxImage.Warning),
            Times.Never);
    }

    [Fact]
    public void RemovePage_ConfirmationDeclined_DoesNotRemoveAnyPanelOrPage()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work" };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(false);

        var removed = sut.RemovePage("work");

        Assert.False(removed); // CRITICAL-1 (P08 fix cycle 2): declined confirmation must report "nothing happened"
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Never);
        Assert.Contains("work", sut.Pages);
    }

    [Fact]
    public void RemovePage_SyncsAllWindowsToModelsBeforeCheckingManagedFiles()
    {
        // Regression for CRITICAL C1: a panel remapped from a stale original path to its managed copy
        // (ADR-009 Amendment) only reflects that on Model.FilePaths once synced — the hasManagedFiles
        // check below must see the POST-sync state, not whatever was there before this page removal
        // even started, or the warning dialog can be skipped for a removal that actually deletes a
        // managed file. Callback-based mock (rather than plain reference sharing) proves the *order*:
        // SyncAllWindowsToModels() must run, and its effect must already be visible, before IsManaged
        // is ever consulted.
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\stale\\original.lnk"] };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");

        _itemStorageMock.Setup(s => s.IsManaged("C:\\stale\\original.lnk")).Returns(false);
        _itemStorageMock.Setup(s => s.IsManaged("C:\\managed\\original.lnk")).Returns(true);
        _windowManagerMock
            .Setup(wm => wm.SyncAllWindowsToModels())
            .Callback(() => panel.FilePaths = ["C:\\managed\\original.lnk"]);

        sut.RemovePage("work");

        _confirmationMock.Verify(
            c => c.Confirm(
                It.Is<string>(m => m.Contains("deleted from disk")),
                It.IsAny<string>(),
                MessageBoxImage.Warning),
            Times.Once);
        _windowManagerMock.Verify(wm => wm.SyncAllWindowsToModels(), Times.Once);
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
    public void Constructor_LoadsPagesFromApplicationStateService()
    {
        // Arrange / Act
        var sut = BuildSut(pageIds: ["default", "home", "work"], currentPageId: "home");

        // Assert
        Assert.Contains("home", sut.Pages);
        Assert.Contains("work", sut.Pages);
        Assert.Equal("home", sut.CurrentPage);
    }

    [Fact]
    public void Constructor_EmptyPageIds_FallsBackToDefaultPage()
    {
        // Arrange / Act — IApplicationStateService.PageIds is never actually empty in production (it
        // always contains at least "default" — see its own remarks), but the constructor still
        // defends against it, mirroring the pre-ADR-024 "invalid/blank Pages JSON" fallback.
        var sut = BuildSut(pageIds: []);

        // Assert
        Assert.Contains("default", sut.Pages);
    }

    [Fact]
    public void Save_DelegatesToApplicationStateServiceSaveNow()
    {
        // Arrange
        var sut = BuildSut();

        // Act
        sut.Save();

        // Assert
        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
    }
}
