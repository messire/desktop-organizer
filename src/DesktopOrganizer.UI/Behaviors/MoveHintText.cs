using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Builds the text of the one-time "the file was moved into the panel" hint (<see cref="HintPopupController.ShowMoveHint"/>)
/// as a pure function of the language, the folder the moved file lives in and the distribution channel, so the
/// wording is unit-testable without a WPF popup.
/// </summary>
internal static class MoveHintText
{
    /// <summary>
    /// Composes the hint: always names <paramref name="folder"/> (where the file now lives); when
    /// <paramref name="survivesUninstall"/> is set it adds that the file also stays there after the app is
    /// uninstalled. The retention sentence is channel-specific on purpose: the Inno uninstaller hands the
    /// files back to the desktop, so the same sentence would be false there.
    /// </summary>
    /// <param name="localization">Resolves the localized sentences for the current language.</param>
    /// <param name="folder">The managed panel-items folder (<c>AppStorageRoot.PanelItemsDirectory</c>).</param>
    /// <param name="survivesUninstall">
    /// Whether the folder outlives the app's uninstall, so the retention sentence is true (the package-identity
    /// / Microsoft Store build: <c>AppStorageRoot.IsPackagedLocation</c>).
    /// </param>
    /// <returns>The hint text; one or two sentences separated by a single space.</returns>
    internal static string Compose(ILocalizationService localization, string folder, bool survivesUninstall)
    {
        ArgumentNullException.ThrowIfNull(localization);

        var message = localization.Format("Hint_Move_Message_Format", folder);
        return survivesUninstall
            ? string.Join(' ', message, localization.GetString("Hint_Move_PackagedRetention"))
            : message;
    }
}
