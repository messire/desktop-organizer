using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State.Dto;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI.State;

/// <inheritdoc cref="IApplicationStateService" />
internal sealed class ApplicationStateService : IApplicationStateService
{
    // Same debounce window WindowManager's SaveScheduler used before this document became shared —
    // see docs/PLANS/phase-7-performance-hardening.md (Block C) / ADR-024 D-12.
    private static readonly TimeSpan SaveDebounceWindow = TimeSpan.FromMilliseconds(500);

    // Deliberately the default (unindented) JsonSerializer options, matching LayoutSnapshotService's
    // own pre-existing PanelJsonItems round-trip format — this is a runtime-only, UI-internal
    // representation (see LayoutSnapshot's remarks), unrelated to the on-disk schema dialect.
    private static readonly JsonSerializerOptions SnapshotPanelJsonOptions = new() { WriteIndented = false };

    private readonly IApplicationStateStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly SaveScheduler _saveScheduler;

    // Lazily resolved via IServiceProvider (mirrors WindowManager's own _pageManager pattern) so this
    // service's constructor never creates a dependency cycle with the four services it pulls from at
    // save time — none of them exist yet when Program.Main constructs/resolves this service.
    private IWindowManager? _windowManager;
    private IPageManager? _pageManager;
    private ILayoutSnapshotService? _layoutSnapshotService;
    private ISystemIconsPanelManager? _systemIconsPanelManager;

    private IDebounceTimer? _debounceTimer;
    private IDiagnosticsLog? _diagnosticsLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationStateService"/> class.
    /// </summary>
    public ApplicationStateService(IApplicationStateStore store, IServiceProvider serviceProvider)
    {
        _store = store;
        _serviceProvider = serviceProvider;
        _saveScheduler = new SaveScheduler(PersistNow, ResolveDebounceTimer, SaveDebounceWindow);
    }

    /// <inheritdoc />
    public IReadOnlyList<ChildWindowModel> Panels { get; private set; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> PageIds { get; private set; } = ["default"];

    /// <inheritdoc />
    public string CurrentPageId { get; private set; } = "default";

    /// <inheritdoc />
    public IReadOnlyList<LayoutSnapshot> Snapshots { get; private set; } = [];

    /// <inheritdoc />
    public SystemIconsPanelStateDto? SystemIconsPanel { get; private set; }

    /// <inheritdoc />
    public StateLoadStatus Load()
    {
        var result = _store.Load();
        var status = result.Status;
        ApplicationStateDocumentDto document;

        try
        {
            document = DeserializeDocument(result.Payload);
        }
        catch (JsonException ex)
        {
            // MINOR fix-cycle 1 (docs/PLANS/workspace-grid-migration.md §10.5): IApplicationStateStore's
            // own recovery ladder (ADR-024 D-7) only guards a document that fails to *parse* as JSON or
            // fails a *migration* step (M-6) — it has no way to know a document that parses and migrates
            // cleanly can still fail one layer up, here, because a field has the wrong JSON *type* for
            // its DTO (e.g. a hand-edited/corrupted v5 document with `"widthSpanUnits":"2"` — a string
            // where an `int?` is expected — or `"geometryMode":1` — a number where a `string` is
            // expected; System.Text.Json's default strict typing throws JsonException for both,
            // uncaught, straight out of Deserialize<ApplicationStateDocumentDto>). Before this fix, that
            // exception propagated past Program.Main's single, unguarded Load() call site and crashed
            // the process — identically on every subsequent launch, since a read that merely throws
            // changes nothing on disk (the exact M-6 failure shape ADR-024 already closed one layer
            // down, for migrations). Recover the same way: preserve the still-JSON-valid primary via the
            // existing partial-load quarantine path (ADR-024 finalization C-1) and degrade to an empty,
            // writable structural state instead of crashing.
            _store.QuarantineLoadedDocument("PayloadDeserializationFailed");
            // NIT N-8, fix-cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8): the returned
            // StateLoadStatus is deliberately, permanently collapsed to Quarantined below (see this
            // method's own XML doc) — a more specific original status (e.g. RecoveredFromBackup,
            // meaning the store's recovery ladder ALSO had to step in for this same document) must not
            // simply vanish. Recorded on the diagnostics event instead of widening the return contract:
            // a closed-set StateLoadStatus name is exactly as safe to log as the errorType already
            // written here (never Message/paths, same ADR-024 invariant).
            DiagPayloadDeserializationFailed(ex.GetType().Name, status);
            document = new ApplicationStateDocumentDto();
            status = StateLoadStatus.Quarantined;
        }

        // MAJOR N-3, fix-cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8): an explicit JSON
        // `null` for any of these three collections (e.g. {"panels":null}) overwrites the DTO
        // property's own initializer during deserialization — System.Text.Json assigns null to a
        // reference-typed property whenever the JSON value is the literal `null`, independent of C#
        // nullable annotations (which are compile-time only). Before this fix, that produced a
        // NullReferenceException here, OUTSIDE the JsonException guard above (a null collection is not
        // a type mismatch STJ itself rejects), crashing Program.Main — and, since a read that merely
        // throws changes nothing on disk, identically on every subsequent launch. Defended at this one
        // call site instead of relying on it never happening.
        // Same crash class one level deeper (P02b fix-cycle 2 final verification, MAJOR-1): a null
        // ELEMENT ({"panels":[null]}) or a snapshot whose own "panels" is null. Skipped, never thrown.
        Panels = (document.Panels ?? []).Where(p => p is not null).Select(PanelStateMapper.FromDto).ToList();

        var pages = document.Pages ?? new PagesStateDto();
        var pageIds = pages.Ids;
        PageIds = pageIds is { Count: > 0 } ? pageIds : ["default"];
        CurrentPageId = string.IsNullOrWhiteSpace(pages.CurrentPageId)
            ? "default"
            : pages.CurrentPageId;

        Snapshots = (document.LayoutSnapshots ?? []).Where(s => s is not null).Select(ToLayoutSnapshot).ToList();

        // Absent key -> null, matching the pre-ADR-024 "blank settings value" contract: the caller
        // (SystemIconsPanelManager) keeps whichever built-in defaults its own model already carries
        // rather than being handed an all-defaults DTO that happens to look the same.
        SystemIconsPanel = result.Payload.TryGetPropertyValue("systemIconsPanel", out var sipNode) && sipNode is not null
            ? document.SystemIconsPanel
            : null;

        return status;
    }

    /// <inheritdoc />
    public void RequestSave() => _saveScheduler.Request();

    /// <inheritdoc />
    public void SaveNow() => _saveScheduler.Flush();

    /// <inheritdoc />
    public void ReportPartialLoad(string reason)
    {
        try
        {
            _store.QuarantineLoadedDocument(reason);
        }
        catch (Exception ex)
        {
            // Mirrors PersistNow's never-throws contract — a failure to quarantine must not take down
            // whatever caller (e.g. WindowManager.LoadWindowsState) is reporting the partial load.
            Console.Error.WriteLine($"Failed to quarantine partially-loaded application state: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public bool ConsumersReady { get; private set; }

    /// <inheritdoc />
    public void MarkConsumersReady() => ConsumersReady = true;

    private static ApplicationStateDocumentDto DeserializeDocument(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Deserialize<ApplicationStateDocumentDto>(StateJson.Options) ?? new ApplicationStateDocumentDto();
    }

    /// <summary>
    /// The actual persistence work deferred/synchronized by <see cref="_saveScheduler"/>: pulls the
    /// <em>current</em> live state from all four owning services, assembles the single document, and
    /// hands it to <see cref="IApplicationStateStore.Save"/>. Never throws — a write failure (disk
    /// full, no permissions, storage lock unavailable) is logged and swallowed, matching the pre-ADR-024
    /// contract of <c>WindowManager.PersistWindowsState</c>/<c>SystemIconsPanelManager.Save</c>/etc.
    /// </summary>
    private void PersistNow()
    {
        // Second half of the D-11 write barrier (ADR-024 finalization MAJOR M-1): IApplicationStateStore
        // already refuses a Save() before Load() completed; this refuses a write before every one of
        // the four state-owning consumers has had the chance to read its own slice of what Load()
        // returned. Writing any earlier would pull an empty/default section from whichever consumer
        // hasn't loaded yet (e.g. a freshly-constructed LayoutSnapshotService whose Load() hasn't run)
        // and persist it over real on-disk data, in the same atomic document as every other section.
        if (!ConsumersReady)
        {
            DiagWriteBarrierViolation("PersistNow/ConsumersNotReady");
            return;
        }

        try
        {
            var windowManager = ResolveWindowManager();

            // Sync happens here, at write time — not at RequestSave()/SaveNow() call time — exactly
            // matching the pre-ADR-024 WindowManager.PersistWindowsState ordering, so a live window's
            // Position/Size is always as fresh as possible at the moment it actually gets serialized.
            windowManager.SyncAllWindowsToModels();
            var panels = windowManager.GetAllChildWindows().Select(PanelStateMapper.ToDto).ToList();

            var pageManager = ResolvePageManager();
            var pages = new PagesStateDto
            {
                Ids = [.. pageManager.Pages],
                CurrentPageId = pageManager.CurrentPage,
            };

            var snapshotService = ResolveLayoutSnapshotService();
            var snapshots = snapshotService.Snapshots.Select(ToSnapshotDto).ToList();

            var systemIconsPanelManager = ResolveSystemIconsPanelManager();
            var systemIconsPanel = SystemIconsPanelStateMapper.ToDto(systemIconsPanelManager.Model);

            var document = new ApplicationStateDocumentDto
            {
                Panels = panels,
                Pages = pages,
                LayoutSnapshots = snapshots,
                SystemIconsPanel = systemIconsPanel,
            };

            var node = JsonSerializer.SerializeToNode(document, StateJson.Options)!.AsObject();
            _store.Save(node);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save application state: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps a schema-v3 <see cref="LayoutSnapshotStateDto"/> (real, typed panel objects) back to the
    /// runtime-only <see cref="LayoutSnapshot"/> shape (<c>PanelJsonItems</c> strings) that
    /// <c>LayoutSnapshotService</c>/<c>LayoutSnapshotsViewModel</c> already work with. See
    /// <see cref="LayoutSnapshot"/>'s remarks: it stays JSON-string-based purely so
    /// <c>DesktopOrganizer.Core</c> never has to reference <c>DesktopOrganizer.UI</c>'s
    /// <c>ChildWindowModel</c> — the on-disk schema itself has carried real objects since schema v2.
    /// </summary>
    private static LayoutSnapshot ToLayoutSnapshot(LayoutSnapshotStateDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        CreatedAt = dto.CreatedAt,
        PanelJsonItems = (dto.Panels ?? [])
            .Where(p => p is not null)
            .Select(PanelStateMapper.FromDto)
            .Select(model => JsonSerializer.Serialize(model, SnapshotPanelJsonOptions))
            .ToList(),
    };

    /// <summary>The inverse of <see cref="ToLayoutSnapshot"/>, used by <see cref="PersistNow"/>.</summary>
    private static LayoutSnapshotStateDto ToSnapshotDto(LayoutSnapshot snapshot)
    {
        var panels = new List<PanelStateDto>();

        foreach (var json in snapshot.PanelJsonItems)
        {
            ChildWindowModel? model;
            try
            {
                model = JsonSerializer.Deserialize<ChildWindowModel>(json, SnapshotPanelJsonOptions);
            }
            catch (JsonException)
            {
                // A single corrupt panel entry must not drop the whole snapshot from the save —
                // mirrors the per-item isolation used elsewhere (WindowManager.CreateEachIsolated).
                continue;
            }

            if (model is not null)
            {
                panels.Add(PanelStateMapper.ToDto(model));
            }
        }

        return new LayoutSnapshotStateDto
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            CreatedAt = snapshot.CreatedAt,
            Panels = panels,
        };
    }

    private IWindowManager ResolveWindowManager()
        => _windowManager ??= _serviceProvider.GetRequiredService<IWindowManager>();

    private IPageManager ResolvePageManager()
        => _pageManager ??= _serviceProvider.GetRequiredService<IPageManager>();

    private ILayoutSnapshotService ResolveLayoutSnapshotService()
        => _layoutSnapshotService ??= _serviceProvider.GetRequiredService<ILayoutSnapshotService>();

    private ISystemIconsPanelManager ResolveSystemIconsPanelManager()
        => _systemIconsPanelManager ??= _serviceProvider.GetRequiredService<ISystemIconsPanelManager>();

    /// <summary>
    /// Records a <c>StateWriteBarrierViolation</c> diagnostics event — the UI-layer half of the same
    /// event <c>JsonApplicationStateStore.Save</c> writes for its own (Load-not-completed) half of the
    /// write barrier. Never throws: diagnostics resolution/writing failing must not affect
    /// <see cref="PersistNow"/>'s own no-op behavior.
    /// </summary>
    private void DiagWriteBarrierViolation(string caller)
    {
        try
        {
            var diagnostics = _diagnosticsLog ??= _serviceProvider.GetRequiredService<IDiagnosticsLog>();
            diagnostics.Write("state", "StateWriteBarrierViolation", null, new KeyValuePair<string, object?>("caller", caller));
        }
        catch (Exception)
        {
            // See summary — a missing/failing IDiagnosticsLog must never affect the write barrier.
        }
    }

    /// <summary>
    /// Records a <c>StatePayloadDeserializationFailed</c> diagnostics event (fix-cycle 1) — the
    /// UI-layer analogue of <c>JsonApplicationStateStore</c>'s <c>StateMigrationFailed</c> (ADR-024
    /// M-6), for the one failure mode the store-level recovery ladder cannot see — see <see cref="Load"/>'s
    /// catch block. Never throws; never carries <see cref="Exception.Message"/> or a file path, same
    /// discipline as every other <c>state</c>-category diagnostics event (ADR-024 invariant).
    /// </summary>
    /// <param name="errorType"><see cref="Exception.GetType()"/>'s name — never the exception's message.</param>
    /// <param name="originalStatus">
    /// The <see cref="StateLoadStatus"/> <see cref="IApplicationStateStore.Load"/> itself reported for
    /// this document, before <see cref="Load"/> collapses its own return value to
    /// <see cref="StateLoadStatus.Quarantined"/> (NIT N-8, fix-cycle 2,
    /// docs/PLANS/workspace-grid-migration.md §10.8) — e.g. <see cref="StateLoadStatus.RecoveredFromBackup"/>
    /// when the document ALSO had to be recovered from <c>.bak</c> before this later, UI-layer failure.
    /// Safe to log verbatim: a closed-set enum name, not user data.
    /// </param>
    private void DiagPayloadDeserializationFailed(string errorType, StateLoadStatus originalStatus)
    {
        try
        {
            var diagnostics = _diagnosticsLog ??= _serviceProvider.GetRequiredService<IDiagnosticsLog>();
            diagnostics.Write(
                "state",
                "StatePayloadDeserializationFailed",
                null,
                new KeyValuePair<string, object?>("errorType", errorType),
                new KeyValuePair<string, object?>("originalStatus", originalStatus.ToString()));
        }
        catch (Exception)
        {
            // See summary — a missing/failing IDiagnosticsLog must never affect Load()'s degrade path.
        }
    }

    /// <summary>
    /// Resolves the <see cref="IDebounceTimer"/> to use for <see cref="_saveScheduler"/> — same
    /// degrade-to-immediate-save pattern as <c>WindowManager.ResolveDebounceTimer</c> (see its remarks).
    /// </summary>
    private IDebounceTimer? ResolveDebounceTimer()
    {
        if (Application.Current is not { } app) return null;
        return _debounceTimer ??= new DispatcherDebounceTimer(app.Dispatcher);
    }
}
