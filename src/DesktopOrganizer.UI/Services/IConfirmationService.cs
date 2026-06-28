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
    /// <param name="icon">
    /// The icon shown in the dialog. Defaults to <see cref="MessageBoxImage.Question"/>, appropriate
    /// for neutral/reversible confirmations. Callers guarding an irreversible, data-losing action
    /// (e.g. permanent deletion) should pass <see cref="MessageBoxImage.Warning"/> instead, matching
    /// Windows convention.
    /// </param>
    bool Confirm(string message, string title = "", MessageBoxImage icon = MessageBoxImage.Question);
}
