namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation that delegates to <see cref="IDialogPresenter"/>/<c>AppDialogWindow</c>'s input
/// mode (design-system-1x.md §2.8, ADR-047 D-9) — replacing the pre-6.3 <c>Views/InputDialog.xaml</c> window.
/// </summary>
public sealed class InputDialogService : IInputDialogService
{
    private readonly IDialogPresenter _presenter;
    private readonly ILocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="InputDialogService"/> class.
    /// </summary>
    /// <param name="presenter">Shows the underlying <c>AppDialogWindow</c>.</param>
    /// <param name="localization">Resolves this service's own "OK"/"Cancel" button labels.</param>
    /// <remarks>
    /// <see langword="internal"/> — see <see cref="ConfirmationService"/>'s identical constructor remarks
    /// (<see cref="IDialogPresenter"/> is itself <see langword="internal"/>, CS0051).
    /// </remarks>
    internal InputDialogService(IDialogPresenter presenter, ILocalizationService localization)
    {
        _presenter = presenter;
        _localization = localization;
    }

    /// <inheritdoc />
    public string? PromptText(string title, string prompt, string defaultValue)
    {
        // MessageBoxImage.None: this prompt has no severity glyph (AppDialogWindow collapses it for that
        // value). Validation preserves the pre-6.3 InputDialog's own behavior exactly — the primary
        // button is disabled while the box is empty/whitespace-only, with no separate inline message: an
        // empty (not null) string return means "invalid, but nothing worth spelling out" (see
        // AppDialogRequest.ValidateInput's own remarks).
        var result = _presenter.Show(new AppDialogRequest(
            title,
            prompt,
            MessageBoxImage.None,
            PrimaryText: _localization.GetString("Common_Ok_Button"),
            SecondaryText: _localization.GetString("Common_Cancel_Button"),
            PrimaryIsDefault: true,
            InputText: defaultValue,
            ValidateInput: text => string.IsNullOrWhiteSpace(text) ? string.Empty : null));

        // DialogPresenter.Show already nulls InputText out for any non-Primary result.
        return result.InputText;
    }
}
