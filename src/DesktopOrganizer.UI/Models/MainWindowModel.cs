using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Represents the data model for the main window.
/// </summary>
public class MainWindowModel : INotifyPropertyChanged
{
    /// <summary>
    /// Collection of child windows managed by the main window.
    /// </summary>
    public ObservableCollection<ChildWindowModel> ChildWindows { get; } = [];

    /// <summary>
    /// Event triggered when a child window is added.
    /// </summary>
    public event EventHandler<ChildWindowModel>? OnChildWindowAdded;

    /// <summary>
    /// Event triggered when a child window is removed.
    /// </summary>
    public event EventHandler<ChildWindowModel>? OnChildWindowRemoved;

    /// <summary>
    /// Event triggered when a child window is modified.
    /// </summary>
    public event EventHandler<ChildWindowModel>? OnChildWindowModified;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Adds a new child window.
    /// </summary>
    /// <param name="window">The window to add.</param>
    public void AddChildWindow(ChildWindowModel window)
    {
        ChildWindows.Add(window);
        OnChildWindowAdded?.Invoke(this, window);
        OnPropertyChanged(nameof(ChildWindows));
    }

    /// <summary>
    /// Removes a child window.
    /// </summary>
    /// <param name="window">The window to remove.</param>
    public void RemoveChildWindow(ChildWindowModel window)
    {
        if (!ChildWindows.Remove(window)) return;

        OnChildWindowRemoved?.Invoke(this, window);
        OnPropertyChanged(nameof(ChildWindows));
    }

    /// <summary>
    /// Notifies that a child window has been modified.
    /// </summary>
    /// <param name="window">The modified window.</param>
    public void NotifyChildWindowModified(ChildWindowModel window)
        => OnChildWindowModified?.Invoke(this, window);

    /// <summary>
    /// Notifies listeners about property value changes.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
