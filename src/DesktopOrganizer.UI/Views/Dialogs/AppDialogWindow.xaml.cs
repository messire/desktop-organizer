using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Views.Dialogs;

/// <summary>
/// The single modal dialog window behind <c>ConfirmationService</c>/<c>InputDialogService</c>
/// (design-system-1x.md §2.8, ADR-047 D-9) — shown only by <see cref="DialogPresenter"/>, never
/// constructed directly by a ViewModel. Code-behind here is limited to what an
/// <see cref="AppDialogRequest"/> cannot already express in XAML: wiring the request's text/icon onto the
/// window, toggling input mode, and tracking which button closed it.
/// </summary>
/// <remarks>
/// Never owned by a <c>ChildWindow</c> (which carries <c>WS_EX_NOACTIVATE</c>, ADR-001/013, and cannot be
/// meaningfully "activated" as an owner) — <see cref="DialogPresenter"/> only ever assigns another chrome
/// window (settings/Customize) as <see cref="System.Windows.Window.Owner"/>, or none at all.
/// </remarks>
public partial class AppDialogWindow : Window
{
    private readonly ThemedWindowBehavior _themedWindowBehavior;
    private readonly Func<string, string?>? _validateInput;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppDialogWindow"/> class from <paramref name="request"/>.
    /// </summary>
    /// <param name="request">Describes the dialog's text, icon, buttons, and (optionally) input mode.</param>
    /// <param name="appThemeService">Design system 1.x §3 phase 6.1: follows the resolved chrome theme (<see cref="ThemedWindowBehavior"/>).</param>
    /// <param name="windowFrameTheming">Applies the DWM immersive dark-mode frame attribute for <paramref name="appThemeService"/>'s theme.</param>
    /// <param name="localization">Resolves the severity glyph's accessible name.</param>
    /// <remarks>
    /// <see langword="internal"/> (not <see langword="public"/>, unlike this class itself): <see cref="AppDialogRequest"/>
    /// is itself <see langword="internal"/> (CS0051 forbids a public member from exposing a less-accessible
    /// type in its signature) — only <see cref="DialogPresenter"/> (same assembly) ever constructs this window.
    /// </remarks>
    internal AppDialogWindow(AppDialogRequest request, IAppThemeService appThemeService, IWindowFrameTheming windowFrameTheming, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(localization);

        InitializeComponent();
        _themedWindowBehavior = new ThemedWindowBehavior(this, appThemeService, windowFrameTheming);
        _validateInput = request.ValidateInput;

        Title = request.Title;
        TitleText.Text = request.Title;
        TitleText.Visibility = string.IsNullOrEmpty(request.Title) ? Visibility.Collapsed : Visibility.Visible; // M-1 defensive: no heading to announce/show for a blank title.
        MessageText.Text = request.Message;

        // M-1 (a11y): a screen reader landing directly on a button (Tab/first focus) otherwise never
        // hears the message body at all — only the button's own short label ("Да"/"Нет"/"OK"). Setting
        // the SAME HelpText on the window, and on both buttons, means whichever element Narrator is
        // actually on (window frame or a button) still exposes the message it is about.
        AutomationProperties.SetHelpText(this, request.Message);
        AutomationProperties.SetHelpText(PrimaryButton, request.Message);
        AutomationProperties.SetHelpText(SecondaryButton, request.Message);

        var glyphKey = ResolveGlyphKey(request.Icon);
        if (glyphKey is null)
        {
            SeverityGlyphText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SeverityGlyphText.Text = (string)FindResource(glyphKey);
            AutomationProperties.SetName(SeverityGlyphText, localization.GetString(ResolveSeverityNameKey(request.Icon)));
        }

        PrimaryButton.Content = request.PrimaryText;
        PrimaryButton.IsDefault = request.PrimaryIsDefault;

        if (request.SecondaryText is { } secondaryText)
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.IsDefault = !request.PrimaryIsDefault;
            SecondaryButton.IsCancel = true;
        }
        else
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.IsCancel = true;
        }

        if (request.InputText is { } inputText)
        {
            InputRow.Visibility = Visibility.Visible;
            InputBox.Text = inputText;
            InputBox.SelectAll();
            UpdateInputValidity();
        }

        // Deferred to Loaded: the window (and its default OS-activated top-level HWND) must exist before
        // keyboard focus can be moved — same convention the pre-6.3 InputDialog used. R-6 mitigation
        // (docs/PLANS/design-system-1x.md §7, see DialogPresenter's own remarks): when no owner is
        // assigned (DialogPresenter.Topmost is already true in that case), this window explicitly claims
        // activation instead of merely hoping Windows grants a brand-new top-level window the foreground.
        // L-1 fix (review round): reads Owner directly inside OnLoaded rather than caching it into a field
        // constructed BEFORE DialogPresenter.Show assigns Owner — a field captured here would always see
        // null (Owner is set on the returned instance only after this constructor returns), silently
        // defeating this whole mitigation for every owned dialog too.
        Loaded += OnLoaded;
    }

    /// <summary>Which button this dialog was dismissed with — <see cref="AppDialogButton.Secondary"/> until a click sets it otherwise.</summary>
    internal AppDialogButton ResultButton { get; private set; } = AppDialogButton.Secondary;

    /// <summary>
    /// The trimmed text entered in input mode. Only meaningful once <see cref="Window.ShowDialog"/> has
    /// returned <see langword="true"/> with <see cref="ResultButton"/> <see cref="AppDialogButton.Primary"/> —
    /// <see cref="DialogPresenter"/> reads this immediately afterwards.
    /// </summary>
    internal string ResponseText => InputBox.Text.Trim();

    private static string? ResolveGlyphKey(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.None => null,
        MessageBoxImage.Question => "Icon.Glyph.Question",
        MessageBoxImage.Warning => "Icon.Glyph.Warning", // == MessageBoxImage.Exclamation
        MessageBoxImage.Error => "Icon.Glyph.Error", // == MessageBoxImage.Hand/.Stop
        _ => "Icon.Glyph.Info", // MessageBoxImage.Information == .Asterisk
    };

    private static string ResolveSeverityNameKey(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Question => "Common_Severity_Question_A11y",
        MessageBoxImage.Warning => "Common_Severity_Warning_A11y",
        MessageBoxImage.Error => "Common_Severity_Error_A11y",
        _ => "Common_Severity_Info_A11y",
    };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (Owner is null)
        {
            Activate();

            // L-2 fallback (review round, same spirit as PanelCustomizeWindow.ShowAndActivate's own
            // Topmost-toggle fallback): Activate() immediately after Loaded can still lose to Windows'
            // foreground-lock timeout on a real system. Re-asserting it once more, deferred to
            // DispatcherPriority.Input (i.e. after the input queue this window's own brand-new HWND just
            // joined has had a chance to settle), is a second, cheap attempt before giving up — Topmost
            // (already set by DialogPresenter for this no-owner case) is the last line of defense.
            if (!IsActive)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!IsActive) Activate();
                }));
            }
        }

        if (InputRow.Visibility == Visibility.Visible)
        {
            InputBox.Focus();
        }
        else
        {
            (PrimaryButton.IsDefault ? PrimaryButton : SecondaryButton).Focus();
        }
    }

    private void UpdateInputValidity()
    {
        var error = _validateInput?.Invoke(InputBox.Text);
        PrimaryButton.IsEnabled = error is null;
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateInputValidity();

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        ResultButton = AppDialogButton.Primary;
        DialogResult = true;
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        ResultButton = AppDialogButton.Secondary;
        DialogResult = false;
    }
}
