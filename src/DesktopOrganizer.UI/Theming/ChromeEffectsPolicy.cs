using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Shader-free fallback policy for ADR-030's <c>PART_Glow</c> (docs/PLANS/reference-panel-chrome.md §1
/// D-9) — <c>PART_Glow</c>'s single <see cref="System.Windows.Media.Effects.DropShadowEffect"/> is only
/// ever rendered on hardware capable of it; everywhere else (software/RDP rendering tier) the template
/// shows <c>PART_GlowFallback</c>, a plain <see cref="System.Windows.Controls.Border"/> ring with no
/// <see cref="System.Windows.UIElement.Effect"/> at all, instead. In Windows High Contrast, the panel
/// template's own SEPARATE <c>DataTrigger</c> on <c>HighContrastState.IsActive</c>
/// (<c>Resources/Styles/Panels.xaml</c>, D-8) collapses BOTH <c>PART_Glow</c> AND <c>PART_GlowFallback</c>
/// outright — this policy's own render-tier decision (see <see cref="IsGlowEffectEnabled"/>) plays no part
/// in that outcome; High Contrast is handled exactly once, at that one other place.
/// </summary>
/// <remarks>
/// Same "read once, cache, allow test override" pattern as <see cref="HighContrastState"/> (D-12) — this
/// class is never re-evaluated after a panel's template is first applied, matching D-12's own "minimum at
/// startup" scope for render-capability-driven decisions in this codebase. <c>App.xaml.cs</c> is
/// deliberately NOT touched by this addition (docs/PLANS/reference-panel-chrome.md §0) — the working tree
/// already carries unrelated, uncommitted edits there this plan must not index/touch; <see cref="IsGlowEffectEnabled"/>
/// is lazily evaluated on first access instead (from the UI thread, when <c>PanelChrome</c>'s template is
/// first applied), not from any startup hook.
/// </remarks>
public static class ChromeEffectsPolicy
{
    /// <summary>
    /// Master kill-switch for the shader-based glow, independent of render tier/High Contrast — flipping
    /// this to <see langword="false"/> disables <c>PART_Glow</c>'s <see cref="System.Windows.Media.Effects.DropShadowEffect"/>
    /// everywhere without touching <c>Resources/Styles/Panels.xaml</c>'s template structure at all (plan
    /// D-9's own "выключатель на уровне кода"). Read through <see cref="ShouldRenderGlowEffect"/>'s own
    /// <c>&amp;&amp;</c> expression, never an <see langword="if"/> — guarding a <see langword="const bool"/>
    /// with <see langword="if"/> makes the other branch provably unreachable, which triggers CS0162
    /// ("unreachable code detected") under this codebase's 0-warnings rule.
    /// </summary>
    internal const bool GlowEffectAllowed = true;

    private static bool? _isGlowEffectEnabledOverride;
    private static bool? _cachedIsGlowEffectEnabled;

    /// <summary>
    /// Pure decision function: should <c>PART_Glow</c>'s shader-based
    /// <see cref="System.Windows.Media.Effects.DropShadowEffect"/> render at all? <see langword="false"/>
    /// whenever <paramref name="highContrast"/> is <see langword="true"/> (decorative glow has no place in
    /// High Contrast, plan D-8) or <paramref name="renderTier"/> is below 2 — tier 0/1 means WPF is
    /// rendering entirely (tier 0) or partially (tier 1) in software, the common case for a remote desktop
    /// session or a VM with no GPU passthrough, where a per-frame pixel-shader effect is disproportionately
    /// expensive (plan D-9).
    /// </summary>
    /// <param name="renderTier"><c>RenderCapability.Tier &gt;&gt; 16</c> — the caller reads the real system value; this function never touches <see cref="RenderCapability"/> itself, so it stays fully unit-testable.</param>
    /// <param name="highContrast">A caller-supplied High Contrast flag — see <see cref="IsGlowEffectEnabled"/>'s own remarks for why production code never actually passes <see langword="true"/> here.</param>
    internal static bool ShouldRenderGlowEffect(int renderTier, bool highContrast)
        => GlowEffectAllowed && renderTier >= 2 && !highContrast;

    /// <summary>
    /// Whether <c>PART_Glow</c>'s shader-based effect should render on THIS machine, right now — lazily
    /// computed once (from <see cref="RenderCapability.Tier"/> alone) and cached for the remainder of the
    /// process, unless <see cref="OverrideIsGlowEffectEnabled"/> is set (test-only). Bound from
    /// <c>Resources/Styles/Panels.xaml</c> via
    /// <c>{Binding Path=(theming:ChromeEffectsPolicy.IsGlowEffectEnabled), Mode=OneTime}</c> — a
    /// <see cref="System.Windows.Data.Binding"/> with <c>Mode=OneTime</c>, which (unlike
    /// <c>{x:Static ...}</c> used as a plain <c>Binding.Source</c>) defers evaluation from XAML-parse time
    /// to whenever the binding is actually attached — here, whenever a panel's <c>ControlTemplate</c> is
    /// first applied — letting a test flip <see cref="OverrideIsGlowEffectEnabled"/> before constructing a
    /// <see cref="Controls.PanelChrome"/> without reloading any resource dictionary.
    /// <para>
    /// This cache deliberately does <b>not</b> fold <see cref="HighContrastState.IsActive"/> into the cached
    /// value — the process-wide tier decision would otherwise permanently bake in whatever HC flag happened
    /// to be set the FIRST time this property was ever touched (a real problem in a test host, where
    /// <see cref="HighContrastState.IsActive"/> is flipped on and off across many test classes sharing this
    /// one static cache). High Contrast is handled at exactly ONE place instead — the panel template's own
    /// <c>DataTrigger</c> on <c>{Binding Path=(theming:HighContrastState.IsActive), Mode=OneTime}</c>
    /// (<c>Resources/Styles/Panels.xaml</c>, D-8), which collapses <c>PART_Glow</c>/<c>PART_GlowFallback</c>
    /// unconditionally whenever HC is active, regardless of what this property returns. This property is
    /// therefore a pure render-TIER decision.
    /// </para>
    /// </summary>
    public static bool IsGlowEffectEnabled
    {
        get
        {
            if (_isGlowEffectEnabledOverride.HasValue)
                return _isGlowEffectEnabledOverride.Value;

            _cachedIsGlowEffectEnabled ??= ShouldRenderGlowEffect(RenderCapability.Tier >> 16, highContrast: false);
            return _cachedIsGlowEffectEnabled.Value;
        }
    }

    /// <summary>
    /// Test-only override for <see cref="IsGlowEffectEnabled"/> — set to a non-<see langword="null"/>
    /// value to force a deterministic result without depending on the real (test-host) <see cref="RenderCapability.Tier"/>;
    /// set back to <see langword="null"/> in a <see langword="finally"/> block to restore the lazy,
    /// cached, real computation. Never touched by production code, mirroring <see cref="HighContrastState.IsActive"/>'s
    /// own <see langword="internal set"/> test-seam convention.
    /// </summary>
    internal static bool? OverrideIsGlowEffectEnabled
    {
        get => _isGlowEffectEnabledOverride;
        set => _isGlowEffectEnabledOverride = value;
    }
}
