namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Abstracts a small modal text-entry prompt so ViewModels stay free of UI dependencies (mirrors
/// <see cref="IConfirmationService"/>'s Yes/No prompt, but for free-form text input — e.g. naming a
/// layout snapshot before it is captured).
/// </summary>
public interface IInputDialogService
{
    /// <summary>
    /// Shows a modal text-entry dialog pre-filled with <paramref name="defaultValue"/> and returns the
    /// trimmed text the user confirmed.
    /// </summary>
    /// <param name="title">The caption of the dialog window.</param>
    /// <param name="prompt">The label describing what the user is being asked to enter.</param>
    /// <param name="defaultValue">The initial, pre-selected text shown in the input box.</param>
    /// <returns>
    /// The trimmed text the user entered, or <see langword="null"/> if the user cancelled (Escape, the
    /// Cancel button, or closing the window). The OK button is disabled while the input box is empty or
    /// whitespace-only, so a non-<see langword="null"/> result is never empty or whitespace-only either.
    /// </returns>
    string? PromptText(string title, string prompt, string defaultValue);
}
