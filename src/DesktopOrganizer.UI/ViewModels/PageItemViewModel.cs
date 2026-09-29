using System.ComponentModel;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// One row in <see cref="MainWindowViewModel.Pages"/> (ADR-043, docs/PLANS/page-display-names.md §5.2):
/// wraps a single page's <see cref="PageDescriptor"/> plus this window's own inline-rename UI state
/// (<see cref="IsEditing"/>/<see cref="EditingName"/>/<see cref="RenameError"/>). <see cref="Id"/> never
/// changes; <see cref="MainWindowViewModel.SyncPages"/> keeps one instance alive per page id across
/// <c>IPageManager.PagesChanged</c> notifications (rather than rebuilding the whole
/// <see cref="MainWindowViewModel.Pages"/> collection from scratch) so a live selection/edit in progress on
/// one row survives an unrelated page being added/removed/renamed elsewhere.
/// </summary>
public sealed class PageItemViewModel : INotifyPropertyChanged
{
    private const string DefaultPageId = "default";

    private string? _name;
    private bool _isEditing;
    private string _editingName = string.Empty;
    private PageRenameResult? _renameError;

    /// <summary>Initializes a new instance of the <see cref="PageItemViewModel"/> class.</summary>
    /// <param name="id">The page's stable identifier — see <see cref="Id"/>.</param>
    /// <param name="name">The page's initial stored display name — see <see cref="Name"/>.</param>
    public PageItemViewModel(string id, string? name)
    {
        Id = id;
        _name = name;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The page's stable identifier — never changes for the lifetime of this instance.</summary>
    public string Id { get; }

    /// <summary>Whether this is the non-removable <c>"default"</c> page.</summary>
    public bool IsDefault => Id == DefaultPageId;

    /// <summary>
    /// The page's stored display name, exactly as <see cref="DesktopOrganizer.UI.Services.IPageManager.PageDescriptors"/>
    /// reports it — <see langword="null"/> only for an unnamed <c>"default"</c> page. Updated by
    /// <see cref="MainWindowViewModel.SyncPages"/> whenever <c>IPageManager.PagesChanged</c> fires; never
    /// set directly by the view.
    /// </summary>
    public string? Name
    {
        get => _name;
        internal set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>
    /// The value the name-display binding (<c>PageDisplayNameConverter</c>) resolves —
    /// changes (and re-raises <see cref="PropertyChanged"/>) every time <see cref="Name"/> does, so a
    /// <c>{loc:LocBinding Display, Converter=...}</c> binding repaints on rename without needing to bind
    /// the whole item.
    /// </summary>
    public PageDisplayInfo Display => new(Id, _name);

    /// <summary>
    /// Whether this page's name is currently being edited inline (Settings → "Pages"). At most one
    /// <see cref="PageItemViewModel"/> in <see cref="MainWindowViewModel.Pages"/> has this
    /// <see langword="true"/> at a time — <c>MainWindowViewModel.BeginRenamePageCommand</c> commits (or,
    /// failing that, cancels) any other page's in-progress edit first.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnPropertyChanged(nameof(IsEditing));
        }
    }

    /// <summary>
    /// The in-progress edited name, two-way bound to the inline rename <see cref="System.Windows.Controls.TextBox"/>
    /// in <c>PagesSection.xaml</c>.
    /// </summary>
    public string EditingName
    {
        get => _editingName;
        set
        {
            if (_editingName == value) return;
            _editingName = value;
            OnPropertyChanged(nameof(EditingName));
        }
    }

    /// <summary>
    /// Set by <c>MainWindowViewModel.CommitRenamePageCommand</c> when the most recent rename attempt
    /// failed, so the view can show a non-blocking inline hint (never a
    /// <see cref="System.Windows.MessageBox"/> — ADR-013 Decision 6 applies here too, even though this
    /// window carries no <c>WS_EX_NOACTIVATE</c> constraint of its own). Cleared back to
    /// <see langword="null"/> at the start of every new rename attempt and on a successful commit.
    /// </summary>
    public PageRenameResult? RenameError
    {
        get => _renameError;
        internal set
        {
            if (_renameError == value) return;
            _renameError = value;
            OnPropertyChanged(nameof(RenameError));
            OnPropertyChanged(nameof(HasRenameError));
        }
    }

    /// <summary>Whether <see cref="RenameError"/> is currently set — backs the error hint's <c>Visibility</c>.</summary>
    public bool HasRenameError => _renameError is not null;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
