using System.Windows.Media;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Small generic visual-tree lookup helpers shared by <see cref="ChildWindow"/> and
/// <see cref="SystemIconsPanelWindow"/> (both host a <see cref="System.Windows.Controls.ListBox"/>
/// inside a swappable List/Grid <c>DataTemplate</c> and need to hit-test into it for drag-to-reorder).
/// </summary>
internal static class VisualTreeWalker
{
    /// <summary>
    /// Walks up the visual tree from <paramref name="obj"/> (inclusive) looking for the nearest
    /// ancestor of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The ancestor type to look for.</typeparam>
    /// <param name="obj">The visual-tree element to start searching from (inclusive).</param>
    public static T? FindAncestorOrSelf<T>(DependencyObject obj)
        where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T t) return t;
            obj = VisualTreeHelper.GetParent(obj);
        }

        return null;
    }

    /// <summary>
    /// Walks the visual tree looking for the first descendant of type <typeparamref name="T"/>
    /// (breadth doesn't matter here — there is exactly one match per <c>DataTemplate</c> instance).
    /// </summary>
    /// <typeparam name="T">The descendant type to look for.</typeparam>
    /// <param name="parent">The visual-tree element to search under.</param>
    public static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            if (FindVisualChild<T>(child) is { } found) return found;
        }

        return null;
    }
}
