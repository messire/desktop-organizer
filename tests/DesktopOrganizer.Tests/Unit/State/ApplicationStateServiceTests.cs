using System.Text.Json.Nodes;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.State.Dto;
using Moq;

namespace DesktopOrganizer.Tests.Unit.State;

/// <summary>
/// Unit tests for <see cref="ApplicationStateService"/> (ADR-024 D-11/D-12, C6): the single load per
/// process, and the uniform pull-on-save model — <c>RequestSave()</c>/<c>SaveNow()</c> must always
/// re-read the <em>current</em> live state from all four owning services at write time, never a value
/// captured earlier, so a single <c>SaveNow()</c> call (e.g. <c>App.OnExit</c>) always captures the
/// whole document's true current state in one shot.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ApplicationStateServiceTests
{
    /// <summary>
    /// Records every event written, for asserting on diagnostics payload contents directly rather than
    /// via a Moq expression against a <c>params</c> array — same pattern as
    /// <c>JsonApplicationStateStoreTests.FakeDiagnosticsLog</c>.
    /// </summary>
    private sealed class FakeDiagnosticsLog : IDiagnosticsLog
    {
        public List<(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)> Events { get; } = new();

        public bool IsEnabled => true;

        public void Write(string category, string name, string? scope, params KeyValuePair<string, object?>[] payload) =>
            Events.Add((category, name, scope, payload));

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private readonly Mock<IApplicationStateStore> _storeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IPageManager> _pageManagerMock = new();
    private readonly Mock<ILayoutSnapshotService> _layoutSnapshotServiceMock = new();
    private readonly Mock<ISystemIconsPanelManager> _systemIconsPanelManagerMock = new();

    // ── Load ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.Load"/> returns the <see cref="StateLoadStatus"/>
    /// reported by the underlying <see cref="IApplicationStateStore"/> unchanged.
    /// </summary>
    [Fact]
    public void Load_ReturnsTheStoresStatus()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();

        var status = sut.Load();

        Assert.Equal(StateLoadStatus.FirstRun, status);
    }

    /// <summary>
    /// Verifies that a panel entry present in the store's payload is deserialized into
    /// <see cref="ApplicationStateService.Panels"/> with its id and title preserved.
    /// </summary>
    [Fact]
    public void Load_PopulatesPanelsFromStorePayload()
    {
        var panelId = Guid.NewGuid();
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = panelId.ToString(),
                    ["title"] = "Downloads",
                    ["pageId"] = "default",
                    ["geometry"] = new JsonObject { ["x"] = 1, ["y"] = 2, ["width"] = 3, ["height"] = 4 },
                    ["appearance"] = new JsonObject(),
                    ["content"] = new JsonObject(),
                },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        sut.Load();

        Assert.Single(sut.Panels);
        Assert.Equal(panelId, sut.Panels[0].Id);
        Assert.Equal("Downloads", sut.Panels[0].Title);
    }

    /// <summary>
    /// Verifies that when the store's payload has no pages section, <see cref="ApplicationStateService.PageIds"/>
    /// and <see cref="ApplicationStateService.CurrentPageId"/> fall back to the single "default" page.
    /// </summary>
    [Fact]
    public void Load_EmptyPageIds_FallsBackToDefault()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();

        sut.Load();

        Assert.Equal(["default"], sut.PageIds);
        Assert.Equal("default", sut.CurrentPageId);
    }

    /// <summary>
    /// Verifies that a pages section with explicit ids and a current page id is applied verbatim to
    /// <see cref="ApplicationStateService.PageIds"/> and <see cref="ApplicationStateService.CurrentPageId"/>.
    /// </summary>
    [Fact]
    public void Load_PopulatesPageIdsAndCurrentPageId()
    {
        var payload = new JsonObject
        {
            ["pages"] = new JsonObject { ["ids"] = new JsonArray("default", "work"), ["currentPageId"] = "work" },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        sut.Load();

        Assert.Equal(["default", "work"], sut.PageIds);
        Assert.Equal("work", sut.CurrentPageId);
    }

    /// <summary>
    /// Verifies that a layout snapshot entry in the store's payload is deserialized into
    /// <see cref="ApplicationStateService.Snapshots"/> with its id and name preserved.
    /// </summary>
    [Fact]
    public void Load_PopulatesSnapshotsFromStorePayload()
    {
        var payload = new JsonObject
        {
            ["layoutSnapshots"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "snap-1",
                    ["name"] = "Morning",
                    ["createdAt"] = DateTime.UtcNow.ToString("O"),
                    ["panels"] = new JsonArray(),
                },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        sut.Load();

        Assert.Single(sut.Snapshots);
        Assert.Equal("snap-1", sut.Snapshots[0].Id);
        Assert.Equal("Morning", sut.Snapshots[0].Name);
    }

    /// <summary>
    /// Verifies that when the payload has no <c>systemIconsPanel</c> key,
    /// <see cref="ApplicationStateService.SystemIconsPanel"/> stays null.
    /// </summary>
    [Fact]
    public void Load_NoSystemIconsPanelKey_ReturnsNull()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();

        sut.Load();

        Assert.Null(sut.SystemIconsPanel);
    }

    /// <summary>
    /// Verifies that a <c>systemIconsPanel</c> key present in the payload is deserialized into a typed
    /// <see cref="ApplicationStateService.SystemIconsPanel"/> DTO with its fields preserved.
    /// </summary>
    [Fact]
    public void Load_SystemIconsPanelKeyPresent_ReturnsTypedDto()
    {
        var payload = new JsonObject
        {
            ["systemIconsPanel"] = new JsonObject { ["isEnabled"] = true, ["iconSize"] = "Large" },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        sut.Load();

        Assert.NotNull(sut.SystemIconsPanel);
        Assert.True(sut.SystemIconsPanel!.IsEnabled);
        Assert.Equal("Large", sut.SystemIconsPanel.IconSize);
    }

    // ── Load: wrong-JSON-type payload field (fix-cycle 1 MINOR finding,
    // docs/PLANS/workspace-grid-migration.md §10.5) — must degrade, never crash Program.Main ───────

    /// <summary>
    /// Verifies that a panel geometry field of the wrong JSON type (a string where a number is expected)
    /// never throws out of <see cref="ApplicationStateService.Load"/>.
    /// </summary>
    [Fact]
    public void Load_PayloadFieldHasWrongJsonType_NeverThrows()
    {
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["geometry"] = new JsonObject { ["widthSpanUnits"] = "not-a-number" },
                },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that a panel geometry field of the wrong JSON type causes that panel to be dropped,
    /// degrading <see cref="ApplicationStateService.Panels"/> to empty instead of a partial object.
    /// </summary>
    [Fact]
    public void Load_PayloadFieldHasWrongJsonType_DegradesToEmptyPanels()
    {
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject { ["geometry"] = new JsonObject { ["geometryMode"] = 1 } },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        sut.Load();

        Assert.Empty(sut.Panels);
    }

    /// <summary>
    /// Verifies that a panel geometry field of the wrong JSON type makes <see cref="ApplicationStateService.Load"/>
    /// return <see cref="StateLoadStatus.Quarantined"/>.
    /// </summary>
    [Fact]
    public void Load_PayloadFieldHasWrongJsonType_ReturnsQuarantinedStatus()
    {
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject { ["geometry"] = new JsonObject { ["heightSpanUnits"] = "2" } },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var status = sut.Load();

        Assert.Equal(StateLoadStatus.Quarantined, status);
    }

    /// <summary>
    /// Verifies that a panel geometry field of the wrong JSON type makes <see cref="ApplicationStateService.Load"/>
    /// quarantine the loaded document via <c>IApplicationStateStore.QuarantineLoadedDocument</c> with the
    /// reason "PayloadDeserializationFailed".
    /// </summary>
    [Fact]
    public void Load_PayloadFieldHasWrongJsonType_QuarantinesLoadedDocumentWithReason()
    {
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject { ["geometry"] = new JsonObject { ["sizingMode"] = false } },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        sut.Load();

        _storeMock.Verify(s => s.QuarantineLoadedDocument("PayloadDeserializationFailed"), Times.Once);
    }

    /// <summary>
    /// Verifies that an ordinary, well-typed payload never triggers the wrong-JSON-type quarantine path,
    /// as a sanity check against the three preceding tests being false-positive-prone.
    /// </summary>
    [Fact]
    public void Load_WellFormedPayload_DoesNotQuarantine()
    {
        // Sanity check the three tests above aren't false-positive-prone: an ordinary, well-typed
        // payload must not trigger the new quarantine path at all.
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, []));
        var sut = BuildSut();

        sut.Load();

        _storeMock.Verify(s => s.QuarantineLoadedDocument(It.IsAny<string>()), Times.Never);
    }

    // ── Load: explicit JSON `null` for a top-level collection (MAJOR N-3, fix-cycle 2,
    // docs/PLANS/workspace-grid-migration.md §10.8) — must degrade, never NullReferenceException ─────

    /// <summary>
    /// Verifies that an explicit JSON <c>null</c> for the top-level <c>panels</c> collection never
    /// throws and degrades <see cref="ApplicationStateService.Panels"/> to empty (MAJOR N-3, fix-cycle 2).
    /// </summary>
    [Fact]
    public void Load_NullPanelsInPayload_NeverThrows_DegradesToEmptyPanels()
    {
        var payload = new JsonObject { ["panels"] = null };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
        Assert.Empty(sut.Panels);
    }

    /// <summary>
    /// Verifies that an explicit JSON <c>null</c> for the top-level <c>pages</c> section never throws
    /// and falls back to the single "default" page (MAJOR N-3, fix-cycle 2).
    /// </summary>
    [Fact]
    public void Load_NullPagesInPayload_NeverThrows_FallsBackToDefault()
    {
        var payload = new JsonObject { ["pages"] = null };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
        Assert.Equal(["default"], sut.PageIds);
        Assert.Equal("default", sut.CurrentPageId);
    }

    /// <summary>
    /// Verifies that an explicit JSON <c>null</c> for the top-level <c>layoutSnapshots</c> collection
    /// never throws and degrades <see cref="ApplicationStateService.Snapshots"/> to empty (MAJOR N-3, fix-cycle 2).
    /// </summary>
    [Fact]
    public void Load_NullLayoutSnapshotsInPayload_NeverThrows_DegradesToEmptySnapshots()
    {
        var payload = new JsonObject { ["layoutSnapshots"] = null };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
        Assert.Empty(sut.Snapshots);
    }

    // ── Load: null ELEMENTS inside the collections (P02b fix-cycle 2 final verification MAJOR-1,
    // docs/PLANS/workspace-grid-migration.md §10.8) — same crash class as N-3, one level deeper ─────

    /// <summary>
    /// Verifies that a null element inside the <c>panels</c> array never throws and is skipped rather
    /// than propagating a crash one level deeper than the top-level-null case (MAJOR-1, fix-cycle 2).
    /// </summary>
    [Fact]
    public void Load_NullElementInPanels_NeverThrows_SkipsTheNullEntry()
    {
        var payload = new JsonObject { ["panels"] = new JsonArray((JsonNode?)null) };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
        Assert.Empty(sut.Panels);
    }

    /// <summary>
    /// Verifies that a null element inside the <c>layoutSnapshots</c> array never throws and is skipped
    /// rather than propagating a crash (MAJOR-1, fix-cycle 2).
    /// </summary>
    [Fact]
    public void Load_NullElementInLayoutSnapshots_NeverThrows_SkipsTheNullEntry()
    {
        var payload = new JsonObject { ["layoutSnapshots"] = new JsonArray((JsonNode?)null) };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
        Assert.Empty(sut.Snapshots);
    }

    /// <summary>
    /// Verifies that a layout snapshot whose own <c>panels</c> field is JSON <c>null</c> never throws
    /// and yields a snapshot with an empty panel list instead of dropping the whole snapshot.
    /// </summary>
    [Fact]
    public void Load_SnapshotWithNullPanels_NeverThrows_YieldsEmptySnapshot()
    {
        var payload = new JsonObject
        {
            ["layoutSnapshots"] = new JsonArray(new JsonObject
            {
                ["id"] = "snap-1",
                ["name"] = "Broken",
                ["panels"] = null,
            }),
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Load());

        Assert.Null(exception);
        var snapshot = Assert.Single(sut.Snapshots);
        Assert.Equal("snap-1", snapshot.Id);
        Assert.Empty(snapshot.PanelJsonItems);
    }

    // ── Load: typed-enum DTO field as an out-of-range JSON number (MINOR N-7, fix-cycle 2,
    // docs/PLANS/workspace-grid-migration.md §10.8) — must degrade via the existing JsonException
    // guard, never propagate an undefined enum value into ChildWindowModel/WPF's own setters ────────

    /// <summary>
    /// Verifies that a typed-enum DTO field set to an out-of-range JSON number degrades via the existing
    /// <see cref="System.Text.Json.JsonException"/> guard to a quarantined, empty-panels load rather than
    /// propagating an undefined enum value into <see cref="ChildWindowModel"/>/WPF's own setters (MINOR N-7, fix-cycle 2).
    /// </summary>
    /// <param name="section">The panel sub-object holding the field under test (e.g. "geometry").</param>
    /// <param name="field">The enum-backed field name within <paramref name="section"/> under test.</param>
    [Theory]
    [InlineData("geometry", "windowState")]
    [InlineData("content", "viewMode")]
    [InlineData("content", "sortMode")]
    [InlineData("appearance", "surfaceStyle")]
    public void Load_TypedEnumFieldAsOutOfRangeNumber_NeverThrows_DegradesToQuarantined(string section, string field)
    {
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    [section] = new JsonObject { [field] = 999 },
                },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.Loaded, payload));
        var sut = BuildSut();

        var status = sut.Load();

        Assert.Equal(StateLoadStatus.Quarantined, status);
        Assert.Empty(sut.Panels);
    }

    /// <summary>
    /// Verifies that when a payload field has the wrong JSON type, the diagnostics event recorded for the
    /// quarantine records the store's original, more specific <see cref="StateLoadStatus"/> under
    /// "originalStatus" even though the returned status permanently collapses to Quarantined (NIT N-8, fix-cycle 2).
    /// </summary>
    [Fact]
    public void Load_PayloadFieldHasWrongJsonType_DiagnosticsRecordsOriginalStatusBeforeOverwrite()
    {
        // NIT N-8, fix-cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8): the returned
        // StateLoadStatus deliberately, permanently collapses to Quarantined for this failure mode
        // (documented on IApplicationStateService.Load()) — but the store's own, more specific original
        // status must not simply vanish; it is recorded on the diagnostics event instead.
        var diagnosticsLog = new FakeDiagnosticsLog();
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IDiagnosticsLog))).Returns(diagnosticsLog);
        var payload = new JsonObject
        {
            ["panels"] = new JsonArray
            {
                new JsonObject { ["geometry"] = new JsonObject { ["windowState"] = 999 } },
            },
        };
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.RecoveredFromBackup, payload));
        var sut = BuildSut();

        sut.Load();

        var recorded = Assert.Single(diagnosticsLog.Events, e => e.Name == "StatePayloadDeserializationFailed");
        Assert.Contains(recorded.Payload, kvp => kvp.Key == "originalStatus" && Equals(kvp.Value, "RecoveredFromBackup"));
    }

    // ── SaveNow — uniform pull model ────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.SaveNow"/> calls <c>IWindowManager.SyncAllWindowsToModels</c>
    /// before reading panels via <c>IWindowManager.GetAllChildWindows</c>, so freshly synced state is what gets saved.
    /// </summary>
    [Fact]
    public void SaveNow_SyncsAllWindowsToModels_BeforeReadingPanels()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();
        sut.Load();
        sut.MarkConsumersReady();

        var order = new List<string>();
        _windowManagerMock.Setup(w => w.SyncAllWindowsToModels()).Callback(() => order.Add("sync"));
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Callback(() => order.Add("read")).Returns([]);

        sut.SaveNow();

        Assert.Equal(["sync", "read"], order);
    }

    /// <summary>
    /// Verifies the core ADR-024 D-11/D-12 pull-model guarantee: a panel created after <c>Load()</c> (so
    /// it never appears in <see cref="ApplicationStateService.Panels"/>) is still included in what
    /// <see cref="ApplicationStateService.SaveNow"/> writes, because it re-reads
    /// <c>IWindowManager.GetAllChildWindows</c> itself rather than trusting the cached <c>Panels</c> list.
    /// </summary>
    [Fact]
    public void SaveNow_PullsCurrentPanels_NotAStaleSnapshotFromLoad()
    {
        // The single most important pull-model guarantee (ADR-024 D-11/D-12): a panel created AFTER
        // Load() (so it never appears in sut.Panels) must still show up in what gets saved, because
        // SaveNow() re-reads IWindowManager.GetAllChildWindows() itself rather than trusting Panels.
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();
        sut.Load();
        sut.MarkConsumersReady();
        Assert.Empty(sut.Panels);

        var freshPanel = new ChildWindowModel { Title = "Created after Load()" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([freshPanel]);

        JsonObject? saved = null;
        _storeMock.Setup(s => s.Save(It.IsAny<JsonObject>()))
            .Callback<JsonObject>(payload => saved = payload)
            .Returns(new StateSaveOutcome(StateSaveResult.Saved, null));

        sut.SaveNow();

        Assert.NotNull(saved);
        var panels = Assert.IsType<JsonArray>(saved!["panels"]);
        Assert.Single(panels);
        Assert.Equal("Created after Load()", panels[0]!["title"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.SaveNow"/> composes the pages, layout snapshots,
    /// and system icons panel sections (alongside panels) into a single saved document.
    /// </summary>
    [Fact]
    public void SaveNow_ComposesAllFourSectionsIntoOneDocument()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();
        sut.Load();
        sut.MarkConsumersReady();

        _pageManagerMock.SetupGet(p => p.Pages).Returns(["default", "work"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("work");
        _layoutSnapshotServiceMock.SetupGet(l => l.Snapshots).Returns([new LayoutSnapshot { Id = "snap-1" }]);
        var iconsModel = new SystemIconsPanelModel { IsEnabled = true };
        _systemIconsPanelManagerMock.SetupGet(s => s.Model).Returns(iconsModel);

        JsonObject? saved = null;
        _storeMock.Setup(s => s.Save(It.IsAny<JsonObject>()))
            .Callback<JsonObject>(payload => saved = payload)
            .Returns(new StateSaveOutcome(StateSaveResult.Saved, null));

        sut.SaveNow();

        Assert.NotNull(saved);
        Assert.Equal("work", saved!["pages"]!["currentPageId"]!.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(saved["layoutSnapshots"]));
        Assert.True(saved["systemIconsPanel"]!["isEnabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.SaveNow"/> never throws when a consumer service
    /// (here <c>IWindowManager.SyncAllWindowsToModels</c>) throws, and skips the store write entirely.
    /// </summary>
    [Fact]
    public void SaveNow_NeverThrows_WhenAConsumerThrows()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();
        sut.Load();
        sut.MarkConsumersReady();
        _windowManagerMock.Setup(w => w.SyncAllWindowsToModels()).Throws(new InvalidOperationException("boom"));

        var exception = Record.Exception(sut.SaveNow);

        Assert.Null(exception);
        _storeMock.Verify(s => s.Save(It.IsAny<JsonObject>()), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.RequestSave"/> saves immediately and synchronously
    /// when no WPF <see cref="System.Windows.Application"/>/dispatcher is available (headless), and
    /// legitimately debounces instead when one is (branches on the actual ambient state since there is no
    /// injectable seam for <c>ApplicationStateService.ResolveDebounceTimer</c>, unlike <c>SaveScheduler</c>).
    /// </summary>
    [Fact]
    public void RequestSave_InHeadlessTestHost_DegradesToImmediateSave()
    {
        // ApplicationStateService.ResolveDebounceTimer (mirrors WindowManager.ResolveDebounceTimer)
        // reads the process-wide System.Windows.Application.Current directly — there is no seam to
        // inject a fake here, unlike SaveScheduler's own isolated unit coverage
        // (SaveSchedulerTests.Request_NoTimerAvailable_SavesImmediatelyAndSynchronously, which proves
        // the same degrade-to-immediate behavior with a fully injected timer provider). Whether
        // Application.Current is null when this test runs depends on whether some earlier test in
        // this same (DisableTestParallelization) process already constructed one — several ViewModels
        // test fixtures do, all now via WpfApplicationHost.EnsureApplication() (M-2 fix, P08 fix cycle
        // 2; see ChildWindowViewModelTests.EnsureWpfApplication) — and once constructed it is never
        // null again for the rest of the process. Branch on the actual ambient state rather than
        // assuming headless, so this test is not order-dependent on which other test classes happen to
        // run first.
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        _storeMock.Setup(s => s.Save(It.IsAny<JsonObject>())).Returns(new StateSaveOutcome(StateSaveResult.Saved, null));
        var sut = BuildSut();
        sut.Load();
        sut.MarkConsumersReady();

        sut.RequestSave();

        if (System.Windows.Application.Current is null)
        {
            // True headless case: RequestSave's debounce degrades to an immediate synchronous save
            // (see SaveScheduler.Request) exactly as SaveSchedulerTests proves at the unit level.
            _storeMock.Verify(s => s.Save(It.IsAny<JsonObject>()), Times.Once);
        }
        else
        {
            // A live Application/Dispatcher is available (from an earlier test in this process): the
            // request is legitimately debounced instead — nothing saves synchronously within this
            // call. This is the other, equally correct branch of the same ResolveDebounceTimer logic.
            _storeMock.Verify(s => s.Save(It.IsAny<JsonObject>()), Times.Never);
        }
    }

    // ── ConsumersReady write barrier (ADR-024 finalization MAJOR M-1) ──────────────

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.ConsumersReady"/> starts false and flips to true
    /// only after <see cref="ApplicationStateService.MarkConsumersReady"/> is called.
    /// </summary>
    [Fact]
    public void ConsumersReady_DefaultsToFalse_UntilMarkConsumersReadyIsCalled()
    {
        var sut = BuildSut();

        Assert.False(sut.ConsumersReady);

        sut.MarkConsumersReady();

        Assert.True(sut.ConsumersReady);
    }

    /// <summary>
    /// Verifies the core M-1 regression guard: <see cref="ApplicationStateService.SaveNow"/> called before
    /// every consumer has loaded its slice never writes to the store, so it can never overwrite real
    /// on-disk data with an empty/default section from an unready service.
    /// </summary>
    [Fact]
    public void SaveNow_BeforeConsumersReady_NeverWritesToStore()
    {
        // The core M-1 regression: a write attempted before every consumer has loaded its slice must
        // never reach the store — otherwise it would pull an empty/default section from whichever
        // service hasn't loaded yet and persist that over real on-disk data.
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();
        sut.Load();

        // Deliberately not calling MarkConsumersReady().
        sut.SaveNow();

        _storeMock.Verify(s => s.Save(It.IsAny<JsonObject>()), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.RequestSave"/> called before every consumer has
    /// loaded its slice never writes to the store (same write barrier as <c>SaveNow</c>, M-1).
    /// </summary>
    [Fact]
    public void RequestSave_BeforeConsumersReady_NeverWritesToStore()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        _storeMock.Setup(s => s.Save(It.IsAny<JsonObject>())).Returns(new StateSaveOutcome(StateSaveResult.Saved, null));
        var sut = BuildSut();
        sut.Load();

        sut.RequestSave();

        _storeMock.Verify(s => s.Save(It.IsAny<JsonObject>()), Times.Never);
    }

    /// <summary>
    /// Verifies that once <see cref="ApplicationStateService.MarkConsumersReady"/> has been called,
    /// <see cref="ApplicationStateService.SaveNow"/> writes to the store exactly once.
    /// </summary>
    [Fact]
    public void SaveNow_AfterMarkConsumersReady_WritesNormally()
    {
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        _storeMock.Setup(s => s.Save(It.IsAny<JsonObject>())).Returns(new StateSaveOutcome(StateSaveResult.Saved, null));
        var sut = BuildSut();
        sut.Load();
        sut.MarkConsumersReady();

        sut.SaveNow();

        _storeMock.Verify(s => s.Save(It.IsAny<JsonObject>()), Times.Once);
    }

    /// <summary>
    /// Verifies that the write barrier's diagnostics event degrades silently instead of throwing when no
    /// <see cref="IDiagnosticsLog"/> is registered in the service provider, so a should-be-no-op save
    /// before consumers are ready never turns into an exception.
    /// </summary>
    [Fact]
    public void SaveNow_BeforeConsumersReady_NeverThrows_EvenWithoutADiagnosticsLogRegistered()
    {
        // The mocked IServiceProvider in BuildSut() never registers IDiagnosticsLog — the write
        // barrier's diagnostics event must degrade silently rather than let a missing/failing
        // IDiagnosticsLog turn a should-be-no-op save into a thrown exception.
        _storeMock.Setup(s => s.Load()).Returns(MakeLoadResult(StateLoadStatus.FirstRun, []));
        var sut = BuildSut();
        sut.Load();

        var exception = Record.Exception(sut.SaveNow);

        Assert.Null(exception);
    }

    // ── ReportPartialLoad (ADR-024 finalization C-1) ────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.ReportPartialLoad"/> delegates directly to
    /// <c>IApplicationStateStore.QuarantineLoadedDocument</c> with the reason it was given.
    /// </summary>
    [Fact]
    public void ReportPartialLoad_DelegatesToStoreQuarantineLoadedDocument()
    {
        var sut = BuildSut();

        sut.ReportPartialLoad("PartialPanelLoad");

        _storeMock.Verify(s => s.QuarantineLoadedDocument("PartialPanelLoad"), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="ApplicationStateService.ReportPartialLoad"/> never propagates an exception
    /// thrown by the store's <c>QuarantineLoadedDocument</c>.
    /// </summary>
    [Fact]
    public void ReportPartialLoad_StoreThrows_NeverPropagates()
    {
        _storeMock.Setup(s => s.QuarantineLoadedDocument(It.IsAny<string>())).Throws(new InvalidOperationException("boom"));
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.ReportPartialLoad("PartialPanelLoad"));

        Assert.Null(exception);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static StateLoadResult MakeLoadResult(StateLoadStatus status, JsonObject payload)
        => new(status, payload, SchemaVersionOnDisk: 3, MigrationsApplied: 0, QuarantinePath: null);

    private ApplicationStateService BuildSut()
    {
        _windowManagerMock.Setup(w => w.SyncAllWindowsToModels());
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([]);
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["default"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        _layoutSnapshotServiceMock.SetupGet(l => l.Snapshots).Returns([]);
        _systemIconsPanelManagerMock.SetupGet(s => s.Model).Returns(new SystemIconsPanelModel());

        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IWindowManager))).Returns(_windowManagerMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IPageManager))).Returns(_pageManagerMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(ILayoutSnapshotService))).Returns(_layoutSnapshotServiceMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(ISystemIconsPanelManager))).Returns(_systemIconsPanelManagerMock.Object);

        return new ApplicationStateService(_storeMock.Object, _serviceProviderMock.Object);
    }
}
