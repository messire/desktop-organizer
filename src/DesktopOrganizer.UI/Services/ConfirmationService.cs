namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation that delegates to <see cref="IDialogPresenter"/>/<c>AppDialogWindow</c>
/// (design-system-1x.md §2.8, ADR-047 D-9) — replacing the pre-6.3 direct <c>MessageBox.Show</c> call.
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    private readonly IDialogPresenter _presenter;
    private readonly ILocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfirmationService"/> class.
    /// </summary>
    /// <param name="presenter">Shows the underlying <c>AppDialogWindow</c>.</param>
    /// <param name="localization">Resolves this service's own "Yes"/"No"/"OK" button labels.</param>
    /// <remarks>
    /// <see langword="internal"/> (not <see langword="public"/>): <see cref="IDialogPresenter"/> is itself
    /// <see langword="internal"/> (CS0051 forbids a public member from exposing a less-accessible type in
    /// its signature) — <c>Configuration/DiConfig.cs</c> constructs this class directly via a factory
    /// lambda instead of the reflection-based <c>AddSingleton&lt;TService, TImplementation&gt;()</c> every
    /// other registration in that file uses, since the DI container's own reflection only ever looks at
    /// PUBLIC constructors.
    /// </remarks>
    internal ConfirmationService(IDialogPresenter presenter, ILocalizationService localization)
    {
        _presenter = presenter;
        _localization = localization;
    }

    /// <inheritdoc />
    public bool Confirm(string message, string title = "", MessageBoxImage icon = MessageBoxImage.Question)
    {
        // ADR-047 D-9/R-12: the visual primary button is always "Да" (the affirmative action) — which
        // button Enter defaults to is what actually differs by icon (Warning defaults to the safe "Нет",
        // guarding an irreversible action against an accidental Enter; Question — the neutral default —
        // keeps Enter on "Да"). PrimaryIsDefault below is exactly that Enter-target flag, independent of
        // PrimaryText/SecondaryText's own fixed visual roles.
        var result = _presenter.Show(new AppDialogRequest(
            title,
            message,
            icon,
            PrimaryText: _localization.GetString("Common_Yes_Button"),
            SecondaryText: _localization.GetString("Common_No_Button"),
            PrimaryIsDefault: icon != MessageBoxImage.Warning));

        return result.Button == AppDialogButton.Primary;
    }

    /// <inheritdoc />
    public void Notify(string message, string title, MessageBoxImage icon)
    {
        // A single "OK" button (no SecondaryText) — AppDialogWindow makes it double as IsCancel too, so
        // Escape/closing the window behaves exactly like clicking it.
        _presenter.Show(new AppDialogRequest(
            title,
            message,
            icon,
            PrimaryText: _localization.GetString("Common_Ok_Button"),
            SecondaryText: null,
            PrimaryIsDefault: true));
    }
}
