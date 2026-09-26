using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation that shows a modal <see cref="InputDialog"/> window.
/// </summary>
public sealed class InputDialogService : IInputDialogService
{
    /// <inheritdoc />
    public string? PromptText(string title, string prompt, string defaultValue)
    {
        var dialog = new InputDialog(title, prompt, defaultValue);

        // Owner = the currently active top-level window, if any (brief D-3-style convention already
        // used elsewhere in this window's family: a modal dialog should sit on top of — and minimize/
        // restore with — whichever window the user was just interacting with, rather than floating
        // independently). There may legitimately be none (e.g. every window is currently hidden), in
        // which case the dialog centers on the screen instead of failing to show at all.
        var owner = FindActiveWindow();
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true ? dialog.ResponseText : null;
    }

    private static Window? FindActiveWindow()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window.IsActive)
                return window;
        }

        return null;
    }
}
