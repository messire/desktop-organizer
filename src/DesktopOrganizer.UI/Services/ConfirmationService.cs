namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default implementation that delegates to <see cref="MessageBox"/>.
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    /// <inheritdoc />
    public bool Confirm(string message, string title = "", MessageBoxImage icon = MessageBoxImage.Question)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, icon)
           == MessageBoxResult.Yes;

    /// <inheritdoc />
    public void Notify(string message, string title, MessageBoxImage icon)
        => MessageBox.Show(message, title, MessageBoxButton.OK, icon);
}
