using System.Windows.Threading;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Drop of files onto the sidebar's Recycle Bin icon (ADR-035 amendment, ADR-audit review cycle 1 G1): the
/// Recycle Bin operation never runs inside the OLE <c>Drop</c> call — it is posted to the dispatcher and runs
/// after the drag source's <c>DoDragDrop</c> has returned.
/// </summary>
/// <remarks>
/// <para>
/// Why deferred: since ADR-035/WP-A the operation can show shell UI (the <c>FOF_WANTNUKEWARNING</c> "delete
/// permanently?" question, the progress dialog). Run synchronously inside <c>Drop</c>, that UI would be modal
/// while the source's drag loop (Explorer, 7-Zip, Total Commander…) is still blocked waiting for <c>Drop</c> to
/// return — the source window freezes, which looks like a hang.
/// </para>
/// <para>
/// Why the reported effect is <see cref="DragDropEffects.None"/>: at the moment <c>Drop</c> returns nothing has
/// happened yet, and for a <see cref="DragDropEffects.Move"/> an "unoptimized move" source deletes the originals
/// itself once <c>DoDragDrop</c> returns. That would destroy the files for good before the Recycle Bin operation
/// even starts — and also if the user then declines the nuke warning. <see cref="DragDropEffects.None"/> is the
/// effect the shell's own data-transfer guidance prescribes for a target that performs the move itself (an
/// "optimized move"), and it is the only value no source interprets as "now delete your copy". The drag-over
/// cursor still shows <see cref="DragDropEffects.Move"/>, like the real Recycle Bin.
/// </para>
/// </remarks>
internal static class RecycleBinFileDrop
{
    /// <summary>The effect reported back to the drag source for a file drop on the Recycle Bin — see the remarks.</summary>
    internal const DragDropEffects ReportedEffect = DragDropEffects.None;

    /// <summary>
    /// Takes the dropped file list out of <paramref name="data"/>, copies it, and posts
    /// <paramref name="recycle"/> to <paramref name="dispatcher"/> at <see cref="DispatcherPriority.Background"/>.
    /// Never throws.
    /// </summary>
    /// <param name="data">The drop's data object (possibly a foreign OLE object).</param>
    /// <param name="dispatcher">The dispatcher of the window that received the drop.</param>
    /// <param name="recycle">The Recycle Bin operation; receives a private copy of the paths.</param>
    /// <returns>
    /// <see langword="true"/> when the data carried a non-empty file list and the operation was queued — the drop
    /// is handled and <see cref="ReportedEffect"/> must be reported; <see langword="false"/> otherwise (e.g. a
    /// sidebar reorder drag, which has to bubble on).
    /// </returns>
    internal static bool TryQueue(IDataObject data, Dispatcher dispatcher, Action<string[]> recycle)
    {
        string[] paths;
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
            if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } dropped) return false;

            // A private copy: the array belongs to the drop's data object, which the source releases as soon as
            // DoDragDrop returns — before the queued operation runs.
            paths = (string[])dropped.Clone();
        }
        catch (Exception)
        {
            // A foreign data object can fail any call (COMException, OutOfMemoryException from a huge HDROP…):
            // treat it as "not a file drop".
            return false;
        }

        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => recycle(paths)));
            return true;
        }
        catch (Exception)
        {
            // Dispatcher shutting down — nothing will run; report the drop as not handled.
            return false;
        }
    }
}
