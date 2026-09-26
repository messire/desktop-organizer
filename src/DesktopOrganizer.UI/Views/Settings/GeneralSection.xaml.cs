using System.Windows.Controls;

namespace DesktopOrganizer.UI.Views.Settings;

/// <summary>
/// "General" settings section (docs/prompts/settings-window-redesign.md): autostart and the collapsed
/// "Advanced" sub-panel (native resize, one-time hints reset). DataContext is the hosting
/// <c>MainWindow</c>'s <see cref="ViewModels.MainWindowViewModel"/>, inherited via
/// <c>MainWindow.xaml</c>'s content-switching <see cref="System.Windows.Controls.ContentControl"/>.
/// </summary>
public partial class GeneralSection : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="GeneralSection"/> class.</summary>
    public GeneralSection() => InitializeComponent();
}
