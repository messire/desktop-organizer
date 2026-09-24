using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// UI-layer facade over <see cref="IApplicationStateStore"/> (ADR-024 §2.3, C6): the single point
/// that loads the whole versioned JSON application-state document once, hands each of the four former
/// independent writers (panels, pages, layout snapshots, the system icons panel — ADR-024 §0.1) its
/// slice as an already-migrated, typed value, and owns the single <c>SaveScheduler</c> that all four
/// now share (ADR-024 D-12) instead of one scheduler per writer.
/// </summary>
/// <remarks>
/// <b>Single load, uniform pull on save (ADR-024 D-11/D-12).</b> <see cref="Load"/> is called exactly
/// once, in <c>Program.Main</c>, before any of the four consumers exist (see the ordering on
/// <see cref="IApplicationStateStore.Load"/>). <see cref="Panels"/>/<see cref="PageIds"/>/
/// <see cref="CurrentPageId"/>/<see cref="Snapshots"/>/<see cref="SystemIconsPanel"/> are that single
/// load's result — each consumer reads its own slice once (at construction or its own <c>Load()</c>)
/// and becomes the live in-memory owner of it from then on. <see cref="RequestSave"/>/
/// <see cref="SaveNow"/>, by contrast, always re-read the <em>current</em> live state directly from
/// each of the four owning services (<c>IWindowManager</c>, <c>IPageManager</c>,
/// <c>ILayoutSnapshotService</c>, <c>ISystemIconsPanelManager</c>) at the moment the write actually
/// happens — never a value cached at some earlier point — so a single <see cref="SaveNow"/> call
/// (e.g. <c>App.OnExit</c>) always captures the whole document's true current state in one shot.
/// </remarks>
public interface IApplicationStateService
{
    /// <summary>
    /// Loads and migrates the whole application-state document (ADR-024 D-7/D-9) exactly once,
    /// populating <see cref="Panels"/>/<see cref="PageIds"/>/<see cref="CurrentPageId"/>/
    /// <see cref="Snapshots"/>/<see cref="SystemIconsPanel"/>. Must be called, and complete, before
    /// <c>new App(...)</c> is constructed (ADR-024 D-11) and before any call to
    /// <see cref="RequestSave"/>/<see cref="SaveNow"/>. Never throws (fix-cycle 1,
    /// docs/PLANS/workspace-grid-migration.md §10.5): a document that parses and migrates cleanly at
    /// the <see cref="IApplicationStateStore"/> layer but fails to deserialize into typed DTOs one
    /// layer up (a field with the wrong JSON type) is quarantined and degrades to an empty, writable
    /// structural state instead — the returned <see cref="StateLoadStatus"/> becomes
    /// <see cref="StateLoadStatus.Quarantined"/> in that case, regardless of what the underlying store
    /// reported. The store's original, more specific status (e.g. <see cref="StateLoadStatus.RecoveredFromBackup"/>)
    /// is not lost — it is recorded on the <c>StatePayloadDeserializationFailed</c> diagnostics event
    /// (fix-cycle 2, docs/PLANS/workspace-grid-migration.md §10.8) even though this method's own return
    /// value still collapses to <see cref="StateLoadStatus.Quarantined"/>.
    /// </summary>
    StateLoadStatus Load();

    /// <summary>Every panel from the just-loaded document, already mapped to <see cref="ChildWindowModel"/>.</summary>
    IReadOnlyList<ChildWindowModel> Panels { get; }

    /// <summary>Every page identifier from the just-loaded document. Always contains at least <c>"default"</c>.</summary>
    IReadOnlyList<string> PageIds { get; }

    /// <summary>The active page identifier from the just-loaded document. Always non-blank.</summary>
    string CurrentPageId { get; }

    /// <summary>Every layout snapshot from the just-loaded document.</summary>
    IReadOnlyList<LayoutSnapshot> Snapshots { get; }

    /// <summary>
    /// The system icons panel section of the just-loaded document, or <see langword="null"/> when the
    /// document never had one (fresh install, or a document written before the feature existed) — in
    /// which case the caller should keep whichever built-in defaults its own model already carries,
    /// exactly like the pre-ADR-024 behaviour of an absent/blank settings value.
    /// </summary>
    SystemIconsPanelStateDto? SystemIconsPanel { get; }

    /// <summary>
    /// Requests that the whole document be persisted. Debounced (500 ms, ADR-012/ADR-024 D-12): a
    /// burst of calls inside the window collapses into a single write. Never throws — persistence
    /// failures are logged, not propagated.
    /// </summary>
    void RequestSave();

    /// <summary>
    /// Persists the whole document synchronously, right now, cancelling any pending debounced write
    /// first. Must be used for every structural change and application shutdown. Never throws.
    /// </summary>
    void SaveNow();

    /// <summary>
    /// Signals that the caller is about to persist (or already has, in-memory) a state that reflects
    /// less than the full document <see cref="Load"/> handed back — e.g.
    /// <c>IWindowManager.LoadWindowsState</c> silently skipping a panel that failed to construct
    /// (ADR-024 finalization C-1). Delegates to <see cref="IApplicationStateStore.QuarantineLoadedDocument"/>
    /// so the current, still-complete on-disk primary document is preserved before the next
    /// <see cref="RequestSave"/>/<see cref="SaveNow"/> call can overwrite it with the reduced state.
    /// Idempotent and safe to call more than once per load; never throws.
    /// </summary>
    /// <param name="reason">Short diagnostic token, e.g. <c>"PartialPanelLoad"</c>.</param>
    void ReportPartialLoad(string reason);

    /// <summary>
    /// Whether every one of the four state-owning services (<c>IWindowManager</c>,
    /// <c>IPageManager</c>, <c>ILayoutSnapshotService</c>, <c>ISystemIconsPanelManager</c>) has had the
    /// chance to read its own slice of the document <see cref="Load"/> returned.
    /// <see langword="false"/> from construction until <see cref="MarkConsumersReady"/> is called — at
    /// the very end of <c>App.StartApp</c>, after <c>SystemIconsPanel.Load()</c> (the last of the four
    /// to run). While <see langword="false"/>, <see cref="RequestSave"/>/<see cref="SaveNow"/> are a
    /// no-op: writing before every consumer has loaded would pull an empty/default section from
    /// whichever service hasn't read its slice yet, and persist that over real on-disk data in the
    /// same atomic document write as every other section (ADR-024 finalization MAJOR M-1). This is the
    /// second half of the D-11 write barrier — <see cref="IApplicationStateStore"/>'s own barrier only
    /// covers "before <see cref="Load"/> completed", not "before every consumer read its slice".
    /// </summary>
    bool ConsumersReady { get; }

    /// <summary>
    /// Marks every state-owning consumer ready (see <see cref="ConsumersReady"/>). Must be called
    /// exactly once, at the very end of <c>App.StartApp</c>, immediately before subscribing to
    /// activation requests. Idempotent — safe to call more than once.
    /// </summary>
    void MarkConsumersReady();
}
