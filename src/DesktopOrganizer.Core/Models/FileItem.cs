using System.ComponentModel;
using System.Windows.Media;

namespace DesktopOrganizer.Core.Models;

/// <summary>
/// File data content.
/// </summary>
/// <remarks>
/// <see cref="Exists"/> and <see cref="IsDirectory"/> are cached at construction time and only
/// re-evaluated by an explicit call to <see cref="Refresh"/>. This used to be a plain computed
/// property re-checking the file system on every access, but WPF only re-pulls a binding when the
/// bound object raises <see cref="PropertyChanged"/> (or the container is regenerated) — a value
/// that silently changes underneath a already-rendered item (e.g. the user deletes it via Explorer
/// while the panel is open) never got reflected in the UI. Callers that want live liveness tracking
/// must call <see cref="Refresh"/> periodically (see <c>ChildWindowViewModel</c>).
/// </remarks>
public sealed class FileItem : INotifyPropertyChanged
{
    private bool _exists;
    private bool _isDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileItem"/> class, evaluating
    /// <see cref="Exists"/> and <see cref="IsDirectory"/> once against the current file system state.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="icon">The file icon.</param>
    public FileItem(string path, ImageSource? icon)
    {
        Path = path;
        Icon = icon;
        _exists = ComputeExists(path);
        _isDirectory = System.IO.Directory.Exists(path);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The file path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The file icon.
    /// </summary>
    public ImageSource? Icon { get; }

    /// <summary>
    /// The file name extracted from the file path.
    /// </summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// The file extension extracted from the file path.
    /// </summary>
    public string Type => System.IO.Path.GetExtension(Path);

    /// <summary>
    /// Whether the underlying file or folder existed on disk as of the last <see cref="Refresh"/>
    /// (or construction). Used by the UI to grey out broken entries. Call <see cref="Refresh"/>
    /// to pick up changes made outside this item's own add/remove flow (e.g. deleted via Explorer).
    /// </summary>
    public bool Exists
    {
        get => _exists;
        private set
        {
            if (_exists == value) return;
            _exists = value;
            OnPropertyChanged(nameof(Exists));
        }
    }

    /// <summary>
    /// True if the path pointed to a directory as of the last <see cref="Refresh"/> (or construction).
    /// False for missing or broken paths.
    /// </summary>
    public bool IsDirectory
    {
        get => _isDirectory;
        private set
        {
            if (_isDirectory == value) return;
            _isDirectory = value;
            OnPropertyChanged(nameof(IsDirectory));
        }
    }

    /// <summary>
    /// True if the path has a <c>.lnk</c> extension (case-insensitive). Does not resolve the shortcut target.
    /// Purely string-based (no file-system access), so unlike <see cref="Exists"/>/<see cref="IsDirectory"/>
    /// it needs no refresh.
    /// </summary>
    public bool IsShortcut =>
        string.Equals(System.IO.Path.GetExtension(Path), ".lnk", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-evaluates <see cref="Exists"/> and <see cref="IsDirectory"/> against the current file-system
    /// state, raising <see cref="PropertyChanged"/> only for values that actually flipped. Cheap
    /// (two file-system checks); safe to call periodically from a polling timer.
    /// </summary>
    public void Refresh()
    {
        Exists = ComputeExists(Path);
        IsDirectory = System.IO.Directory.Exists(Path);
    }

    /// <summary>
    /// The file name as a string representation of the object.
    /// </summary>
    public override string ToString() => Name;

    private static bool ComputeExists(string path) =>
        System.IO.File.Exists(path) || System.IO.Directory.Exists(path);

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
