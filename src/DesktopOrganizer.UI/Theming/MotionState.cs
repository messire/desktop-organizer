namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Holds the app-wide "should UI animations run" flag consumed by <see cref="Behaviors.RollUpBehavior"/>
/// (docs/PLANS/workspace-grid-07-visual.md "Collapsed panel"/"reduced motion") — the single place today
/// that starts a <see cref="System.Windows.Media.Animation.DoubleAnimation"/> on a live desktop panel.
/// </summary>
/// <remarks>
/// <para>
/// Same shape and same rationale as <see cref="HighContrastState"/> (ADR-025 D-12): a plain static bool,
/// read once at startup from <see cref="System.Windows.SystemParameters.ClientAreaAnimation"/> (the
/// Win32 <c>SPI_GETCLIENTAREAANIMATION</c> setting — Windows' "reduced motion" signal, toggled by e.g.
/// Settings &gt; Accessibility &gt; Visual effects &gt; Animation effects), not a live/observable value.
/// A user who toggles this OS setting while the app is running needs to restart it to pick up the
/// change — the exact same "minimum at startup" trade-off <see cref="HighContrastState"/> already makes,
/// for the exact same reason (no consumer here needs anything more elaborate: <c>RollUpBehavior</c>
/// reads this flag once per roll-up/roll-down gesture, not continuously).
/// </para>
/// <para>
/// Defaults to <see langword="true"/> (animations enabled) — the byte-for-byte behavior every panel had
/// before this flag existed — so any consumer constructed before <c>App.xaml.cs</c>'s startup code runs
/// (there are none on any reachable path today) degrades to the original animated behavior rather than
/// silently skipping every roll-up animation.
/// </para>
/// </remarks>
public static class MotionState
{
    /// <summary>
    /// Whether UI animations should run. Set once, at app startup, from
    /// <see cref="System.Windows.SystemParameters.ClientAreaAnimation"/> — see this type's remarks.
    /// </summary>
    public static bool AnimationsEnabled { get; internal set; } = true;
}
