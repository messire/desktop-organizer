namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Root schema-v3 payload (ADR-024 D-5/D-9): the four sections that used to be four independent
/// <c>user.config</c> writers (<c>panels</c>, <c>pages</c>, <c>layoutSnapshots</c>,
/// <c>systemIconsPanel</c> — ADR-024 §0.1), now serialized together as one document by
/// <see cref="ApplicationStateService"/>. Envelope keys (<c>schemaVersion</c>/<c>writtenAtUtc</c>/
/// <c>appVersion</c>) are not part of this type — they are added/stripped by
/// <c>DesktopOrganizer.Services.Storage.StateEnvelope</c>, one layer below this one.
/// </summary>
internal sealed class ApplicationStateDocumentDto
{
    public List<PanelStateDto> Panels { get; set; } = [];

    public PagesStateDto Pages { get; set; } = new();

    public List<LayoutSnapshotStateDto> LayoutSnapshots { get; set; } = [];

    public SystemIconsPanelStateDto SystemIconsPanel { get; set; } = new();
}
