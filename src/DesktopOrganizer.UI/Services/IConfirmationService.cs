namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Abstracts confirmation dialogs so ViewModels stay free of UI dependencies.
/// </summary>
public interface IConfirmationService
{
    /// <summary>
    /// Displays a Yes/No confirmation dialog and returns <c>true</c> when the user chooses Yes.
    /// </summary>
    /// <param name="message">The body text of the dialog.</param>
    /// <param name="title">The caption of the dialog window.</param>
    bool Confirm(string message, string title = "");
}
