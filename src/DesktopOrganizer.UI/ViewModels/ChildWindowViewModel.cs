using DesktopOrganizer.UI.Commands;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Child window ViewModel.
/// </summary>
public class ChildWindowViewModel : BaseViewModel
{
    private const string ListViewTemplate = nameof(ListViewTemplate);
    private const string WrapViewTemplate = nameof(WrapViewTemplate);

    private readonly IReadOnlyDictionary<DataTemplate, DataTemplate> _templates;
    private DataTemplate _currentViewTemplate = new();

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
    /// Command to switch between different views.
    /// </summary>
    protected ICommand SwitchViewCommand { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">Provider for fetching file icons.</param>
    public ChildWindowViewModel(IIconProvider iconProvider)
        : base(iconProvider)
    {
        _templates = GetTemplates();
        SwitchViewCommand = new RelayCommand(SwitchView);
        CurrentViewTemplate = _templates.Values.First();
    }

    private static IReadOnlyDictionary<DataTemplate, DataTemplate> GetTemplates()
    {
        var listViewTemplate = GetTemplate(ListViewTemplate);
        var wrapViewTemplate = GetTemplate(WrapViewTemplate);
        return new Dictionary<DataTemplate, DataTemplate>
        {
            { listViewTemplate, wrapViewTemplate },
            { wrapViewTemplate, listViewTemplate },
        };
    }

    private static DataTemplate GetTemplate(string templateName)
        => Application.Current.FindResource(templateName) as DataTemplate ?? new DataTemplate();

    /// <summary>
    /// Toggles the current view template between list and wrap views.
    /// </summary>
    private void SwitchView()
        => CurrentViewTemplate = _templates[CurrentViewTemplate];
}
