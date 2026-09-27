using System.Globalization;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Event arguments for <see cref="ILocalizationService.CultureChanged"/> (docs/PLANS/localization.md §2.2).
/// </summary>
/// <param name="oldCulture">The effective UI culture before the change.</param>
/// <param name="newCulture">The effective UI culture after the change.</param>
public sealed class CultureChangedEventArgs(CultureInfo oldCulture, CultureInfo newCulture) : EventArgs
{
    /// <summary>The effective UI culture before the change.</summary>
    public CultureInfo OldCulture { get; } = oldCulture;

    /// <summary>The effective UI culture after the change.</summary>
    public CultureInfo NewCulture { get; } = newCulture;
}
