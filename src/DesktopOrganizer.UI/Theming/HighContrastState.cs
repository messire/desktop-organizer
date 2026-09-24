namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Holds the app-wide "is Windows High Contrast active" flag consumed by
/// <c>ChildWindow.xaml</c>'s <c>PanelSurfaceBrush</c> <c>MultiBinding</c> (its 4th, <c>highContrast</c>,
/// value — <see cref="DesktopOrganizer.UI.Converters.PanelSurfaceBrushConverter"/>) and by
/// <c>App.xaml.cs</c>'s <see cref="DesktopOrganizer.UI.App"/>-startup resource-dictionary build
/// (ADR-025 D-12, docs/PLANS/phase-5-ambient-accent-ui.md §3.2/§3.4).
///
/// Deliberately a single static bool, not a live/observable value: D-12 explicitly scopes this fix round
/// to "minimum at startup" — the app reads <see cref="System.Windows.SystemParameters.HighContrast"/>
/// ONCE, at launch, before any window/resource dictionary is built, and this flag never changes for the
/// lifetime of the process. A user who toggles Windows High Contrast while the app is running needs to
/// restart it to pick up the change; live re-theming (SystemParameters.StaticPropertyChanged /
/// SystemEvents.UserPreferenceChanged) is explicitly OPTIONAL per D-12 and out of scope here.
///
/// A plain static property (not a DependencyProperty) is enough because every consumer only ever reads
/// it once, via <c>{x:Static theming:HighContrastState.IsActive}</c> used as a <c>Binding.Source</c>
/// inside a <c>MultiBinding</c> — <c>x:Static</c> is evaluated once, at XAML-parse time, which is exactly
/// this flag's "read once at startup" contract.
/// </summary>
public static class HighContrastState
{
    /// <summary>
    /// Whether Windows High Contrast was active when the app started. Set exactly once, by
    /// <c>App.xaml.cs</c>'s startup resource-building code, before any <c>Window</c> is constructed.
    /// Defaults to <see langword="false"/> so any consumer instantiated before that assignment (there
    /// are none on any reachable path today, but this keeps the type safe-by-default) degrades to the
    /// normal, non-high-contrast rendering rather than throwing or rendering garbage.
    /// </summary>
    public static bool IsActive { get; internal set; }
}
