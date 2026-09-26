using System.IO;
using Microsoft.Win32;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default <see cref="IFolderPickerService"/> implementation, backed by
/// <see cref="OpenFolderDialog"/> (the modern Win32 folder picker, replacing the legacy
/// <c>FolderBrowserDialog</c>/<c>SHBrowseForFolder</c> shell dialog).
/// </summary>
public sealed class FolderPickerService : IFolderPickerService
{
    /// <inheritdoc />
    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
