namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Which button an <see cref="AppDialogRequest"/> was dismissed with. Deliberately independent of the
/// PRIMARY/secondary VISUAL styling (<see cref="AppDialogRequest.PrimaryIsDefault"/> already carries which
/// one Enter triggers) — see <see cref="AppDialogRequest"/>'s own remarks.
/// </summary>
internal enum AppDialogButton
{
    /// <summary>The visually primary (accent-filled) action button.</summary>
    Primary,

    /// <summary>The secondary button, or — for a single-button dialog — Escape/closing the window.</summary>
    Secondary,
}

/// <summary>
/// The outcome of showing an <see cref="AppDialogRequest"/>.
/// </summary>
/// <param name="Button">Which button the dialog was dismissed with.</param>
/// <param name="InputText">
/// For an input-mode request (<see cref="AppDialogRequest.InputText"/> was non-<see langword="null"/>),
/// the trimmed text the user confirmed — only populated when <paramref name="Button"/> is
/// <see cref="AppDialogButton.Primary"/>; <see langword="null"/> otherwise (cancelled) or for a
/// non-input-mode request.
/// </param>
internal sealed record AppDialogResult(AppDialogButton Button, string? InputText = null);

/// <summary>
/// A fully-described request to show one <see cref="Views.Dialogs.AppDialogWindow"/> — the seam
/// <see cref="ConfirmationService"/>/<see cref="InputDialogService"/> depend on
/// (<see cref="IDialogPresenter"/>) instead of constructing/showing a real <see cref="System.Windows.Window"/>
/// themselves, so their own unit tests never need one (design-system-1x.md §2.8, ADR-047 D-9).
/// </summary>
/// <param name="Title">The dialog's caption/heading text.</param>
/// <param name="Message">The body text.</param>
/// <param name="Icon">
/// The severity glyph to show (<see cref="System.Windows.MessageBoxImage.None"/> hides it entirely — used
/// by the input-mode prompt, which has no severity of its own).
/// </param>
/// <param name="PrimaryText">The visually primary (accent-filled) button's label — always an action, never "Cancel"/"No".</param>
/// <param name="SecondaryText">
/// The secondary button's label, or <see langword="null"/> for a single-button dialog (in which case the
/// primary button itself also closes the dialog on Escape/window-close).
/// </param>
/// <param name="PrimaryIsDefault">
/// Whether Enter triggers the PRIMARY button (<see langword="true"/>) or the secondary one
/// (<see langword="false"/>) — independent of which one is drawn as the visually primary, accent-filled
/// button (<see cref="PrimaryText"/> always is). This is what lets a <see cref="System.Windows.MessageBoxImage.Warning"/>
/// confirmation keep "Да" as its visual primary while still defaulting Enter to "Нет" (ADR-047 D-9,
/// docs/PLANS/design-system-1x.md §2.8 R-12).
/// </param>
/// <param name="InputText">
/// Non-<see langword="null"/> switches the dialog into input mode: shows a <see cref="System.Windows.Controls.TextBox"/>
/// pre-filled and pre-selected with this text.
/// </param>
/// <param name="ValidateInput">
/// Input-mode only. Called on every keystroke with the box's current text: <see langword="null"/> means
/// valid; a non-<see langword="null"/> return (possibly <see cref="string.Empty"/> for "invalid, but no
/// message worth showing") disables the primary button and, if non-empty, shows the string as an inline
/// error (<c>Theme.Text.Critical</c>).
/// </param>
internal sealed record AppDialogRequest(
    string Title,
    string Message,
    MessageBoxImage Icon,
    string PrimaryText,
    string? SecondaryText,
    bool PrimaryIsDefault,
    string? InputText = null,
    Func<string, string?>? ValidateInput = null);

/// <summary>
/// Shows one <see cref="AppDialogRequest"/> modally and reports how it was dismissed — the production
/// implementation (<see cref="DialogPresenter"/>) is a thin owner-resolution/activation wrapper around a
/// real <see cref="Views.Dialogs.AppDialogWindow"/>; <see cref="ConfirmationService"/>/
/// <see cref="InputDialogService"/> depend on this seam instead, so their own unit tests can substitute a
/// fake that never shows a window at all.
/// </summary>
internal interface IDialogPresenter
{
    /// <summary>Shows <paramref name="request"/> modally and returns once the user has dismissed it.</summary>
    /// <param name="request">The dialog to show.</param>
    /// <returns>How the dialog was dismissed.</returns>
    AppDialogResult Show(AppDialogRequest request);
}
