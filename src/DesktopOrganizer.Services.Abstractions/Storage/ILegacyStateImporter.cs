using System.Text.Json.Nodes;

namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// One-time importer that reads the five structural keys still living in the legacy
/// <c>user.config</c> (<c>ChildWindowsState</c>, <c>Pages</c>, <c>CurrentPage</c>,
/// <c>LayoutSnapshots</c>, <c>SystemIconsPanel</c> — ADR-024 §0.2/D-10) and produces a schema-v1
/// document for <see cref="IApplicationStateStore.Load"/> to migrate up from, when no JSON store
/// exists yet (<see cref="StateLoadStatus.ImportedFromLegacy"/>).
/// </summary>
/// <remarks>
/// Legacy state is read-only and frozen (ADR-024 D-10 — "not deleted, and not written to either"):
/// this importer is only ever meant to be consulted when neither <c>application-state.json</c> nor
/// its <c>.bak</c> exists yet, never as a fallback once a JSON document has been created (ADR-024
/// D-7 step 4 explicitly rejects that fallback) — resurrecting legacy values after that point would
/// silently replace current state with month-old data.
/// </remarks>
public interface ILegacyStateImporter
{
    /// <summary>
    /// Reads the legacy keys and produces a schema-v1 document, or <see langword="null"/> if legacy
    /// state is empty or exactly the application's own defaults (e.g. a single "default" page and no
    /// panels) — that case is <see cref="StateLoadStatus.FirstRun"/>, not an import. A legacy key
    /// that is individually corrupt (e.g. one unparsable layout snapshot) must not fail the whole
    /// import: the remaining keys should still be imported, matching the per-panel/per-snapshot
    /// isolation (<c>CreateEachIsolated</c>/<c>SyncEachIsolated</c>) the rest of the app already
    /// applies when loading. Never throws.
    /// </summary>
    JsonObject? TryImport();
}
