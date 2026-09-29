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

    /// <summary>
    /// L-1 fix (backlog 1.x wave 1 review): <see cref="PageManagerService.PageDescriptors"/> must not be
    /// the service's own live, mutable backing <see cref="List{T}"/> — a caller casting the returned
    /// <see cref="IReadOnlyList{T}"/> back to a concrete <see cref="List{T}"/> could otherwise mutate this
    /// service's internal state directly, bypassing every one of its own invariants (persistence,
    /// <see cref="PageManagerService.PagesChanged"/>, the "default" page always being present, etc.).
    /// </summary>
    [Fact]
    public void PageDescriptors_IsNotCastableToTheServicesOwnBackingList()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        Assert.IsNotType<List<PageDescriptor>>(sut.PageDescriptors);
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
    /// Verifies that the constructor defends against an empty <c>IApplicationStateService.Pages</c> by
    /// falling back to the <c>"default"</c> page, mirroring the pre-ADR-024 blank-pages fallback.
    /// </summary>
    [Fact]
    public void Constructor_EmptyPageIds_FallsBackToDefaultPage()
    {
        // Arrange / Act — IApplicationStateService.Pages is never actually empty in production (it
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

    // ── PageDescriptors / GetPageName ────────────────────────────────────────────

    /// <summary>Verifies that <see cref="PageManagerService.PageDescriptors"/> exposes the same ids, in the same order, as <see cref="PageManagerService.Pages"/>.</summary>
    [Fact]
    public void PageDescriptors_SameOrderAsPages()
    {
        var sut = BuildSut(pageIds: ["default", "home", "work"]);

        Assert.Equal(sut.Pages, sut.PageDescriptors.Select(p => p.Id));
    }

    /// <summary>Verifies that <see cref="PageManagerService.GetPageName"/> returns <see langword="null"/> for the unnamed default page and an unknown id.</summary>
    [Fact]
    public void GetPageName_DefaultAndUnknown_ReturnsNull()
    {
        var sut = BuildSut();

        Assert.Null(sut.GetPageName("default"));
        Assert.Null(sut.GetPageName("does-not-exist"));
    }

    /// <summary>Verifies that <see cref="PageManagerService.GetPageName"/> returns the stored name for a named page.</summary>
    [Fact]
    public void GetPageName_NamedPage_ReturnsStoredName()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        Assert.Equal("work", sut.GetPageName("work"));
    }

    // ── RenamePage / ValidatePageName ────────────────────────────────────────────

    /// <summary>Verifies a successful rename: <see cref="PageRenameResult.Renamed"/>, the stored name is updated, <c>SaveNow</c> is called once, and <see cref="PageManagerService.PagesChanged"/> fires but <see cref="PageManagerService.PageChanged"/> does not.</summary>
    [Fact]
    public void RenamePage_ValidNewName_RenamesPersistsAndRaisesPagesChangedOnly()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);
        var pagesChangedRaised = false;
        var pageChangedRaised = false;
        sut.PagesChanged += () => pagesChangedRaised = true;
        sut.PageChanged += _ => pageChangedRaised = true;

        var result = sut.RenamePage("work", "Project Alpha");

        Assert.Equal(PageRenameResult.Renamed, result);
        Assert.Equal("Project Alpha", sut.GetPageName("work"));
        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
        Assert.True(pagesChangedRaised);
        Assert.False(pageChangedRaised);
    }

    /// <summary>Verifies that renaming a page to its own current name (ordinal) is <see cref="PageRenameResult.Unchanged"/>: no save, no event.</summary>
    [Fact]
    public void RenamePage_SameName_IsUnchanged_NoSaveNoEvent()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);
        var raised = false;
        sut.PagesChanged += () => raised = true;

        var result = sut.RenamePage("work", "work");

        Assert.Equal(PageRenameResult.Unchanged, result);
        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Never);
        Assert.False(raised);
    }

    /// <summary>
    /// H-1 fix (backlog 1.x wave 1 review): committing the literal English localized fallback text
    /// ("Default", <c>Page_Default_DisplayName</c>) for an unnamed <c>"default"</c> page — exactly what
    /// <c>MainWindowViewModel.BeginRenamePage</c> pre-fills the inline editor with — must be
    /// <see cref="PageRenameResult.Unchanged"/>, not <see cref="PageRenameResult.Renamed"/>: the previous
    /// behavior persisted that literal fallback text as a real stored <see cref="PageDescriptor.Name"/>,
    /// de-localizing the page for every other UI culture from then on (ADR-032 D-5 violation).
    /// </summary>
    [Fact]
    public void RenamePage_DefaultUnnamed_LiteralLocalizedFallbackText_IsUnchanged_NoSaveNoEvent()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);
        var raised = false;
        sut.PagesChanged += () => raised = true;

        var result = sut.RenamePage("default", "Default");

        Assert.Equal(PageRenameResult.Unchanged, result);
        Assert.Null(sut.GetPageName("default"));
        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Never);
        Assert.False(raised);
    }

    /// <summary>Verifies that a pure case change to a page's own name is a real <see cref="PageRenameResult.Renamed"/> (self-exclusion in the duplicate check).</summary>
    [Fact]
    public void RenamePage_CaseOnlyChangeOfOwnName_IsRenamed()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        var result = sut.RenamePage("work", "Work");

        Assert.Equal(PageRenameResult.Renamed, result);
        Assert.Equal("Work", sut.GetPageName("work"));
    }

    /// <summary>Verifies that an empty/whitespace name is rejected for a non-default page.</summary>
    /// <param name="blank">An empty/whitespace-only candidate name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RenamePage_EmptyForNonDefault_ReturnsEmpty(string blank)
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        var result = sut.RenamePage("work", blank);

        Assert.Equal(PageRenameResult.Empty, result);
        Assert.Equal("work", sut.GetPageName("work"));
    }

    /// <summary>Verifies that an empty name resets the "default" page to unnamed (<see cref="PageRenameResult.Renamed"/>), and repeating it is then <see cref="PageRenameResult.Unchanged"/>.</summary>
    [Fact]
    public void RenamePage_EmptyForDefault_ResetsToUnnamed_ThenUnchanged()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);
        sut.RenamePage("default", "Main");

        var resetResult = sut.RenamePage("default", string.Empty);
        var repeatResult = sut.RenamePage("default", "  ");

        Assert.Equal(PageRenameResult.Renamed, resetResult);
        Assert.Null(sut.GetPageName("default"));
        Assert.Equal(PageRenameResult.Unchanged, repeatResult);
    }

    /// <summary>Verifies that a name longer than the maximum length is rejected, and exactly the maximum length is accepted.</summary>
    [Fact]
    public void RenamePage_TooLongVersusExactlyMax_Rejected_Accepted()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);
        var tooLong = new string('a', 41);
        var exactlyMax = new string('a', 40);

        Assert.Equal(PageRenameResult.TooLong, sut.RenamePage("work", tooLong));
        Assert.Equal(PageRenameResult.Renamed, sut.RenamePage("work", exactlyMax));
    }

    /// <summary>
    /// Verifies that a name containing a control character (e.g. a pasted line break/tab) is rejected —
    /// the control character sits in the middle of otherwise-non-blank text so <c>Trim()</c> (step 2)
    /// cannot strip it away before the control-character check (step 4) ever sees it.
    /// </summary>
    /// <param name="invalid">A candidate name containing an embedded control character.</param>
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    public void RenamePage_ControlCharacters_ReturnsInvalidCharacters(string invalid)
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        var result = sut.RenamePage("work", invalid);

        Assert.Equal(PageRenameResult.InvalidCharacters, result);
    }

    /// <summary>
    /// L-10 fix (backlog 1.x wave 1 review): <c>char.IsControl</c> alone misses the Unicode "Format" (Cf)
    /// category — invisible formatting characters like U+200B (zero-width space) and U+202E (right-to-left
    /// override, usable to visually spoof a page name) must be rejected exactly like a control character.
    /// </summary>
    /// <param name="invalid">A candidate name containing an embedded Unicode Format-category character.</param>
    [Theory]
    [InlineData("a​b")]
    [InlineData("a‮b")]
    public void RenamePage_UnicodeFormatCharacters_ReturnsInvalidCharacters(string invalid)
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        var result = sut.RenamePage("work", invalid);

        Assert.Equal(PageRenameResult.InvalidCharacters, result);
    }

    /// <summary>Verifies that a name matching another page's effective name (case-insensitive) is a duplicate, including against Cyrillic names and the localized "default" fallback.</summary>
    [Fact]
    public void RenamePage_DuplicateAgainstOtherPage_ReturnsDuplicate()
    {
        var sut = BuildSut(pageIds: ["default", "work", "home"]);
        sut.RenamePage("home", "Работа");

        Assert.Equal(PageRenameResult.Duplicate, sut.RenamePage("work", "РАБОТА"));
        Assert.Equal(PageRenameResult.Duplicate, sut.RenamePage("work", "Default")); // "Default" == English localized Page_Default_DisplayName
    }

    /// <summary>Verifies that <see cref="PageManagerService.RenamePage"/> returns <see cref="PageRenameResult.NotFound"/> for an unknown page id.</summary>
    [Fact]
    public void RenamePage_UnknownPageId_ReturnsNotFound()
    {
        var sut = BuildSut();

        Assert.Equal(PageRenameResult.NotFound, sut.RenamePage("does-not-exist", "Anything"));
    }

    /// <summary>Verifies that <see cref="PageManagerService.ValidatePageName"/> never mutates state or persists.</summary>
    [Fact]
    public void ValidatePageName_DoesNotMutateOrSave()
    {
        var sut = BuildSut(pageIds: ["default", "work"]);

        var result = sut.ValidatePageName("work", "Project Alpha");

        Assert.Equal(PageRenameResult.Renamed, result);
        Assert.Equal("work", sut.GetPageName("work"));
        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Never);
    }

    // ── AddNewPage ───────────────────────────────────────────────────────────────

    /// <summary>Verifies that <see cref="PageManagerService.AddNewPage"/> creates a page with a 32-hex-character GUID id, a localized name, persists, and raises <see cref="PageManagerService.PagesChanged"/>.</summary>
    [Fact]
    public void AddNewPage_CreatesGuidIdWithLocalizedName_PersistsAndRaisesPagesChanged()
    {
        var sut = BuildSut();
        var raised = false;
        sut.PagesChanged += () => raised = true;

        var id = sut.AddNewPage();

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal("Page 2", sut.GetPageName(id));
        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
        Assert.True(raised);
    }

    /// <summary>Verifies that <see cref="PageManagerService.AddNewPage"/> skips a name already taken by a user-named page.</summary>
    [Fact]
    public void AddNewPage_NameCollisionWithUserNamedPage_SkipsToNextUniqueName()
    {
        // Two existing pages ("default", "work") -> the first candidate name is "Page 3"
        // (Pages.Count + 1). Renaming "work" to exactly that collides on purpose, so AddNewPage must
        // skip to "Page 4" instead.
        var sut = BuildSut(pageIds: ["default", "work"]);
        sut.RenamePage("work", "Page 3");

        var id = sut.AddNewPage();

        Assert.Equal("Page 4", sut.GetPageName(id));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private PageManagerService BuildSut(
        IEnumerable<ChildWindowModel>? panels = null,
        IReadOnlyList<string>? pageIds = null,
        string currentPageId = "default")
    {
        var pages = pageIds is null
            ? new List<PageDescriptor> { new("default", null) }
            : [.. pageIds.Select(id => new PageDescriptor(id, id == "default" ? null : id))];
        _applicationStateServiceMock.SetupGet(s => s.Pages).Returns(pages);
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
