using DesktopOrganizer.UI.Commands;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the main application window. Manages files and switches between different view templates.
/// </summary>
public class MainWindowViewModel : BaseViewModel
{
    private DataTemplate _listViewTemplate = new();
    private DataTemplate _wrapViewTemplate = new();
    private DataTemplate _currentViewTemplate = new();

    /// <summary>
    /// Command to switch between different views.
    /// </summary>
    public ICommand SwitchViewCommand { get; }

    /// <summary>
    /// The current view template being applied to the UI.
    /// </summary>
    public DataTemplate CurrentViewTemplate
    {
        get => _currentViewTemplate;
        set
        {
            if (_currentViewTemplate == value) return;
            _currentViewTemplate = value;
            OnPropertyChanged(nameof(CurrentViewTemplate));
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">Provider for fetching file icons.</param>
    public MainWindowViewModel(IIconProvider iconProvider)
        : base(iconProvider)
    {
        SwitchViewCommand = new RelayCommand(SwitchView);
        InitializeTemplates();
        CurrentViewTemplate = _listViewTemplate;
    }

    /// <summary>
    /// Initializes the templates for the list and wrap views.
    /// </summary>
    private void InitializeTemplates()
    {
        _listViewTemplate = Application.Current.FindResource("ListViewTemplate") as DataTemplate ?? new DataTemplate();
        _wrapViewTemplate = Application.Current.FindResource("WrapViewTemplate") as DataTemplate ?? new DataTemplate();
    }

    /// <summary>
    /// Toggles the current view template between list and wrap views.
    /// </summary>
    private void SwitchView()
        => CurrentViewTemplate = CurrentViewTemplate == _listViewTemplate ? _wrapViewTemplate : _listViewTemplate;
}
