using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Severity levels <see cref="InfoBar"/> paints itself with (design-system-1x.md §2.2/§2.5/§2.8) — one
/// <see cref="ThemeKeys"/>-backed background token per case
/// (<see cref="DesktopOrganizer.UI.Theming.ThemeKeys.InfoBarInfoBackground"/> and its
/// Success/Caution/Critical siblings). Deliberately a distinct enum from
/// <see cref="DesktopOrganizer.UI.Services.StatusSeverity"/> (that contract's own service-facing severity):
/// <c>MainWindow.xaml</c>'s <c>StatusSeverityToInfoBarSeverityConverter</c> bridges the two by name, so
/// this control-facing enum never needs to reference the <c>Services</c> namespace or vice versa.
/// </summary>
public enum InfoBarSeverity
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
/// The Windows 11 "InfoBar" pattern (design-system-1x.md §2.5/§2.8, ADR-047 D-9) — a non-blocking, in-window
/// status surface that replaces a blocking <see cref="System.Windows.MessageBox"/> for a successful action
/// (e.g. "Панель перемещена на страницу …"). Hosted in <c>MainWindow.xaml</c> above its content
/// <see cref="ScrollViewer"/>, bound to <c>MainWindowViewModel.Status</c>
/// (<c>IStatusNotifier</c>/<c>SettingsStatusNotifier</c>, phase 6.3).
/// </summary>
/// <remarks>
/// A lookless <see cref="Control"/> (default style in <c>Resources/Styles/Theme.Controls.xaml</c>), the
/// same convention every other 6.2a control here follows — never persisted (ADR-024/G-1: this is
/// transient in-memory UI state, not application state).
/// </remarks>
/// <remarks>
/// Phase 6.3 (design-system-1x.md §2.8, ADR-047 D-9): raises its own
/// <see cref="AutomationEvents.LiveRegionChanged"/> whenever <see cref="Message"/> becomes a new,
/// non-empty value — <c>AutomationProperties.LiveSetting="Polite"</c> alone (set by this control's own
/// default style) only marks it as a live region; it does not, by itself, make Narrator announce anything
/// (the exact gap <c>PanelsSection.xaml.cs</c>'s own pre-6.3 manual subscription used to plug for its
/// since-removed inline status line — this control now does that itself, for every consumer).
/// </remarks>
public class InfoBar : Control
{
    /// <summary>Identifies the <see cref="Severity"/> dependency property.</summary>
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity),
        typeof(InfoBarSeverity),
        typeof(InfoBar),
        new PropertyMetadata(InfoBarSeverity.Informational));

    /// <summary>Identifies the <see cref="Title"/> dependency property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(InfoBar),
        new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Message"/> dependency property.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(InfoBar),
        new PropertyMetadata(null, OnMessageChanged));

    /// <summary>Identifies the <see cref="CloseCommand"/> dependency property.</summary>
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand),
        typeof(ICommand),
        typeof(InfoBar),
        new PropertyMetadata(null));

    static InfoBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InfoBar), new FrameworkPropertyMetadata(typeof(InfoBar)));
    }

    /// <summary>Gets or sets which of the four severity backgrounds (<see cref="ThemeKeys"/>'s <c>Theme.InfoBar.*.Background</c> tokens) this bar paints itself with.</summary>
    public InfoBarSeverity Severity
    {
        get => (InfoBarSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    /// <summary>Gets or sets the bold title line.</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the body text.</summary>
    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Gets or sets the command the dismiss ("×") button invokes.</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>
    /// Raises <see cref="AutomationEvents.LiveRegionChanged"/> whenever <see cref="Message"/> becomes a
    /// new, non-empty value — see this class's own remarks. Deferred to
    /// <see cref="DispatcherPriority.Background"/>: this callback runs synchronously as part of the
    /// property-changed notification, before a data-bound <see cref="Message"/> (the normal case — a
    /// ViewModel property, not a literal) has necessarily finished re-rendering into this control's visual
    /// tree — the same deferral <c>PanelsSection.xaml.cs</c>'s own pre-6.3 manual subscription used.
    /// </summary>
    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty((string?)e.NewValue)) return; // Cleared/hidden — nothing new to announce.
        if (d is not InfoBar infoBar) return;

        infoBar.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var peer = UIElementAutomationPeer.FromElement(infoBar) ?? UIElementAutomationPeer.CreatePeerForElement(infoBar);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }));
    }
}
