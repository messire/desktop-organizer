namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Thin seam over the Windows folder-browser dialog (<see cref="Microsoft.Win32.OpenFolderDialog"/>),
/// extracted so <see cref="ViewModels.PanelCustomizeViewModel.BrowseFolderCommand"/> can be unit tested
/// without spawning a real Explorer picker (FIX-B fix-cycle, docs/prompts/settings-window-redesign.md
/// D-4 minor finding 5).
/// </summary>
public interface IFolderPickerService
{
    /// <summary>
    /// Shows a folder-browser dialog and returns the folder the user chose.
    /// </summary>
    /// <param name="title">The dialog's title text.</param>
    /// <param name="initialFolder">
    /// The folder to pre-select when the dialog opens (e.g. the panel's current Folder Portal path).
    /// Ignored (falls back to the dialog's own default) when <see langword="null"/>, empty, or the
    /// folder no longer exists.
    /// </param>
    /// <returns>The chosen folder's full path, or <see langword="null"/> if the user cancelled.</returns>
    string? PickFolder(string title, string? initialFolder);
}
