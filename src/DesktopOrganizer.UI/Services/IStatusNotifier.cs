using System.ComponentModel;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Severity of a <see cref="StatusItem"/> (design-system-1x.md §2.8, ADR-047 D-9) — deliberately a
/// distinct type from <see cref="DesktopOrganizer.UI.Controls.InfoBarSeverity"/> (that enum's own remarks
/// left this phase free to choose): this one lives on the service-facing contract
/// <see cref="IStatusNotifier"/>, so a future non-<c>InfoBar</c> consumer of that contract never has to
/// reference the <c>Controls</c> namespace. <c>MainWindow.xaml</c>'s
/// <c>StatusSeverityToInfoBarSeverityConverter</c> maps one onto the other by name — every case below has
/// an identically-named <see cref="DesktopOrganizer.UI.Controls.InfoBarSeverity"/> sibling.
/// </summary>
public enum StatusSeverity
{
    /// <summary>Neutral, informational message.</summary>
    Informational,

    /// <summary>A completed action succeeded.</summary>
    Success,

    /// <summary>A non-blocking warning the user should notice but that does not require action.</summary>
    Caution,

    /// <summary>A failure the user must notice.</summary>
    Critical,
}

/// <summary>
/// A single non-blocking status message <see cref="IStatusNotifier"/> is currently showing — immutable,
/// never persisted (ADR-024/G-1: purely transient, in-memory UI state).
/// </summary>
/// <param name="Severity">Which of the four severities this status is.</param>
/// <param name="Message">The body text.</param>
/// <param name="Title">The bold title line, or <see langword="null"/> for a message-only status.</param>
public sealed record StatusItem(StatusSeverity Severity, string Message, string? Title);

/// <summary>
/// A non-blocking, in-window status surface (the Windows 11 "InfoBar" pattern, design-system-1x.md
/// §2.8, ADR-047 D-9) — replaces a blocking <see cref="System.Windows.MessageBox"/> for a successful
/// action, e.g. "Панель перемещена на страницу …" (<c>MainWindowViewModel.MovePanelToPage</c>/
/// <c>CopyPanelToPage</c>). <see cref="Current"/>/<see cref="DismissCommand"/> back <c>MainWindow.xaml</c>'s
/// <c>InfoBar</c> host directly — never modal, never persisted.
/// </summary>
public interface IStatusNotifier : INotifyPropertyChanged
{
    /// <summary>The status currently shown, or <see langword="null"/> when nothing is showing.</summary>
    StatusItem? Current { get; }

    /// <summary>Dismisses <see cref="Current"/> — bound to the <c>InfoBar</c>'s own close ("×") button.</summary>
    ICommand DismissCommand { get; }

    /// <summary>
    /// Shows a new status, replacing whatever <see cref="Current"/> already held. <see cref="StatusSeverity.Informational"/>
    /// and <see cref="StatusSeverity.Success"/> auto-hide after a short delay; <see cref="StatusSeverity.Caution"/>
    /// and <see cref="StatusSeverity.Critical"/> stay until explicitly dismissed.
    /// </summary>
    /// <param name="severity">The status's severity.</param>
    /// <param name="message">The body text.</param>
    /// <param name="title">The bold title line, or <see langword="null"/> for a message-only status.</param>
    void Show(StatusSeverity severity, string message, string? title = null);

    /// <summary>Dismisses <see cref="Current"/> immediately. A no-op if nothing is showing.</summary>
    void Dismiss();
}
