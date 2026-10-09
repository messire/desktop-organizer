using System.Text.Json;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// Everything in the loaded document that this build does not understand and that is not carried by a
/// <see cref="ChildWindowModel"/> (ADR-024 Amendment "Additive revisions", docs/PLANS/schema-additive-revision-v7.md
/// §2.2): unknown keys of the document root, the <c>pages</c> section, each page, each layout snapshot and the
/// <c>systemIconsPanel</c> section (and its items), an unrecognised <c>systemIconsPanel.iconSize</c>, and system-icon
/// items whose <c>kind</c> is unknown. Owned by <see cref="ApplicationStateService"/>: filled once in
/// <c>Load()</c>, read on every <c>PersistNow()</c>.
/// </summary>
/// <remarks>
/// Extras of entities that no longer exist (a deleted page or snapshot) are simply never looked up again, which is
/// the correct outcome. Keys are stable ids: page ids are never renamed (ADR-043) and snapshot ids are fixed at
/// capture. Panel-level extras are <em>not</em> here — they live on <see cref="ChildWindowModel.PersistedExtras"/>
/// so they survive the JSON round-trips layout snapshots keep their panels in.
/// </remarks>
internal sealed class UnknownFieldLedger
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _pagesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _snapshotsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _systemIconItemsByKind = new(StringComparer.Ordinal);
    private readonly List<SystemIconsPanelItemStateDto> _unknownSystemIconItems = [];

    private Dictionary<string, JsonElement>? _root;
    private Dictionary<string, JsonElement>? _pagesSection;
    private Dictionary<string, JsonElement>? _systemIconsPanel;

    /// <summary>The unrecognised <c>systemIconsPanel.iconSize</c> string, if any (fallback = the model default).</summary>
    public PreservedEnumValue? PreservedIconSize { get; private set; }

    /// <summary>Number of unknown JSON keys held by this ledger (all levels it covers).</summary>
    public int UnknownKeyCount { get; private set; }

    /// <summary>Number of unrecognised enum strings held by this ledger.</summary>
    public int UnknownEnumCount => PreservedIconSize is null ? 0 : 1;

    /// <summary>Number of system-icon items with an unknown <c>kind</c> held by this ledger.</summary>
    public int UnknownSystemIconItemCount => _unknownSystemIconItems.Count;

    /// <summary>
    /// Reads the ledger out of a freshly deserialized document. Never throws on malformed content (null
    /// elements, blank ids, duplicates — first wins, mirroring the mappers).
    /// </summary>
    /// <param name="document">The deserialized root payload.</param>
    public static UnknownFieldLedger Capture(ApplicationStateDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ledger = new UnknownFieldLedger();
        ledger._root = ledger.Take(document.Extras);

        if (document.Pages is { } pages)
        {
            ledger._pagesSection = ledger.Take(pages.Extras);
            foreach (var item in pages.Items ?? [])
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id) || ledger._pagesById.ContainsKey(item.Id))
                {
                    continue;
                }

                if (ledger.Take(item.Extras) is { } extras)
                {
                    ledger._pagesById[item.Id] = extras;
                }
            }
        }

        foreach (var snapshot in document.LayoutSnapshots ?? [])
        {
            if (snapshot is null || ledger._snapshotsById.ContainsKey(snapshot.Id ?? string.Empty))
            {
                continue;
            }

            if (ledger.Take(snapshot.Extras) is { } extras)
            {
                ledger._snapshotsById[snapshot.Id ?? string.Empty] = extras;
            }
        }

        if (document.SystemIconsPanel is { } panel)
        {
            ledger._systemIconsPanel = ledger.Take(panel.Extras);

            if (!string.IsNullOrEmpty(panel.IconSize) && !Enum.TryParse<SystemIconSize>(panel.IconSize, out _))
            {
                ledger.PreservedIconSize = new PreservedEnumValue(panel.IconSize, nameof(SystemIconSize.Medium));
            }

            foreach (var item in panel.Items ?? [])
            {
                if (item is null)
                {
                    continue;
                }

                var kind = TolerantEnumParser.Parse(item.Kind, SystemIconKind.ThisPc, out var recognized);
                if (!recognized)
                {
                    ledger._unknownSystemIconItems.Add(item);
                    ledger.UnknownKeyCount += item.Extras?.Count ?? 0;
                }
                else if (ledger.Take(item.Extras) is { } extras)
                {
                    ledger._systemIconItemsByKind.TryAdd(kind.ToString(), extras);
                }
            }
        }

        return ledger;
    }

    /// <summary>Writes the root, <c>pages</c> section and per-page extras into <paramref name="document"/>.</summary>
    /// <param name="document">The document about to be serialized.</param>
    public void ApplyToDocument(ApplicationStateDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Extras = Copy(_root);

        if (document.Pages is not { } pages)
        {
            return;
        }

        pages.Extras = Copy(_pagesSection);
        foreach (var item in pages.Items)
        {
            if (_pagesById.TryGetValue(item.Id, out var extras))
            {
                item.Extras = Copy(extras);
            }
        }
    }

    /// <summary>Writes the snapshot's own extras (looked up by snapshot id) into <paramref name="dto"/>.</summary>
    /// <param name="dto">The snapshot DTO about to be serialized.</param>
    public void ApplyToSnapshot(LayoutSnapshotStateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (_snapshotsById.TryGetValue(dto.Id ?? string.Empty, out var extras))
        {
            dto.Extras = Copy(extras);
        }
    }

    /// <summary>
    /// Writes the <c>systemIconsPanel</c> section extras, per-item extras, the unrecognised icon size and the
    /// unknown-kind items (appended after the known ones — position is irrelevant, <c>order</c> decides) into
    /// <paramref name="dto"/>.
    /// </summary>
    /// <param name="dto">The system-icons-panel DTO about to be serialized.</param>
    public void ApplyToSystemIconsPanel(SystemIconsPanelStateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        dto.Extras = Copy(_systemIconsPanel);

        if (PreservedIconSize is { } preserved && string.Equals(dto.IconSize, preserved.Fallback, StringComparison.Ordinal))
        {
            dto.IconSize = preserved.Raw;
        }

        foreach (var item in dto.Items)
        {
            if (_systemIconItemsByKind.TryGetValue(item.Kind, out var extras))
            {
                item.Extras = Copy(extras);
            }
        }

        foreach (var unknown in _unknownSystemIconItems)
        {
            dto.Items.Add(unknown);
        }
    }

    private static Dictionary<string, JsonElement>? Copy(Dictionary<string, JsonElement>? source)
        => source is { Count: > 0 } ? new Dictionary<string, JsonElement>(source) : null;

    private Dictionary<string, JsonElement>? Take(Dictionary<string, JsonElement>? source)
    {
        if (source is not { Count: > 0 })
        {
            return null;
        }

        UnknownKeyCount += source.Count;
        return new Dictionary<string, JsonElement>(source);
    }
}
