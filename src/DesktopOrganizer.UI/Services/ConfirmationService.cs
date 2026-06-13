namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation that delegates to <see cref="MessageBox"/>.
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    /// <inheritdoc />
    public bool Confirm(string message, string title = "")
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;
}
