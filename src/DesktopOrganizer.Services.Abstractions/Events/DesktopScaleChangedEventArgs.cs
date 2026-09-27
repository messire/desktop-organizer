namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Payload of <see cref="IDesktopScaleProvider.Changed"/>: the snapshot that was replaced and the one that
/// replaced it.
/// </summary>
/// <param name="oldModel">The previous snapshot.</param>
/// <param name="newModel">The new, now current snapshot.</param>
public sealed class DesktopScaleChangedEventArgs(DesktopScaleModel oldModel, DesktopScaleModel newModel) : EventArgs
{
    /// <summary>Gets the previous snapshot.</summary>
    public DesktopScaleModel Old { get; } = oldModel;

    /// <summary>Gets the new snapshot (equal to <see cref="IDesktopScaleProvider.Current"/> when raised).</summary>
    public DesktopScaleModel New { get; } = newModel;

    /// <summary>Gets a value indicating whether the desktop layer mode itself changed (e.g. Embedded → TopLevel after an Explorer restart).</summary>
    public bool ModeChanged => Old.Mode != New.Mode;
}
