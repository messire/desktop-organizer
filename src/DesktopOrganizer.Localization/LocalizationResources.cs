using System.Globalization;
using System.Resources;

namespace DesktopOrganizer.Localization;

/// <summary>
/// Culture-explicit access to <c>Strings.resx</c>/<c>Strings.ru.resx</c> (docs/PLANS/localization.md §2.1,
/// L-3). Pure, thread-safe, never throws — every lookup takes <see cref="CultureInfo"/> explicitly, never
/// reading <see cref="CultureInfo.CurrentUICulture"/> itself, so calls are deterministic regardless of the
/// calling thread's culture.
/// </summary>
public static class LocalizationResources
{
    /// <summary>
    /// The underlying <see cref="ResourceManager"/> for the <c>DesktopOrganizer.Localization.Strings</c>
    /// manifest resource (neutral = English, satellite <c>ru</c> = Russian).
    /// </summary>
    public static ResourceManager ResourceManager { get; } =
        new("DesktopOrganizer.Localization.Strings", typeof(LocalizationResources).Assembly);

    /// <summary>
    /// Looks up <paramref name="key"/> for <paramref name="culture"/>. Never throws: a missing key, an
    /// unsupported culture, or a misconfigured/missing satellite assembly all fall back to returning
    /// <paramref name="key"/> itself — a visible, greppable placeholder rather than a crash, and exactly
    /// what the XAML/C# key-coverage gates (G-L2) scan for.
    /// </summary>
    /// <param name="key">The RESX key (docs/PLANS/localization.md §2.6 naming convention).</param>
    /// <param name="culture">The culture to look the string up for.</param>
    /// <returns>The localized string, or <paramref name="key"/> itself when it cannot be found.</returns>
    public static string GetString(string key, CultureInfo culture)
    {
        try
        {
            return ResourceManager.GetString(key, culture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return key;
        }
    }

    /// <summary>
    /// <see cref="GetString"/> followed by <see cref="string.Format(IFormatProvider?, string, object?[])"/>
    /// using <paramref name="culture"/> as the format provider, so numbers/dates embedded in a
    /// <c>_Format</c> key's placeholders always format with the string's own culture rather than the
    /// calling thread's (L-3).
    /// </summary>
    /// <param name="key">The <c>_Format</c> RESX key.</param>
    /// <param name="culture">The culture used both to look up the string and to format <paramref name="args"/>.</param>
    /// <param name="args">Positional format arguments.</param>
    /// <returns>The formatted string.</returns>
    public static string Format(string key, CultureInfo culture, params object?[] args) =>
        string.Format(culture, GetString(key, culture), args);

    /// <summary>
    /// Resolves the plural category for <paramref name="count"/> under <paramref name="culture"/>
    /// (<see cref="PluralRules.Select"/>), looks up <c>$"{baseKey}_{category}"</c>, and formats it with
    /// <paramref name="count"/> as <c>{0}</c> followed by <paramref name="extraArgs"/> as <c>{1}</c>, <c>{2}</c>, ….
    /// </summary>
    /// <param name="baseKey">
    /// The plural base key, e.g. <c>"Plural_Items"</c> — must have all four <see cref="PluralCategory"/>-suffixed
    /// keys defined in both RESX files (L-8).
    /// </param>
    /// <param name="count">The cardinal count driving both category selection and <c>{0}</c>.</param>
    /// <param name="culture">The culture used for category selection, lookup, and formatting.</param>
    /// <param name="extraArgs">Additional format arguments, available as <c>{1}</c>, <c>{2}</c>, ….</param>
    /// <returns>The formatted, pluralized string.</returns>
    public static string FormatPlural(string baseKey, long count, CultureInfo culture, params object?[] extraArgs)
    {
        var category = PluralRules.Select(culture, count);
        var key = $"{baseKey}_{category}";
        var args = new object?[extraArgs.Length + 1];
        args[0] = count;
        extraArgs.CopyTo(args, 1);
        return string.Format(culture, GetString(key, culture), args);
    }
}
