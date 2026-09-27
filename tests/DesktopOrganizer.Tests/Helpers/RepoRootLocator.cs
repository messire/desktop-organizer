using System.IO;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Locates the repository root from a running test assembly's own output directory — shared by every
/// gate that reads SOURCE files directly off disk rather than through a compiled assembly (RESX
/// consistency, XAML literal/key-coverage gates, docs/PLANS/localization.md §6 G-L1/G-L2/G-L3).
/// </summary>
public static class RepoRootLocator
{
    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for a directory containing
    /// <c>DesktopOrganizer.sln</c>. Throws rather than returning <see langword="null"/>: a gate that
    /// silently found nothing to check would be worse than not having it at all.
    /// </summary>
    /// <returns>The repository root's full path.</returns>
    /// <exception cref="InvalidOperationException">No <c>DesktopOrganizer.sln</c> was found walking up from the base directory.</exception>
    public static string Find()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopOrganizer.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (a directory containing DesktopOrganizer.sln) walking up from '{AppContext.BaseDirectory}'.");
    }
}
