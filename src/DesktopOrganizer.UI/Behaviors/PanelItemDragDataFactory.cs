using System.Windows;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Builds the multi-format <see cref="DataObject"/> for a panel-item drag-out gesture (ADR-036,
/// release-hardening-1.0.md §E). A single <see cref="System.Windows.DragDrop.DoDragDrop"/> call now
/// carries three formats at once: the panel's own private drag-to-reorder format (so a drop back onto
/// the SAME panel's file list still reorders exactly as before ADR-014), <see cref="DataFormats.FileDrop"/>
/// (so a DIFFERENT panel, Explorer, or the desktop all recognize the drag as an ordinary file drop), and
/// the shell's "Preferred DropEffect" clipboard format set to <see cref="DragDropEffects.Move"/> —
/// Explorer's own default for a bare <see cref="DataFormats.FileDrop"/> payload is a copy; this format is
/// what makes it move the file instead, matching Stardock Fences' own drag-out behavior.
/// </summary>
/// <remarks>
/// Pure — no side effects, no session/state of its own. Callers (<c>ChildWindow</c>) are responsible for
/// starting/ending the actual drag-out session around this factory's output.
/// </remarks>
public static class PanelItemDragDataFactory
{
    /// <summary>
    /// The clipboard format name the shell reads to decide a drag source's preferred drop effect (move
    /// vs. copy) for a <see cref="DataFormats.FileDrop"/> payload.
    /// </summary>
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    /// <summary>
    /// Builds the drag-out payload for <paramref name="path"/>.
    /// </summary>
    /// <param name="reorderFormat">
    /// The owning panel's private drag-to-reorder <see cref="DataObject"/> format name (see
    /// <c>ChildWindow.PanelItemReorderFormat</c>).
    /// </param>
    /// <param name="path">
    /// The dragged item's file-system path. Used verbatim as the reorder format's payload (matching
    /// <c>ChildWindow</c>'s existing <c>createPayload: item =&gt; item.Path</c>) and becomes the sole
    /// entry in the <see cref="DataFormats.FileDrop"/> array.
    /// </param>
    /// <param name="isReadOnly">
    /// <see langword="true"/> for a read-only source (a Folder Portal, ADR-036 DoD 4) — no drag-out
    /// payload is ever built for one, and this returns <see langword="null"/> so the caller aborts the
    /// drag before <see cref="System.Windows.DragDrop.DoDragDrop"/> is ever invoked.
    /// </param>
    /// <returns>
    /// The three-format <see cref="DataObject"/>, or <see langword="null"/> when <paramref name="isReadOnly"/>
    /// is <see langword="true"/> or <paramref name="path"/> is null/blank.
    /// </returns>
    public static DataObject? Create(string reorderFormat, string path, bool isReadOnly)
    {
        if (isReadOnly || string.IsNullOrWhiteSpace(path)) return null;

        var data = new DataObject();
        data.SetData(reorderFormat, path);
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetData(PreferredDropEffectFormat, BitConverter.GetBytes((int)DragDropEffects.Move));
        return data;
    }
}
