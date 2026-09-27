namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// How a native shell context menu (<see cref="IFileLauncher.ShowContextMenu"/>) ended.
/// </summary>
/// <remarks>
/// <c>default(ShellContextMenuOutcome)</c> is <see cref="NotShown"/>, so a loose test double that returns
/// the default value never makes a caller believe a shell command ran.
/// </remarks>
public enum ShellContextMenuOutcome
{
    /// <summary>
    /// No menu was shown: the item could not be resolved by the shell (missing file, unparsable path),
    /// the shell refused to build a menu, or a menu was already open. Logged by the native layer.
    /// </summary>
    NotShown,

    /// <summary>The menu was shown and dismissed without choosing a command.</summary>
    Dismissed,

    /// <summary>
    /// A shell command was chosen and handed to the shell (<c>IContextMenu::InvokeCommand</c>). The command
    /// may have renamed, deleted or moved the item on disk — callers reconcile their view of it afterwards.
    /// </summary>
    Invoked,

    /// <summary>
    /// The shell's own "Rename" command was chosen. It is not invoked (the shell has no in-place editor to
    /// start outside Explorer's view) — the caller starts its own inline rename instead.
    /// </summary>
    RenameRequested,

    /// <summary>
    /// The shell's own "Delete" command was chosen while the caller asked for it to be intercepted
    /// (<see cref="IFileLauncher.ShowContextMenu"/> with <c>interceptDelete</c>). It is not invoked — the caller
    /// deletes the item itself (for an app-managed item: back to its original location first, then to the
    /// Recycle Bin, so a later "Restore" from the Recycle Bin puts it where the user can see it).
    /// </summary>
    DeleteRequested,
}

/// <summary>
/// Result of <see cref="IFileLauncher.ShowContextMenu"/> (M-B, release-polish 1.0 final review): how the
/// menu ended, plus — when a command was actually handed to the shell — its canonical verb
/// (<c>IContextMenu.GetCommandString(GCS_VERBW)</c>, e.g. <c>"copy"</c>, <c>"cut"</c>, <c>"open"</c>,
/// <c>"createshortcut"</c>), already read by the native layer to recognize the intercepted
/// <c>rename</c>/<c>delete</c> verbs. Exposing it lets a caller decide whether the invoked command could
/// plausibly have created a new file next to the item (see <c>ChildWindowViewModel</c>'s sibling-adoption
/// watch) without re-deriving it from <see cref="Outcome"/> alone, which cannot tell "Copy" from "Create
/// shortcut" from "Properties".
/// </summary>
/// <param name="Outcome">How the menu ended.</param>
/// <param name="Verb">
/// The chosen command's canonical verb, lower-cased by the shell handler by convention but always compared
/// case-insensitively by callers. <see langword="null"/> when <see cref="Outcome"/> is not
/// <see cref="ShellContextMenuOutcome.Invoked"/>, or the handler did not report one for the chosen command
/// (some third-party shell extensions don't implement <c>GCS_VERBW</c>).
/// </param>
public readonly record struct ShellContextMenuResult(ShellContextMenuOutcome Outcome, string? Verb);
