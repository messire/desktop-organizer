namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// xUnit collection for tests that mutate PROCESS-WIDE localization statics —
/// <see cref="DesktopOrganizer.UI.Localization.LocalizationSource"/>'s "already read" guard, thread
/// culture (<see cref="System.Globalization.CultureInfo.CurrentUICulture"/>/
/// <see cref="System.Globalization.CultureInfo.DefaultThreadCurrentUICulture"/>), and
/// <see cref="System.Windows.FrameworkElement.LanguageProperty"/>'s default metadata override
/// (docs/PLANS/localization.md §4.1/§4.8, WP-0U/WP-6). <c>[assembly: CollectionBehavior(DisableTestParallelization
/// = true)]</c> (<c>AssemblyInfo.cs</c>) already serializes every test class in this assembly, so this
/// collection exists to document the intent explicitly (and keep it true even if that assembly-wide
/// setting is ever narrowed), not because parallelization is otherwise a real risk here.
/// </summary>
[CollectionDefinition("GlobalCulture", DisableParallelization = true)]
public class GlobalCultureCollection
{
}
