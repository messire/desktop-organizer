using System.Windows.Controls;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Modal text-entry prompt shown by <see cref="DesktopOrganizer.UI.Services.InputDialogService"/>.
/// A plain activatable window (no <c>WS_EX_NOACTIVATE</c> involved) — code-behind here is limited to
/// what XAML cannot express: seeding the initial text, focusing the input box, and toggling the OK
/// button's enabled state as the user types.
/// </summary>
public partial class InputDialog : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InputDialog"/> class.
    /// </summary>
    /// <param name="title">The caption of the dialog window.</param>
    /// <param name="prompt">The label describing what the user is being asked to enter.</param>
    /// <param name="defaultValue">The initial, pre-selected text shown in the input box.</param>
    public InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();

        Title = title;
        PromptTextBlock.Text = prompt;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();

        // TextChanged (wired in XAML) only fires on an actual change, so it never runs if
        // defaultValue is itself empty/whitespace (TextBox already starts out empty) — set the
        // initial enabled state explicitly rather than relying on that event firing at least once.
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(defaultValue);

        // Deferred to Loaded: the window (and its default OS-activated top-level HWND) must exist
        // before keyboard focus can be moved onto a child element.
        Loaded += (_, _) => InputBox.Focus();
    }

    /// <summary>
    /// The trimmed text entered by the user. Only meaningful once <see cref="Window.ShowDialog"/> has
    /// returned <see langword="true"/> — <see cref="Services.InputDialogService.PromptText"/> reads this
    /// immediately afterwards.
    /// </summary>
    public string ResponseText => InputBox.Text.Trim();

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);

    private void OkButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
