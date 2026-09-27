using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
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
    private readonly ILocalizationService _localization = TestLocalization.English;

    // ── AddPage ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PageManagerService.AddPage"/> adds the new page id to
    /// <see cref="PageManagerService.Pages"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that adding a page name that already exists does not create a duplicate entry.
    /// </summary>
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

    /// <summary>
    /// Verifies that the built-in <c>"default"</c> page is always present in
    /// <see cref="PageManagerService.Pages"/>.
    /// </summary>
    [Fact]
    public void AddPage_DefaultPageAlwaysPresent()
    {
        var sut = BuildSut();
        Assert.Contains("default", sut.Pages);
    }

    // ── RemovePage ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PageManagerService.RemovePage"/> refuses to remove the built-in
    /// <c>"default"</c> page and reports it did nothing.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PageManagerService.RemovePage"/> returns <see langword="false"/> for a
    /// page id that does not exist.
    /// </summary>
    [Fact]
    public void RemovePage_UnknownPageId_ReturnsFalse()
    {
        var sut = BuildSut();

        var removed = sut.RemovePage("does-not-exist");

        Assert.False(removed);
    }

    /// <summary>
    /// Verifies that removing a page closes every panel on it. Managed items were already recycled by
    /// <see cref="PageManagerService.RemovePage"/> itself (ADR-035), so the panels are removed with
    /// <c>releaseManagedItems: false</c>.
    /// </summary>
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
        // releaseManagedItems: false since RemovePage already recycled the page's managed items in one
        // batch (ADR-035), and deferSave: true since RemovePage batches the removals and flushes once
        // at the end (M4).
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelId, false, true), Times.Once);
    }

    /// <summary>
    /// Verifies that removing a page with several panels defers each panel's persistence write and
    /// flushes the window state exactly once for the whole page, not once per removed panel.
    /// </summary>
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

        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), false, true), Times.Exactly(2));
        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Once);
    }

    /// <summary>
    /// Verifies that removing a page with no panels on it never flushes the window state, since there is
    /// nothing to flush.
    /// </summary>
    [Fact]
    public void RemovePage_NoPanelsOnPage_DoesNotFlushState()
    {
        // No RemoveChildWindow calls happened, so there is nothing to flush.
        var sut = BuildSut();
        sut.AddPage("empty");

        sut.RemovePage("empty");

        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Never);
    }

    /// <summary>
    /// Verifies that a successful <see cref="PageManagerService.RemovePage"/> call removes the page id
    /// from <see cref="PageManagerService.Pages"/> and reports success.
    /// </summary>
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

    // ── ADR-035: page deletion recycles managed items in ONE batch; a refusal keeps the page ───────

    /// <summary>
    /// Verifies that the managed items of ALL panels on the page (and only the managed ones) go to the
    /// Recycle Bin in a single batch before any panel is removed, and that a complete recycle removes
    /// the page.
    /// </summary>
    [Fact]
    public void RemovePage_ManagedItems_RecycledInOneBatch_ThenPageRemoved()
    {
        var panel1 = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\managed\\a.txt", "C:\\desktop\\x.lnk"] };
        var panel2 = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\managed\\b.txt"] };
        var other = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "default", FilePaths = ["C:\\managed\\c.txt"] };
        var sut = BuildSut(panels: [panel1, panel2, other]);
        sut.AddPage("work");
        _itemStorageMock.Setup(s => s.IsManaged(It.Is<string>(p => p.StartsWith("C:\\managed", StringComparison.Ordinal)))).Returns(true);
        var recycleCalledBeforeRemoval = false;
        _itemStorageMock
            .Setup(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()))
            .Callback(() => recycleCalledBeforeRemoval = true)
            .Returns(RecycleOutcome.Create(["C:\\managed\\a.txt", "C:\\managed\\b.txt"], [], cancelled: false));
        _windowManagerMock
            .Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback(() => Assert.True(recycleCalledBeforeRemoval, "Items must be recycled before any panel is removed."));

        var removed = sut.RemovePage("work");

        Assert.True(removed);
        _itemStorageMock.Verify(
            s => s.ReleaseToRecycleBin(It.Is<IReadOnlyList<string>>(p =>
                p.Count == 2 && p.Contains("C:\\managed\\a.txt") && p.Contains("C:\\managed\\b.txt"))),
            Times.Once);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panel1.Id, false, true), Times.Once);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panel2.Id, false, true), Times.Once);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(other.Id, It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        _confirmationMock.Verify(c => c.Notify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()), Times.Never);
        Assert.DoesNotContain("work", sut.Pages);
    }

    /// <summary>
    /// Q6 default (fail visibly): if any managed item could not be recycled, the page and all its panels
    /// are kept, only the recycled items are taken out of the panels, the user gets a warning with the
    /// number of remaining items, and <see langword="false"/> is returned.
    /// </summary>
    [Fact]
    public void RemovePage_RecycleIncomplete_KeepsPageAndPanels_RemovesRecycledItems_Notifies()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\managed\\a.txt", "C:\\managed\\b.txt"] };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");
        _itemStorageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(true);
        _itemStorageMock
            .Setup(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()))
            .Returns(RecycleOutcome.Create(["C:\\managed\\a.txt"], ["C:\\managed\\b.txt"], cancelled: true));

        var removed = sut.RemovePage("work");

        Assert.False(removed);
        Assert.Contains("work", sut.Pages);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        _windowManagerMock.Verify(
            wm => wm.RemoveItemsFromPanels(It.Is<IReadOnlyCollection<string>>(p => p.Count == 1 && p.Contains("C:\\managed\\a.txt"))),
            Times.Once);
        _confirmationMock.Verify(
            c => c.Notify(
                It.Is<string>(m => m.StartsWith("1 ", StringComparison.Ordinal)),
                _localization.GetString("Recycle_Failed_Title"),
                MessageBoxImage.Warning),
            Times.Once);
    }

    /// <summary>
    /// A page with no managed items never calls the Recycle Bin at all.
    /// </summary>
    [Fact]
    public void RemovePage_NoManagedItems_NeverRecycles()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\desktop\\x.lnk"] };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");

        var removed = sut.RemovePage("work");

        Assert.True(removed);
        _itemStorageMock.Verify(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    // ── CRITICAL C1 (P08 fix cycle 1, 2026-09-22): confirmation gate mirrors RemovePanel's own ──────

    /// <summary>
    /// Verifies that removing a page whose panels reference at least one managed file asks for a warning
    /// confirmation mentioning that files will be moved to the Recycle Bin.
    /// </summary>
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
                It.Is<string>(m => m.Contains("moved to the Recycle Bin")),
                It.IsAny<string>(),
                MessageBoxImage.Warning),
            Times.Once);
    }

    /// <summary>
    /// Verifies that removing a page whose panels reference no managed files asks for a neutral
    /// confirmation without the warning icon, and never asks for a warning-styled confirmation.
    /// </summary>
    [Fact]
    public void RemovePage_NoManagedFiles_UsesNeutralConfirmationWithoutWarningIcon()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), PageId = "work", FilePaths = ["C:\\desktop\\shortcut.lnk"] };
        var sut = BuildSut(panels: [panel]);
        sut.AddPage("work");

        sut.RemovePage("work");

        _confirmationMock.Verify(
            c => c.Confirm(
                It.Is<string>(m => !m.Contains("moved to the Recycle Bin")),
                It.IsAny<string>(),
                MessageBoxImage.Question),
            Times.Once);
        _confirmationMock.Verify(
            c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), MessageBoxImage.Warning),
            Times.Never);
    }

    /// <summary>
    /// Verifies that declining the removal confirmation leaves every panel and the page itself untouched
    /// and reports that nothing happened.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PageManagerService.RemovePage"/> forces a
    /// <see cref="IWindowManager.SyncAllWindowsToModels"/> before checking for managed files, so the
    /// managed-files check observes the post-sync state rather than stale data.
    /// </summary>
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
                It.Is<string>(m => m.Contains("moved to the Recycle Bin")),
                It.IsAny<string>(),
                MessageBoxImage.Warning),
            Times.Once);
        _windowManagerMock.Verify(wm => wm.SyncAllWindowsToModels(), Times.Once);
    }

    // ── SwitchTo ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PageManagerService.SwitchTo"/> hides panels belonging to the previous
    /// page and shows panels belonging to the newly selected page.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PageManagerService.CurrentPage"/> reflects the page just switched to.
    /// </summary>
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

    /// <summary>
    /// Verifies that switching to a page id that does not exist leaves
    /// <see cref="PageManagerService.CurrentPage"/> unchanged.
    /// </summary>
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

    /// <summary>
    /// Verifies that switching to a different page raises <see cref="PageManagerService.PageChanged"/>
    /// with the new page id.
    /// </summary>
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

    /// <summary>
    /// Verifies that switching to the page that is already current does not raise
    /// <see cref="PageManagerService.PageChanged"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that the constructor loads the page list and current page from
    /// <see cref="IApplicationStateService"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that the constructor defends against an empty <c>IApplicationStateService.PageIds</c> by
    /// falling back to the <c>"default"</c> page, mirroring the pre-ADR-024 blank-pages fallback.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PageManagerService.Save"/> delegates to
    /// <see cref="IApplicationStateService.SaveNow"/>.
    /// </summary>
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

    // ── helpers ──────────────────────────────────────────────────────────────
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
            _itemStorageMock.Object,
            _localization);
    }
}
