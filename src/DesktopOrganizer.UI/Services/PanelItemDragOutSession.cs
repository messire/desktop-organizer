namespace DesktopOrganizer.UI.Services;

/// <inheritdoc cref="IPanelItemDragOutSession" />
internal sealed class PanelItemDragOutSession : IPanelItemDragOutSession
{
    private Guid? _sourcePanelId;
    private string? _path;
    private Guid? _claimedBy;

    /// <inheritdoc />
    public bool IsActive => _sourcePanelId is not null;

    /// <inheritdoc />
    public Guid? SourcePanelId => _sourcePanelId;

    /// <inheritdoc />
    public string? Path => _path;

    /// <inheritdoc />
    public void Begin(Guid sourcePanelId, string path)
    {
        _sourcePanelId = sourcePanelId;
        _path = path;
        _claimedBy = null;
    }

    /// <inheritdoc />
    public bool TryClaim(Guid targetPanelId, string path)
    {
        if (_sourcePanelId is null || _path is null) return false;
        if (_claimedBy is not null) return false; // single-claim: first caller wins.
        if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return false;

        _claimedBy = targetPanelId;

        // A self-claim (the source panel's own drag landing outside its file list) is recorded — so
        // End() resolves to Reordered, not External — but is never reported as an accepted transfer.
        return targetPanelId != _sourcePanelId;
    }

    /// <inheritdoc />
    public DragOutOutcome End()
    {
        var outcome = _claimedBy is null
            ? new DragOutOutcome(DragOutOutcomeKind.External, null)
            : _claimedBy == _sourcePanelId
                ? new DragOutOutcome(DragOutOutcomeKind.Reordered, null)
                : new DragOutOutcome(DragOutOutcomeKind.TransferredTo, _claimedBy);

        _sourcePanelId = null;
        _path = null;
        _claimedBy = null;
        return outcome;
    }
}
