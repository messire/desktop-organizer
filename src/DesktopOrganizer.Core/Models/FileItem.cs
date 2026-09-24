using System.ComponentModel;
using System.Windows.Media;

namespace DesktopOrganizer.Core;

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
    private string? _displayName;
    private bool _exists;
    private bool _isDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileItem"/> class, evaluating
    /// <see cref="Exists"/> and <see cref="IsDirectory"/> once against the current file system state.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="icon">The file icon.</param>
    /// <param name="displayName">
    /// Optional cosmetic override for <see cref="DisplayName"/>, never persisted. Used only to strip
    /// a legacy managed-storage GUID suffix from an item's on-disk name for display purposes (see
    /// <see cref="ManagedItemName.StripLegacyGuidSuffix"/> and ADR-013 Decision 3) — the on-disk
    /// <see cref="Path"/>/<see cref="Name"/> themselves are unaffected.
    /// </param>
    public FileItem(string path, ImageSource? icon, string? displayName = null)
    {
        Path = path;
        Icon = icon;
        _displayName = displayName;
        _exists = ComputeExists(path);
        _isDirectory = System.IO.Directory.Exists(path);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The file path.
    /// </summary>
    public string Path { get; private set; }

    /// <summary>
    /// The file icon.
    /// </summary>
    public ImageSource? Icon { get; private set; }

    /// <summary>
    /// The file name extracted from the file path.
    /// </summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// The name to show in the UI: <see cref="Name"/>, unless a cosmetic <c>displayName</c> override
    /// was supplied at construction (see the constructor's <c>displayName</c> parameter). Never
    /// persisted — purely a rendering concern, recomputed fresh from <see cref="Path"/> whenever no
    /// override is set. For a shortcut (<see cref="IsShortcut"/>) the trailing <c>.lnk</c> is also
    /// stripped, matching Windows Explorer's default "hide extensions" behavior — <see cref="Name"/>/
    /// <see cref="Path"/>/<see cref="Type"/> themselves are unaffected and still reflect the real
    /// on-disk name with its extension.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var name = _displayName ?? Name;
            return IsShortcut ? StripShortcutExtension(name) : name;
        }
    }

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
    public bool IsShortcut => string.Equals(System.IO.Path.GetExtension(Path), ".lnk", StringComparison.OrdinalIgnoreCase);

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
    /// Updates this item in place after a physical rename/move (see
    /// <c>IFileItemRenameService.Rename</c> and <c>BaseViewModel.TryRenameItem</c>), instead of
    /// discarding and recreating the <see cref="FileItem"/> — the same instance stays bound to
    /// whatever UI container (and selection) already references it. Raises <see cref="PropertyChanged"/>
    /// for every value that changed as a result: <see cref="Path"/>, <see cref="Icon"/>,
    /// <see cref="Name"/>, <see cref="DisplayName"/>, <see cref="Type"/> and <see cref="IsShortcut"/>
    /// all derive from <see cref="Path"/>, so they all change together. Also re-evaluates
    /// <see cref="Exists"/>/<see cref="IsDirectory"/> against the new path and clears any cosmetic
    /// <c>displayName</c> override passed at construction — after a rename the on-disk name <i>is</i>
    /// the name the user chose, so <see cref="DisplayName"/> should reflect it directly rather than
    /// keep showing a stale legacy-suffix-stripped value computed from the old path.
    /// </summary>
    /// <param name="newPath">The item's new full path, as returned by the rename service.</param>
    /// <param name="icon">A freshly resolved icon for <paramref name="newPath"/>.</param>
    public void Relocate(string newPath, ImageSource? icon)
    {
        Path = newPath;
        Icon = icon;
        _displayName = null;

        Exists = ComputeExists(newPath);
        IsDirectory = System.IO.Directory.Exists(newPath);

        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(IsShortcut));
    }

    /// <summary>
    /// Re-resolves <see cref="Icon"/> in place, without touching <see cref="Path"/>/<see cref="Name"/>/
    /// <see cref="DisplayName"/>/<see cref="Type"/>/<see cref="IsShortcut"/> the way <see cref="Relocate"/>
    /// does. Used by <c>BaseViewModel.RefreshItemsLiveness</c> when a previously-missing item's file
    /// reappears (P00 baseline finding H3): <c>IIconProvider</c> deliberately never caches its
    /// placeholder result for a missing path, so re-resolving here is enough to replace it with the
    /// file's real icon instead of leaving the item permanently stuck on the placeholder even after
    /// <see cref="Refresh"/> flips <see cref="Exists"/> back to <see langword="true"/>.
    /// </summary>
    /// <param name="icon">The freshly resolved icon, or <see langword="null"/> if none could be resolved.</param>
    /// <returns>
    /// <see langword="true"/> if <see cref="Icon"/> actually changed (and <see cref="PropertyChanged"/>
    /// was raised for it); <see langword="false"/> for a no-op re-resolution to the same instance.
    /// </returns>
    public bool UpdateIcon(ImageSource? icon)
    {
        if (ReferenceEquals(Icon, icon)) return false;

        Icon = icon;
        OnPropertyChanged(nameof(Icon));
        return true;
    }

    /// <summary>
    /// The file name as a string representation of the object.
    /// </summary>
    public override string ToString() => Name;

    private static bool ComputeExists(string path) => System.IO.File.Exists(path) || System.IO.Directory.Exists(path);

    /// <summary>
    /// Strips a trailing <c>.lnk</c> extension (case-insensitive) from <paramref name="name"/>, used
    /// by <see cref="DisplayName"/> for shortcuts. Only ever called with <see cref="IsShortcut"/>
    /// already <see langword="true"/>, but re-checks the suffix itself rather than trusting the caller
    /// blindly, so a cosmetic <c>displayName</c> override that doesn't happen to end in <c>.lnk</c>
    /// (shouldn't normally occur, but isn't guaranteed by the constructor's contract) is left untouched
    /// instead of truncated.
    /// </summary>
    private static string StripShortcutExtension(string name) => name.EndsWith(".lnk", System.StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
