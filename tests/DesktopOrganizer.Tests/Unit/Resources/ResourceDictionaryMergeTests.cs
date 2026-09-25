using System.Collections;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Resources;

/// <summary>
/// Regression coverage for the CRITICAL-1 class of bug found in Ambient Accent UI review round 2: an
/// <c>internal</c> member referenced from XAML via <c>{x:Static ...}</c> — or any other resource that
/// fails to resolve (a missing/renamed <c>StaticResource</c>/<c>DynamicResource</c> key, a broken merge
/// order) — throws <see cref="System.Windows.Markup.XamlParseException"/> the moment the referencing
/// <c>Style</c>/<c>ControlTemplate</c>/<c>DataTemplate</c> is actually materialized. None of the other
/// ~1748 unit tests caught it: none of them build the real <see cref="Application.Resources"/> merge
/// chain <c>App.xaml.cs</c>'s <c>LoadResourceDictionaries()</c> builds and then force every deferred
/// resource to materialize. This test does exactly that, for both the normal and Windows-High-Contrast
/// merge orders, so this whole bug class is caught by every <c>dotnet test</c> run instead of only by a
/// one-off manual reviewer probe.
/// </summary>
/// <remarks>
/// Runs on a dedicated STA thread (there is no existing STA test helper in this project to reuse —
/// checked first, per the usual convention here, but none of the other WPF-touching fixtures, e.g.
/// <c>ChildWindowViewModelTests.EnsureWpfApplication</c>, needed one because they never construct a
/// <see cref="System.Windows.Controls.ContextMenu"/> or otherwise touch <c>InputManager</c>). Simply
/// merging/parsing dictionaries doesn't require it, but this test also has to force every deferred
/// resource to materialize — including <c>Panels.xaml</c>'s <c>FileContextMenu</c> — and constructing a
/// <see cref="System.Windows.Controls.ContextMenu"/> (a <see cref="System.Windows.FrameworkElement"/>)
/// requires <see cref="System.Windows.Input.InputManager"/> on the CURRENT thread, which throws
/// <see cref="System.InvalidOperationException"/> ("The calling thread must be STA") off the ordinary
/// (MTA) xUnit test thread.
///
/// <see cref="Application"/> only allows a single instance per process, and
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> (AssemblyInfo.cs) means test
/// CLASSES never run concurrently within one <c>dotnet test</c> process — but their order is not
/// guaranteed, so <see cref="Application.Current"/> may already exist by the time this runs (several
/// other fixtures create one and never tear it down — see <c>ApplicationStateServiceTests
/// .RequestSave_InHeadlessTestHost_DegradesToImmediateSave</c>'s own remarks for the identical ambient-
/// state caveat). This test therefore reuses whatever <c>Application.Current</c> it finds rather than
/// assuming it owns the process, swaps in a throwaway <see cref="ResourceDictionary"/> for its own two
/// (normal / high-contrast) merge attempts, and in a <c>finally</c> restores whatever <c>Resources</c>
/// instance was there before if it found an existing <c>Application.Current</c>. If it constructed the
/// <c>Application</c> itself instead, it deliberately leaves it (and the last, high-contrast, merge)
/// alive rather than tearing it down — see <see cref="RunOnStaThread{T}"/>'s remarks for why.
/// <see cref="HighContrastState.IsActive"/> is likewise restored, and this class shares
/// <see cref="HighContrastStateTests"/>' collection so the two never race on that same static.
/// <para>
/// M-2 fix (P08 fix cycle 2): "did THIS call construct <c>Application.Current</c>" is decided by
/// reference-comparing <c>WpfApplicationHost.EnsureApplication()</c>'s return value against whatever
/// <c>Application.Current</c> was immediately beforehand, not by a separate "was it null" probe taken a
/// moment earlier — the two can never disagree with each other by construction. Delegating construction
/// itself to <c>WpfApplicationHost.EnsureApplication()</c> also means the (immediately-discarded, since
/// this test always overwrites <c>Resources</c> right after) initial merge — if this call happens to be
/// the one that constructs <c>Application.Current</c> — uses the SAME merge chain <c>App.xaml.cs</c>
/// builds in production, rather than a second, hand-rolled copy of it.
/// </para>
/// </remarks>
[Collection("HighContrastState")]
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResourceDictionaryMergeTests
{
    [Fact]
    public void LoadResourceDictionaries_NormalAndHighContrastModes_MaterializeEveryResourceWithoutThrowing()
    {
        var failures = RunOnStaThread(() =>
        {
            var results = new List<(bool HighContrast, Exception? Exception)>();
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                // Order is [true, false] (cheap fix, review round 3): the last iteration's merged
                // dictionaries are what survive in the shared Application.Resources for the rest of the
                // `dotnet test` process (this method only restores HighContrastState.IsActive/app.Resources
                // in `finally` below when it doesn't own the Application — see this class's own remarks —
                // it never re-merges a "normal" set afterward). Running high-contrast LAST used to leave
                // Colors.HighContrast.xaml's overrides merged into Application.Resources with
                // HighContrastState.IsActive correctly reset back to false — a resources/flag desync for
                // every test that runs afterward and happens to touch a themed resource. Normal-last
                // avoids that mismatch.
                foreach (var highContrast in new[] { true, false })
                {
                    app.Resources = new ResourceDictionary();
                    MergeResourceDictionaries(app, highContrast);

                    var exception = Record.Exception(() =>
                    {
                        TouchAllResourceKeys(app.Resources);
                        LoadTemplateContent(app, "ListViewTemplate");
                        LoadTemplateContent(app, "GridViewTemplate");
                        MaterializePanelChromeTemplate(app);
                    });

                    results.Add((highContrast, exception));
                }
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;

                // If ownsApplication, deliberately leave Application.Current/Resources as-is (the last
                // — high-contrast — merge) rather than tearing it down: see RunOnStaThread's remarks for
                // why this thread keeps pumping afterward instead of exiting.
            }

            return results;
        });

        foreach (var (highContrast, exception) in failures)
        {
            Assert.True(
                exception is null,
                $"Resource dictionary merge chain (HighContrast={highContrast}) threw while materializing resources: {exception}");
        }
    }

    /// <summary>
    /// T-R4 (docs/PLANS/reference-panel-chrome.md §5.2) — FINAL replacement (commit C4) for both
    /// <c>PanelChromeTemplate_AccentIndicator_SpansTheHeaderRowOnly</c> (fix-cycle round 1 CRITICAL,
    /// fixed in d16aab6) and <c>PanelChromeTemplate_AccentIndicatorColumn_WidthCoversMaxCombinedAccentEmphasis</c>
    /// — retired for good reason: D-4 replaced the fixed accent-indicator Grid COLUMN with a curved
    /// <see cref="System.Windows.Shapes.Path"/> in a fixed-size overlay host
    /// (<c>PART_AccentIndicatorHost</c>), so there is no column width left to measure. This test instead
    /// pins D-7's real "no layout jitter" contract for that host: explicit, finite, non-<see cref="double.NaN"/>
    /// Width/Height, at least as large as the REAL <see cref="AccentMarkerGeometryConverter"/> output's own
    /// <see cref="Geometry.Bounds"/> (for the REAL <c>Radius.Panel</c> resource) inflated by half the
    /// marker's own real maximum combined <see cref="System.Windows.Shapes.Shape.StrokeThickness"/> (read
    /// from <see cref="InteractionVisualState"/>/<see cref="LayoutEditVisuals"/>/<see cref="PanelMetrics"/>
    /// directly, never a hand-copied literal) — and that the host's own inflated vertical extent never
    /// exceeds <see cref="PanelMetrics.HeaderInnerHeight"/> (plan D-4 collision requirement (б): the
    /// marker's round end-cap must never overflow into the content row).
    /// </summary>
    [Fact]
    public void PanelChromeTemplate_AccentIndicatorHost_HasFiniteBoundsThatCoverTheRealMarkerGeometry_AndNeverExceedsHeaderInnerHeight()
    {
        var (hostWidth, hostHeight, geometryBounds) = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var style = (Style)app.Resources[typeof(PanelChrome)];
                var chrome = new PanelChrome { Style = style };
                chrome.ApplyTemplate();

                var host = (FrameworkElement)chrome.Template.FindName(PanelChrome.PartAccentIndicatorHost, chrome)!;
                var accentIndicator = (System.Windows.Shapes.Path)chrome.Template.FindName("PART_AccentIndicator", chrome)!;

                // Real converter output, for the REAL Radius.Panel resource — not a hand-copied radius.
                var radiusPanel = (CornerRadius)app.Resources["Radius.Panel"];
                var geometryConverter = (AccentMarkerGeometryConverter)app.Resources["AccentMarkerGeometry"];
                var geometry = (Geometry)geometryConverter.Convert(radiusPanel, typeof(Geometry), null, System.Globalization.CultureInfo.InvariantCulture);

                Assert.Same(host, VisualTreeHelper.GetParent(accentIndicator));
                var surfaceLayer = (FrameworkElement)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
                Assert.Same(surfaceLayer, VisualTreeHelper.GetParent(host));

                return (host.Width, host.Height, geometry.Bounds);
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        Assert.False(double.IsNaN(hostWidth), "PART_AccentIndicatorHost.Width must be an explicit, finite value (not Auto/NaN).");
        Assert.False(double.IsNaN(hostHeight), "PART_AccentIndicatorHost.Height must be an explicit, finite value (not Auto/NaN).");
        Assert.False(double.IsInfinity(hostWidth));
        Assert.False(double.IsInfinity(hostHeight));

        var maxCombinedStroke = PanelMetrics.AccentMarkerStrokeThickness
            * InteractionVisualState.AccentEmphasisFor(PanelVisualState.Resizing)
            * LayoutEditVisuals.AccentEmphasis;
        var halfMaxStroke = maxCombinedStroke / 2d;

        var inflatedRight = geometryBounds.Right + halfMaxStroke;
        var inflatedBottom = geometryBounds.Bottom + halfMaxStroke;

        Assert.True(
            hostWidth >= inflatedRight,
            $"PART_AccentIndicatorHost.Width ({hostWidth}) must be >= the real marker geometry's own bounds inflated by half the max combined stroke ({inflatedRight}).");
        Assert.True(
            hostHeight >= inflatedBottom,
            $"PART_AccentIndicatorHost.Height ({hostHeight}) must be >= the real marker geometry's own bounds inflated by half the max combined stroke ({inflatedBottom}).");

        Assert.True(
            inflatedBottom <= PanelMetrics.HeaderInnerHeight + 0.001,
            $"The marker's own inflated bottom edge ({inflatedBottom}) must not exceed HeaderInnerHeight ({PanelMetrics.HeaderInnerHeight}) — plan D-4 collision requirement (б).");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.2,
    // T-R1/T-R2/T-R3/T-R5) — real-template regression pins for the layered chrome commit (C3).
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>T-R1 (root layer order): PART_HitCatcher renders before (i.e. UNDER, lower z-order) PART_SurfaceLayer in the template root Grid.</summary>
    [Fact]
    public void PanelChromeTemplate_RootGrid_ChildrenOrder_HitCatcherBeforeSurfaceLayer()
    {
        var (hitCatcherIndex, surfaceLayerIndex) = WithRealChrome(
            highContrast: false,
            width: 400,
            height: 300,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                var hitCatcher = (FrameworkElement)chrome.Template.FindName("PART_HitCatcher", chrome)!;
                var surfaceLayer = (FrameworkElement)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
                var root = (Grid)VisualTreeHelper.GetParent(hitCatcher);

                Assert.Same(root, VisualTreeHelper.GetParent(surfaceLayer));
                return (root.Children.IndexOf(hitCatcher), root.Children.IndexOf(surfaceLayer));
            });

        Assert.True(hitCatcherIndex < surfaceLayerIndex, "PART_HitCatcher must render before (under) PART_SurfaceLayer.");
    }

    /// <summary>T-R1 (PART_SurfaceLayer's own child order): matches §3's diagram — PART_Chrome, PART_NeutralStroke, PART_AccentStroke, PART_AccentIndicator, PART_InteractionOverlay, bottom to top.</summary>
    [Fact]
    public void PanelChromeTemplate_SurfaceLayerChildrenOrder_MatchesPlanDiagram()
    {
        var order = WithRealChrome(
            highContrast: false,
            width: 400,
            height: 300,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                var surfaceLayer = (Grid)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
                return surfaceLayer.Children.OfType<FrameworkElement>().Select(e => e.Name).ToArray();
            });

        Assert.Equal(
            new[] { "PART_Chrome", "PART_NeutralStroke", "PART_AccentStroke", PanelChrome.PartAccentIndicatorHost, "PART_InteractionOverlay" },
            order);
    }

    /// <summary>T-R2: every decorative layer added/relocated by C3 is IsHitTestVisible=false.</summary>
    [Fact]
    public void PanelChromeTemplate_DecorativeParts_AreNotHitTestVisible()
    {
        var flags = WithRealChrome(
            highContrast: false,
            width: 400,
            height: 300,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                bool IsHitTestVisible(string name) => ((UIElement)chrome.Template.FindName(name, chrome)!).IsHitTestVisible;

                return new Dictionary<string, bool>
                {
                    ["PART_HitCatcher"] = IsHitTestVisible("PART_HitCatcher"),
                    ["PART_Glow"] = IsHitTestVisible("PART_Glow"),
                    ["PART_GlowFallback"] = IsHitTestVisible("PART_GlowFallback"),
                    ["PART_SurfaceHighlight"] = IsHitTestVisible("PART_SurfaceHighlight"),
                    ["PART_NeutralStroke"] = IsHitTestVisible("PART_NeutralStroke"),
                    ["PART_AccentStroke"] = IsHitTestVisible("PART_AccentStroke"),
                    ["PART_AccentIndicator"] = IsHitTestVisible("PART_AccentIndicator"),
                    ["PART_InteractionOverlay"] = IsHitTestVisible("PART_InteractionOverlay"),
                };
            });

        foreach (var (name, isHitTestVisible) in flags)
            Assert.False(isHitTestVisible, $"{name} must be IsHitTestVisible=false.");
    }

    /// <summary>
    /// M-1 regression pin: every hairline-stroked decorative <see cref="Border"/> the layered chrome draws
    /// directly over <c>PART_Chrome</c>'s own surface — <c>PART_Chrome</c> itself, <c>PART_NeutralStroke</c>,
    /// <c>PART_AccentStroke</c>, <c>PART_InteractionOverlay</c>, and <c>PART_GlowFallback</c> — has
    /// <see cref="UIElement.SnapsToDevicePixels"/> = <see langword="true"/> in the real template. A
    /// 1-DIP <see cref="Border.BorderThickness"/> that straddles a device-pixel boundary at a non-integer
    /// DPI scale renders as a blurred ~1.5px smear instead of a crisp hairline; <see cref="FrameworkElement.SnapsToDevicePixels"/>
    /// is what keeps every one of these concentric rings crisp against every other (plan §3's own "crisp
    /// hairline rings" requirement).
    /// </summary>
    [Fact]
    public void PanelChromeTemplate_HairlineStrokedParts_SnapToDevicePixels()
    {
        var flags = WithRealChrome(
            highContrast: false,
            width: 400,
            height: 300,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                bool SnapsToDevicePixels(string name) => ((UIElement)chrome.Template.FindName(name, chrome)!).SnapsToDevicePixels;

                return new Dictionary<string, bool>
                {
                    ["PART_Chrome"] = SnapsToDevicePixels("PART_Chrome"),
                    [PanelChrome.PartNeutralStroke] = SnapsToDevicePixels(PanelChrome.PartNeutralStroke),
                    [PanelChrome.PartAccentStroke] = SnapsToDevicePixels(PanelChrome.PartAccentStroke),
                    [PanelChrome.PartInteractionOverlay] = SnapsToDevicePixels(PanelChrome.PartInteractionOverlay),
                    [PanelChrome.PartGlowFallback] = SnapsToDevicePixels(PanelChrome.PartGlowFallback),
                };
            });

        foreach (var (name, snapsToDevicePixels) in flags)
            Assert.True(snapsToDevicePixels, $"{name} must have SnapsToDevicePixels=true.");
    }

    /// <summary>T-R3 (no layout jitter): for all 5 states x {editing false/true} x {Ambient, Glass}, PART_SurfaceLayer/PART_Header/PART_ContentHost/PART_AccentIndicatorHost's own TranslatePoint(0,0)/ActualWidth/ActualHeight, and PanelChrome's own DesiredSize, stay byte-for-byte equal to the Idle/false/Ambient baseline.</summary>
    [Fact]
    public void PanelChromeTemplate_NoLayoutJitter_AcrossStatesEditingAndSurfaceStyle()
    {
        PartSnapshot baselineSurfaceLayer = default;
        PartSnapshot baselineHeader = default;
        PartSnapshot baselineContent = default;
        PartSnapshot baselineMarkerHost = default;
        var baselineDesired = default(Size);
        var first = true;

        foreach (PanelVisualState state in Enum.GetValues<PanelVisualState>())
        {
            foreach (var isLayoutEditing in new[] { false, true })
            {
                foreach (var surfaceStyle in new[] { PanelSurfaceStyle.Ambient, PanelSurfaceStyle.Glass })
                {
                    var (surfaceLayer, header, content, markerHost, desired) = WithRealChrome(
                        highContrast: false,
                        width: 400,
                        height: 300,
                        state: state,
                        isLayoutEditing: isLayoutEditing,
                        surfaceStyle: surfaceStyle,
                        body: chrome => (
                            SnapshotPart(chrome, "PART_SurfaceLayer"),
                            SnapshotPart(chrome, "PART_Header"),
                            SnapshotPart(chrome, "PART_ContentHost"),
                            SnapshotPart(chrome, PanelChrome.PartAccentIndicatorHost),
                            chrome.DesiredSize));

                    if (first)
                    {
                        baselineSurfaceLayer = surfaceLayer;
                        baselineHeader = header;
                        baselineContent = content;
                        baselineMarkerHost = markerHost;
                        baselineDesired = desired;
                        first = false;
                        continue;
                    }

                    var combo = $"state={state}, isLayoutEditing={isLayoutEditing}, surfaceStyle={surfaceStyle}";
                    Assert.True(baselineSurfaceLayer.Equals(surfaceLayer), $"PART_SurfaceLayer jitter at {combo}: baseline={baselineSurfaceLayer}, actual={surfaceLayer}");
                    Assert.True(baselineHeader.Equals(header), $"PART_Header jitter at {combo}: baseline={baselineHeader}, actual={header}");
                    Assert.True(baselineContent.Equals(content), $"PART_ContentHost jitter at {combo}: baseline={baselineContent}, actual={content}");
                    Assert.True(baselineMarkerHost.Equals(markerHost), $"{PanelChrome.PartAccentIndicatorHost} jitter at {combo}: baseline={baselineMarkerHost}, actual={markerHost}");
                    Assert.True(baselineDesired.Equals(desired), $"PanelChrome.DesiredSize jitter at {combo}: baseline={baselineDesired}, actual={desired}");
                }
            }
        }
    }

    /// <summary>T-R5 (content geometry): content width/top-offset, PART_SurfaceLayer's own rect, and PART_HitCatcher's own rect, all in PanelChrome-local coordinates.</summary>
    [Theory]
    [InlineData(200d)]
    [InlineData(344d)]
    [InlineData(400d)]
    public void PanelChromeTemplate_ContentGeometry_MatchesInsetAndFileListHPadding(double width)
    {
        const double height = 300d;

        var (contentWidth, contentTopY, surfaceLayerRect, hitCatcherRect) = WithRealChrome(
            highContrast: false,
            width: width,
            height: height,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                var contentHost = (FrameworkElement)chrome.Template.FindName("PART_ContentHost", chrome)!;
                var surfaceLayer = (FrameworkElement)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
                var hitCatcher = (FrameworkElement)chrome.Template.FindName("PART_HitCatcher", chrome)!;

                var contentTopLeft = contentHost.TranslatePoint(new Point(0, 0), chrome);
                var surfaceLayerTopLeft = surfaceLayer.TranslatePoint(new Point(0, 0), chrome);
                var hitCatcherTopLeft = hitCatcher.TranslatePoint(new Point(0, 0), chrome);

                return (
                    contentHost.ActualWidth,
                    contentTopLeft.Y,
                    new Rect(surfaceLayerTopLeft, new Size(surfaceLayer.ActualWidth, surfaceLayer.ActualHeight)),
                    new Rect(hitCatcherTopLeft, new Size(hitCatcher.ActualWidth, hitCatcher.ActualHeight)));
            });

        Assert.Equal(width - GridMetrics.FileListHPadding, contentWidth, precision: 6);
        Assert.Equal(PanelMetrics.HeaderHeight, contentTopY, precision: 6);
        Assert.Equal(
            new Rect(PanelMetrics.ChromeOuterInset, PanelMetrics.ChromeOuterInset, width - (2 * PanelMetrics.ChromeOuterInset), height - (2 * PanelMetrics.ChromeOuterInset)),
            surfaceLayerRect);
        Assert.Equal(new Rect(0, 0, width, height), hitCatcherRect);
    }

    /// <summary>T-R5: Brush.HitTest.Catcher has Color.A == 1 in BOTH merge orders (D-5's documented HC exception — Colors.HighContrast.xaml deliberately does not override Color.HitTest.Catcher).</summary>
    [Fact]
    public void HitTestCatcherBrush_HasAlphaOfOne_InBothMergeOrders()
    {
        foreach (var highContrast in new[] { true, false })
        {
            var alpha = RunOnStaThread(() =>
            {
                var originalHighContrast = HighContrastState.IsActive;
                var beforeEnsure = Application.Current;
                var app = WpfApplicationHost.EnsureApplication();
                var ownsApplication = !ReferenceEquals(app, beforeEnsure);
                var originalResources = ownsApplication ? null : app.Resources;

                try
                {
                    app.Resources = new ResourceDictionary();
                    MergeResourceDictionaries(app, highContrast);

                    var brush = (SolidColorBrush)app.Resources["Brush.HitTest.Catcher"];
                    return brush.Color.A;
                }
                finally
                {
                    HighContrastState.IsActive = originalHighContrast;

                    if (!ownsApplication)
                        app.Resources = originalResources!;
                }
            });

            Assert.Equal((byte)1, alpha);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.2,
    // T-R6/T-R7/T-R8) — the glow effect / shader-free fallback / High Contrast regression pins (C5).
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// T-R6: exactly one element with a non-null <see cref="UIElement.Effect"/> exists anywhere in
    /// <see cref="PanelChrome"/>'s real template tree — <c>PART_Glow</c>, a real
    /// <see cref="DropShadowEffect"/> with the documented token values. After assigning a known
    /// <see cref="SolidColorBrush"/> to <see cref="PanelChrome.AccentBrush"/> and forcing layout, the
    /// EFFECT'S OWN <see cref="DropShadowEffect.Color"/> equals that brush's <see cref="Color"/> — catches
    /// a silently-unresolved <c>Color="{Binding AccentBrush.Color, RelativeSource={RelativeSource
    /// TemplatedParent}}"</c> binding inside a <see cref="Freezable"/> (plan R-2), which would otherwise
    /// leave the glow permanently black without throwing. Also pins zero <see cref="Effect"/>s in
    /// <c>GridViewTemplate</c>/<c>ListViewTemplate</c>'s <see cref="DataTemplate.LoadContent"/> and in
    /// <c>PanelItemContainerStyle</c>'s own <see cref="ControlTemplate"/> (plan D-9 "no per-item effects").
    /// </summary>
    /// <remarks>
    /// Explicitly forces <see cref="ChromeEffectsPolicy.OverrideIsGlowEffectEnabled"/> = <see langword="true"/>
    /// in a <see langword="try"/>/<see langword="finally"/> — this test is about <c>PART_Glow</c> ITSELF
    /// rendering with a real effect, which requires the shader-based path to be enabled; it must not depend
    /// on the real (test-host) <see cref="System.Windows.Media.RenderCapability.Tier"/> (a software-
    /// rendering CI agent would otherwise make this fail for a reason unrelated to what it actually tests)
    /// or on some earlier test in this same process having already cached a different value into
    /// <see cref="ChromeEffectsPolicy.IsGlowEffectEnabled"/>'s static cache.
    /// </remarks>
    [Fact]
    public void PanelChromeTemplate_ExactlyOneEffect_IsPartGlowWithTokenValuesAndResolvedAccentColor()
    {
        var knownAccent = Color.FromRgb(0x11, 0x22, 0x33);
        var originalOverride = ChromeEffectsPolicy.OverrideIsGlowEffectEnabled;

        (int effectCount, string glowName, bool hasEffect, double shadowDepth, double blurRadius, double effectOpacity, Color effectColor, int itemEffectCount) result;
        try
        {
            ChromeEffectsPolicy.OverrideIsGlowEffectEnabled = true;

            result = WithRealChrome(
                highContrast: false,
                width: 400,
                height: 300,
                state: PanelVisualState.Idle,
                isLayoutEditing: false,
                surfaceStyle: PanelSurfaceStyle.Ambient,
                body: chrome =>
                {
                    chrome.AccentBrush = new SolidColorBrush(knownAccent);
                    chrome.Measure(new Size(400, 300));
                    chrome.Arrange(new Rect(0, 0, 400, 300));
                    chrome.UpdateLayout();

                    var effects = CountEffectsInSubtree(chrome);
                    var glowElement = (FrameworkElement)chrome.Template.FindName("PART_Glow", chrome)!;
                    var glowEffect = glowElement.Effect as DropShadowEffect;

                    var app = System.Windows.Application.Current;
                    var gridTemplate = (DataTemplate)app.Resources["GridViewTemplate"];
                    var listTemplate = (DataTemplate)app.Resources["ListViewTemplate"];
                    var itemStyle = (Style)app.Resources["PanelItemContainerStyle"];
                    var itemControl = new ListBoxItem { Style = itemStyle };
                    itemControl.ApplyTemplate();

                    var itemEffects = CountEffectsInSubtree(gridTemplate.LoadContent())
                        + CountEffectsInSubtree(listTemplate.LoadContent())
                        + CountEffectsInSubtree(itemControl);

                    return (
                        effects,
                        glowElement.Name,
                        glowEffect is not null,
                        glowEffect?.ShadowDepth ?? double.NaN,
                        glowEffect?.BlurRadius ?? double.NaN,
                        glowEffect?.Opacity ?? double.NaN,
                        glowEffect?.Color ?? default,
                        itemEffects);
                });
        }
        finally
        {
            ChromeEffectsPolicy.OverrideIsGlowEffectEnabled = originalOverride;
        }

        Assert.Equal(1, result.effectCount);
        Assert.Equal("PART_Glow", result.glowName);
        Assert.True(result.hasEffect, "PART_Glow must have a real DropShadowEffect assigned.");
        Assert.Equal(0d, result.shadowDepth);
        Assert.Equal(PanelMetrics.GlowBlurRadius, result.blurRadius);
        Assert.Equal(PanelMetrics.GlowEffectOpacity, result.effectOpacity);
        Assert.Equal(knownAccent, result.effectColor);
        Assert.Equal(0, result.itemEffectCount);
    }

    /// <summary>
    /// T-R7 (full — plan §5.2): HC-merge + <c>IsActive=true</c> BEFORE constructing the instance —
    /// <c>PART_Glow</c> is Collapsed with <c>Effect == null</c>; <c>PART_GlowFallback</c>/
    /// <c>PART_AccentStroke</c>/<c>PART_SurfaceHighlight</c> are Collapsed; <c>PART_NeutralStroke.Opacity
    /// == 1</c>; <c>PART_AccentIndicator</c> stays visible (ADR-025 deliberately does not neutralize accent
    /// identity in High Contrast).
    /// </summary>
    /// <remarks>
    /// Explicitly forces <see cref="ChromeEffectsPolicy.OverrideIsGlowEffectEnabled"/> to a known value in a
    /// <see langword="try"/>/<see langword="finally"/> — same reasoning as T-R6 above — rather than depending
    /// on the real (test-host) <see cref="System.Windows.Media.RenderCapability.Tier"/> or on some earlier
    /// test in this same process having already cached a different value into
    /// <see cref="ChromeEffectsPolicy.IsGlowEffectEnabled"/>'s static cache. Both <see cref="TheoryAttribute"/>
    /// cases below must land on the exact SAME asserted state, which is the point: with
    /// <paramref name="glowEffectEnabledOverride"/> = <see langword="true"/>, the shader-based path would
    /// otherwise be enabled, so only the High Contrast <c>DataTrigger</c> (D-8) can be responsible for
    /// collapsing <c>PART_Glow</c>/<c>PART_GlowFallback</c> here — proving that trigger wins entirely on its
    /// own. With <paramref name="glowEffectEnabledOverride"/> = <see langword="false"/>, the shader-free-
    /// fallback <c>DataTrigger</c> (D-9) ALSO collapses <c>PART_Glow</c> (for the unrelated software/RDP-
    /// render-tier reason) and would normally reveal <c>PART_GlowFallback</c> — the real "High Contrast on a
    /// software render tier" machine this codebase must actually run on (e.g. an RDP session with High
    /// Contrast turned on) — proving the later-declared HC trigger (Panels.xaml's own trigger-order remarks)
    /// still wins the tie and keeps <c>PART_GlowFallback</c> Collapsed too, never Visible.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PanelChromeTemplate_HighContrast_CollapsesGlowFallbackAccentStrokeAndHighlight_KeepsMarkerVisible(bool glowEffectEnabledOverride)
    {
        var originalOverride = ChromeEffectsPolicy.OverrideIsGlowEffectEnabled;
        (Visibility glowVisibility, Effect? glowEffect, Visibility glowFallbackVisibility, Visibility accentStrokeVisibility, Visibility surfaceHighlightVisibility, double neutralStrokeOpacity, Visibility markerVisibility) result;
        try
        {
            ChromeEffectsPolicy.OverrideIsGlowEffectEnabled = glowEffectEnabledOverride;

            result = WithRealChrome(
                highContrast: true,
                width: 400,
                height: 300,
                state: PanelVisualState.Idle,
                isLayoutEditing: false,
                surfaceStyle: PanelSurfaceStyle.Ambient,
                body: chrome =>
                {
                    var glow = (UIElement)chrome.Template.FindName("PART_Glow", chrome)!;
                    var glowFallback = (UIElement)chrome.Template.FindName("PART_GlowFallback", chrome)!;
                    var accentStroke = (UIElement)chrome.Template.FindName("PART_AccentStroke", chrome)!;
                    var surfaceHighlight = (UIElement)chrome.Template.FindName("PART_SurfaceHighlight", chrome)!;
                    var neutralStroke = (UIElement)chrome.Template.FindName("PART_NeutralStroke", chrome)!;
                    var marker = (UIElement)chrome.Template.FindName("PART_AccentIndicator", chrome)!;

                    return (
                        glow.Visibility,
                        glow.Effect,
                        glowFallback.Visibility,
                        accentStroke.Visibility,
                        surfaceHighlight.Visibility,
                        neutralStroke.Opacity,
                        marker.Visibility);
                });
        }
        finally
        {
            ChromeEffectsPolicy.OverrideIsGlowEffectEnabled = originalOverride;
        }

        Assert.Equal(Visibility.Collapsed, result.glowVisibility);
        Assert.Equal(Visibility.Collapsed, result.surfaceHighlightVisibility);
        Assert.Null(result.glowEffect);
        Assert.Equal(Visibility.Collapsed, result.glowFallbackVisibility);
        Assert.Equal(Visibility.Collapsed, result.accentStrokeVisibility);
        Assert.Equal(1d, result.neutralStrokeOpacity);
        Assert.Equal(Visibility.Visible, result.markerVisibility);
    }

    /// <summary>T-R8: with <see cref="ChromeEffectsPolicy.IsGlowEffectEnabled"/> forced <see langword="false"/> (non-HC), <c>PART_Glow</c> is Collapsed with a null Effect and <c>PART_GlowFallback</c> is Visible with no Effect.</summary>
    [Fact]
    public void PanelChromeTemplate_GlowEffectDisabledByPolicy_ShowsFallback_NoEffect()
    {
        var original = ChromeEffectsPolicy.OverrideIsGlowEffectEnabled;
        try
        {
            ChromeEffectsPolicy.OverrideIsGlowEffectEnabled = false;

            var (glowVisibility, glowEffect, fallbackVisibility, fallbackEffect) = WithRealChrome(
                highContrast: false,
                width: 400,
                height: 300,
                state: PanelVisualState.Idle,
                isLayoutEditing: false,
                surfaceStyle: PanelSurfaceStyle.Ambient,
                body: chrome =>
                {
                    var glow = (UIElement)chrome.Template.FindName("PART_Glow", chrome)!;
                    var glowFallback = (UIElement)chrome.Template.FindName("PART_GlowFallback", chrome)!;
                    return (glow.Visibility, glow.Effect, glowFallback.Visibility, glowFallback.Effect);
                });

            Assert.Equal(Visibility.Collapsed, glowVisibility);
            Assert.Null(glowEffect);
            Assert.Equal(Visibility.Visible, fallbackVisibility);
            Assert.Null(fallbackEffect);
        }
        finally
        {
            ChromeEffectsPolicy.OverrideIsGlowEffectEnabled = original;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.2,
    // T-R9/T-R10) — rolled-up geometry and the single clip-owner invariant.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// T-R9 (rolled-up): <c>Arrange(400, PanelMetrics.HeaderHeight)</c> — the exact size a rolled-up
    /// panel's window collapses to (<c>ChildWindow.xaml</c>'s <c>RollUpBehavior</c> collapses
    /// <c>ContentRow</c> to 0, leaving only the header row, <see cref="PanelMetrics.HeaderHeight"/> tall —
    /// see <see cref="PanelMetrics.HeaderContentGap"/>'s own remarks) — <c>PART_Header.ActualHeight</c>
    /// still covers <see cref="PanelMetrics.HeaderButtonSize"/> (plan D-3's rolled-up row: the whole reason
    /// <see cref="PanelMetrics.ChromeOuterInset"/> is capped at 2, not wider), <c>PART_Header</c>'s own
    /// bottom edge never falls past <c>PART_SurfaceLayer</c>'s own bottom edge, and
    /// <c>PART_InteractionOverlay</c> still spans the WHOLE <c>PART_SurfaceLayer</c> (D-2's "moved from the
    /// old two-row Grid to PART_SurfaceLayer directly ... same coverage" claim, pinned for the rolled-up
    /// size specifically, since that is the one size where the pre-ADR-030 template's two GridRows would
    /// have collapsed one of them to zero).
    /// </summary>
    [Fact]
    public void PanelChromeTemplate_RolledUpSize_HeaderCoversButtonSize_AndInteractionOverlaySpansWholeSurfaceLayer()
    {
        var (headerActualHeight, headerBottom, surfaceLayerRect, overlayTopLeft, overlayWidth, overlayHeight) = WithRealChrome(
            highContrast: false,
            width: 400,
            height: PanelMetrics.HeaderHeight,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                var header = (FrameworkElement)chrome.Template.FindName("PART_Header", chrome)!;
                var surfaceLayer = (FrameworkElement)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
                var overlay = (FrameworkElement)chrome.Template.FindName("PART_InteractionOverlay", chrome)!;

                var headerBottomInChrome = header.TranslatePoint(new Point(0, header.ActualHeight), chrome).Y;
                var surfaceLayerTopLeft = surfaceLayer.TranslatePoint(new Point(0, 0), chrome);
                var surfaceRect = new Rect(surfaceLayerTopLeft, new Size(surfaceLayer.ActualWidth, surfaceLayer.ActualHeight));
                var overlayTopLeftInSurfaceLayer = overlay.TranslatePoint(new Point(0, 0), surfaceLayer);

                return (header.ActualHeight, headerBottomInChrome, surfaceRect, overlayTopLeftInSurfaceLayer, overlay.ActualWidth, overlay.ActualHeight);
            });

        Assert.True(
            headerActualHeight >= PanelMetrics.HeaderButtonSize,
            $"PART_Header.ActualHeight ({headerActualHeight}) must be >= HeaderButtonSize ({PanelMetrics.HeaderButtonSize}) when rolled up.");

        Assert.True(
            headerBottom <= surfaceLayerRect.Bottom + 0.01,
            $"PART_Header's own bottom ({headerBottom}) must not fall past PART_SurfaceLayer's own bottom ({surfaceLayerRect.Bottom}) when rolled up.");

        Assert.Equal(0d, overlayTopLeft.X, precision: 6);
        Assert.Equal(0d, overlayTopLeft.Y, precision: 6);
        Assert.Equal(surfaceLayerRect.Width, overlayWidth, precision: 6);
        Assert.Equal(surfaceLayerRect.Height, overlayHeight, precision: 6);
    }

    /// <summary>
    /// T-R10 (single clip owner, plan D-12): <c>PART_SurfaceLayer.Clip</c> is a
    /// <see cref="RectangleGeometry"/> with <c>(RadiusX, RadiusY) == Radius.Panel</c> (the REAL compiled
    /// resource, not a hand-copied literal) and a <see cref="RectangleGeometry.Rect"/> sized
    /// <c>(ActualWidth - 2*ChromeOuterInset, ActualHeight - 2*ChromeOuterInset)</c> — i.e. the window rect
    /// shrunk once by the inset (the SAME <c>Grid.Clip</c> MultiBinding <c>Panels.xaml</c>'s own remarks
    /// describe). <c>PART_Chrome.Clip</c> is <see langword="null"/> — its own former Clip moved to
    /// <c>PART_SurfaceLayer</c>, so there is exactly ONE rounded-rect clip on the whole panel, not one per
    /// decorative layer.
    /// </summary>
    [Fact]
    public void PanelChromeTemplate_SurfaceLayerIsTheSoleClipOwner_PartChromeHasNoClipOfItsOwn()
    {
        const double width = 400d;
        const double height = 300d;

        var (surfaceLayerClip, partChromeClip, radiusPanel) = WithRealChrome(
            highContrast: false,
            width: width,
            height: height,
            state: PanelVisualState.Idle,
            isLayoutEditing: false,
            surfaceStyle: PanelSurfaceStyle.Ambient,
            body: chrome =>
            {
                var surfaceLayer = (FrameworkElement)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
                var partChrome = (FrameworkElement)chrome.Template.FindName("PART_Chrome", chrome)!;
                var resources = System.Windows.Application.Current!.Resources;
                var realRadiusPanel = (CornerRadius)resources["Radius.Panel"];

                return (surfaceLayer.Clip, partChrome.Clip, realRadiusPanel);
            });

        var clipGeometry = Assert.IsType<RectangleGeometry>(surfaceLayerClip);
        Assert.Equal(radiusPanel.TopLeft, clipGeometry.RadiusX);
        Assert.Equal(radiusPanel.TopLeft, clipGeometry.RadiusY);
        Assert.Equal(
            new Rect(0, 0, width - (2 * PanelMetrics.ChromeOuterInset), height - (2 * PanelMetrics.ChromeOuterInset)),
            clipGeometry.Rect);

        Assert.Null(partChromeClip);
    }

    /// <summary>
    /// UX-P2 fix regression pin (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1): List mode's item
    /// root switched from a horizontal <see cref="StackPanel"/> to a <see cref="Grid"/> with a star
    /// column for the label specifically so <c>TextTrimming="CharacterEllipsis"</c> has a finite width to
    /// truncate against — a <see cref="StackPanel"/> always measures its orientation-axis children with
    /// an infinite available width, so the label previously rendered at its own full, un-clipped desired
    /// width regardless of the item's actual on-screen width. Reuses this class's own STA/resource-merge
    /// infrastructure to load the REAL <c>ListViewTemplate</c>/its real <c>ItemTemplate</c> (not a
    /// hand-reimplemented stand-in), gives it a very long <see cref="FileItem.DisplayName"/>, and
    /// Measures/Arranges it at a deliberately narrow width — this would have failed against the
    /// pre-fix <see cref="StackPanel"/> markup (the label's <see cref="FrameworkElement.ActualWidth"/>
    /// would have come out far wider than the available width, instead of clamped to it).
    /// </summary>
    [Fact]
    public void ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth()
    {
        var (labelWidth, availableWidth) = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var listTemplate = (DataTemplate)app.Resources["ListViewTemplate"];
                var listBox = (ListBox)listTemplate.LoadContent();
                var itemTemplate = listBox.ItemTemplate;

                var root = (FrameworkElement)itemTemplate.LoadContent();
                root.DataContext = new FileItem(
                    path: @"C:\workspace-grid-07-visual\" + new string('A', 200) + ".txt",
                    icon: null,
                    displayName: new string('A', 200));

                const double narrow = 150d;
                root.Measure(new Size(narrow, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, narrow, Math.Max(root.DesiredSize.Height, 20)));

                var label = (TextBlock)root.FindName("DisplayNameText")!;
                return (label.ActualWidth, narrow);
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        Assert.True(
            labelWidth <= availableWidth,
            $"List-mode label TextBlock ({labelWidth:F1}) must stay within the item's available width " +
            $"({availableWidth:F1}) even for a very long DisplayName.");
    }

    /// <summary>
    /// MAJOR-B fix-cycle 2 regression pin (docs/PLANS/workspace-grid-07-visual.md): the test above
    /// (<see cref="ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth"/>) measures
    /// the item <see cref="DataTemplate"/>'s ROOT element directly (<c>itemTemplate.LoadContent()</c>,
    /// then <c>Measure</c>/<c>Arrange</c> on THAT), never going through the real <see cref="ListBox"/>/
    /// <see cref="ScrollViewer"/> that actually hosts it in the running app — it therefore stayed green
    /// even though <c>ListViewTemplate</c>'s <see cref="ListBox"/> never set
    /// <c>ScrollViewer.HorizontalScrollBarVisibility="Disabled"</c> (unlike <c>GridViewTemplate</c>, which
    /// always has), leaving <c>CanHorizontallyScroll=true</c> and a REAL horizontal scrollbar/overflow in
    /// the running app instead of the intended ellipsis. This test instead loads the REAL
    /// <c>ListViewTemplate</c> <see cref="ListBox"/> element (so it reads whatever value that XAML
    /// actually sets for the <c>ScrollViewer.HorizontalScrollBarVisibility</c> attached property — this
    /// test fails against the pre-fix-cycle-2 markup, confirmed by a temporary local revert before writing
    /// the fix), gives it a minimal-but-faithful <see cref="ControlTemplate"/> (a real
    /// <see cref="ScrollViewer"/>, <see cref="TemplateBindingExtension"/>-bound to that SAME attached
    /// property, hosting a real <see cref="ItemsPresenter"/>/<see cref="StackPanel"/> — the "give it the
    /// smallest real template it needs, skip the OS theme" convention this file's own
    /// <see cref="MaterializePanelChromeTemplate"/>/<c>ListReorderGeometryHitTestTests</c> already
    /// establish, since the OS/theme default <see cref="ListBox"/> template does not reliably realize
    /// containers in this headless test host), lays it out at a narrow width with a very long
    /// <see cref="FileItem.DisplayName"/>, and asserts against the REAL generated <see cref="ListBoxItem"/>
    /// container and its REAL descendant <see cref="TextBlock"/> — not a hand-simulated stand-in.
    /// </summary>
    [Fact]
    public void ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow()
    {
        var (containerWidth, labelWidth, availableWidth) = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var listTemplate = (DataTemplate)app.Resources["ListViewTemplate"];
                var listBox = (ListBox)listTemplate.LoadContent();

                // Minimal-but-faithful template: a real ScrollViewer, TemplateBound to the SAME
                // ScrollViewer.HorizontalScrollBarVisibility attached property ListViewTemplate's own XAML
                // sets on `listBox` above, hosting a real ItemsPresenter — see this test's own remarks for
                // why the OS default ListBox template is not used here instead.
                var itemsPresenterFactory = new FrameworkElementFactory(typeof(ItemsPresenter));
                var scrollViewerFactory = new FrameworkElementFactory(typeof(ScrollViewer));
                scrollViewerFactory.SetValue(
                    ScrollViewer.HorizontalScrollBarVisibilityProperty,
                    new TemplateBindingExtension(ScrollViewer.HorizontalScrollBarVisibilityProperty));
                scrollViewerFactory.SetValue(
                    ScrollViewer.VerticalScrollBarVisibilityProperty,
                    new TemplateBindingExtension(ScrollViewer.VerticalScrollBarVisibilityProperty));
                scrollViewerFactory.AppendChild(itemsPresenterFactory);
                listBox.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scrollViewerFactory };

                // Plain (non-virtualizing) vertical StackPanel — the same simplification
                // ListReorderGeometryHitTestTests/AdaptiveIconGridPanelTests already use for their own
                // ItemsPanel in this headless host, and irrelevant to the mechanism under test: a
                // VirtualizingStackPanel's own IScrollInfo machinery is not what CanHorizontallyScroll
                // drives here — ScrollContentPresenter's physical-scrolling Measure behavior (infinite vs.
                // constrained width) applies identically to any Panel content.
                listBox.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));

                listBox.DataContext = new List<FileItem>
                {
                    new(
                        path: @"C:\workspace-grid-07-visual\" + new string('A', 200) + ".txt",
                        icon: null,
                        displayName: new string('A', 200)),
                };

                const double narrow = 150d;
                listBox.Measure(new Size(narrow, double.PositiveInfinity));
                listBox.Arrange(new Rect(0, 0, narrow, listBox.DesiredSize.Height));
                listBox.UpdateLayout();

                var container = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0)!;
                var label = FindVisualChildByName<TextBlock>(container, "DisplayNameText")!;

                return (container.ActualWidth, label.ActualWidth, narrow);
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        Assert.True(
            containerWidth <= availableWidth + 0.5,
            $"The real generated ListBoxItem container ({containerWidth:F1}) must not overflow the " +
            $"ListBox's own available width ({availableWidth:F1}) — a wider container means a horizontal " +
            "scrollbar/clip instead of the intended ellipsis.");
        Assert.True(
            labelWidth <= availableWidth,
            $"List-mode label TextBlock ({labelWidth:F1}) must stay within the item's available width " +
            $"({availableWidth:F1}) even for a very long DisplayName, through the REAL ListBox/ScrollViewer pipeline.");
    }

    /// <summary>
    /// Depth-first visual-tree search for a descendant of type <typeparamref name="T"/> named
    /// <paramref name="name"/> — used only by
    /// <see cref="ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow"/> to reach
    /// the generated item template's <c>DisplayNameText</c> <see cref="TextBlock"/> through a real
    /// <see cref="ListBoxItem"/> container's own <see cref="ContentPresenter"/>.
    /// </summary>
    private static T? FindVisualChildByName<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name)
                return typed;

            var descendant = FindVisualChildByName<T>(child, name);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="func"/> to completion on a brand-new STA thread and returns its result —
    /// see this class's own remarks for why STA is required here.
    /// </summary>
    /// <remarks>
    /// M-2 fix (P08 fix cycle 2): delegates to <see cref="StaTestRunner.Run{T}"/> instead of a
    /// hand-rolled duplicate of its STA-thread/ownership machinery — this class's own former copy kept
    /// the owning thread pumping forever (exactly like <see cref="StaTestRunner"/> does — see its own
    /// remarks for why that is required, not optional), but with NO
    /// <see cref="Dispatcher.UnhandledException"/> absorber, so a leaked callback throwing on that
    /// pumped-forever thread was an unguarded process kill. <see cref="StaTestRunner.Run{T}"/> already
    /// closes that gap.
    /// </remarks>
    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);

    /// <summary>
    /// Mirrors <c>DesktopOrganizer.UI.App</c>'s private <c>LoadResourceDictionaries()</c>
    /// (src/DesktopOrganizer.UI/App.xaml.cs) — same 9 dictionaries, same relative pack URIs, same
    /// conditional <c>Colors.HighContrast.xaml</c> insertion point between <c>Colors.xaml</c> and
    /// <c>Spacing.xaml</c>. Keep this in sync if that method's list/order ever changes. Driven by an
    /// explicit <paramref name="highContrast"/> bool instead of the real
    /// <see cref="SystemParameters.HighContrast"/> (a live OS accessibility setting a test can't flip)
    /// so both merge orders are exercised deterministically regardless of the CI machine's actual
    /// accessibility settings.
    /// </summary>
    private static void MergeResourceDictionaries(Application app, bool highContrast)
    {
        HighContrastState.IsActive = highContrast;

        var merged = app.Resources.MergedDictionaries;
        merged.Add(LoadResourceDictionary("/Resources/Styles/Colors.xaml"));

        if (highContrast)
            merged.Add(LoadResourceDictionary("/Resources/Styles/Colors.HighContrast.xaml"));

        merged.Add(LoadResourceDictionary("/Resources/Styles/Spacing.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Brushes.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Typography.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Icons.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Controls.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Panels.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Templates.xaml"));
    }

    private static ResourceDictionary LoadResourceDictionary(string relativePackUri)
        => new() { Source = new Uri($"/DesktopOrganizer.UI;component{relativePackUri}", UriKind.Relative) };

    /// <summary>Depth-first count of descendants (visual tree) with a non-null <see cref="UIElement.Effect"/>, including <paramref name="root"/> itself.</summary>
    private static int CountEffectsInSubtree(DependencyObject root)
    {
        var count = root is UIElement { Effect: not null } ? 1 : 0;

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
            count += CountEffectsInSubtree(VisualTreeHelper.GetChild(root, i));

        return count;
    }

    /// <summary>
    /// ADR-030 (docs/PLANS/reference-panel-chrome.md §5.2) shared harness — builds a real
    /// <see cref="PanelChrome"/> against the real merged resource chain, applies its actual (implicit,
    /// type-keyed) default style/template, sets the requested visual-state/editing/surface-style/size
    /// inputs, forces a real Measure/Arrange/UpdateLayout pass, and runs <paramref name="body"/> against
    /// it — all on a dedicated STA thread, with the same HighContrastState/app.Resources restore-in-finally
    /// convention every other test in this class follows. <see cref="PanelChrome.AccentBrush"/> is always a
    /// real, non-default <see cref="SolidColorBrush"/> (not the DP's own <see cref="Brushes.Transparent"/>
    /// default) — several consumers (PART_AccentStroke's <c>BorderBrush</c>, PART_AccentIndicator's own
    /// fill/stroke) would otherwise render invisibly, masking a real binding failure as a false pass.
    /// </summary>
    private static T WithRealChrome<T>(
        bool highContrast,
        double width,
        double height,
        PanelVisualState state,
        bool isLayoutEditing,
        PanelSurfaceStyle surfaceStyle,
        Func<PanelChrome, T> body)
    {
        return RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast);

                var style = (Style)app.Resources[typeof(PanelChrome)];
                var chrome = new PanelChrome
                {
                    Style = style,
                    VisualState = state,
                    IsLayoutEditing = isLayoutEditing,
                    SurfaceStyle = surfaceStyle,
                    AccentBrush = new SolidColorBrush(Colors.Red),

                    // A real, fixed-size Content/TitleEditor — without these, PART_ContentHost/PART_Header
                    // have nothing to measure, so PanelChrome.DesiredSize would instead be dominated by the
                    // decorative strokes/marker's own tiny, LEGITIMATELY state-varying sizes (their opacity/
                    // thickness ARE meant to change per state — that is not the "content moved" jitter T-R3
                    // guards against). Fixed, oversized content keeps DesiredSize's own dominant term stable.
                    Content = new Border { Width = 200, Height = 150 },
                    TitleEditor = new TextBlock { Text = "Sample", Width = 100, Height = 16 },
                };

                chrome.ApplyTemplate();
                chrome.Measure(new Size(width, height));
                chrome.Arrange(new Rect(0, 0, width, height));
                chrome.UpdateLayout();

                return body(chrome);
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });
    }

    /// <summary>A named template part's own origin (in the owning <see cref="PanelChrome"/>'s local coordinates) and size — T-R3's jitter-comparison unit.</summary>
    private readonly record struct PartSnapshot(Point Origin, double Width, double Height);

    private static PartSnapshot SnapshotPart(PanelChrome chrome, string partName)
    {
        var element = (FrameworkElement)chrome.Template.FindName(partName, chrome)!;
        return new PartSnapshot(element.TranslatePoint(new Point(0, 0), chrome), element.ActualWidth, element.ActualHeight);
    }

    /// <summary>
    /// Walks every merged dictionary in the chain (recursively, in case any of them nest their own
    /// <see cref="ResourceDictionary.MergedDictionaries"/>) and touches every key via the indexer — this
    /// forces a deferred <c>Style</c>/<c>ControlTemplate</c> (and any <c>StaticResource</c>/<c>x:Static</c>
    /// it references) to actually resolve, instead of merely sitting unparsed in the dictionary.
    /// </summary>
    private static void TouchAllResourceKeys(ResourceDictionary dictionary)
    {
        foreach (ResourceDictionary child in dictionary.MergedDictionaries)
            TouchAllResourceKeys(child);

        foreach (var key in new ArrayList(dictionary.Keys))
            _ = dictionary[key];
    }

    /// <summary>
    /// <see cref="DataTemplate.LoadContent"/> is what actually materializes
    /// <c>PanelItemContainerStyle</c>'s <c>ControlTemplate</c> (reached via <c>ListViewTemplate</c>/
    /// <c>GridViewTemplate</c>'s <c>ItemContainerStyle="{StaticResource PanelItemContainerStyle}"</c>) —
    /// a plain key lookup (<see cref="TouchAllResourceKeys"/>) alone is not enough to reproduce
    /// CRITICAL-1; only loading the template's content is.
    /// </summary>
    private static void LoadTemplateContent(Application app, string templateKey)
    {
        var template = (DataTemplate)app.Resources[templateKey];
        template.LoadContent();
    }

    /// <summary>
    /// MAJOR-D fix (review round 3): <see cref="TouchAllResourceKeys"/> plus <see cref="LoadTemplateContent"/>
    /// above touch every dictionary KEY and materialize <c>PanelItemContainerStyle</c>'s
    /// <see cref="ControlTemplate"/> content (via <c>ListViewTemplate</c>/<c>GridViewTemplate</c>'s
    /// <c>ItemContainerStyle</c>), but neither one ever parses <c>Panels.xaml</c>'s <c>PanelChrome</c>
    /// implicit-<see cref="Style"/> <see cref="ControlTemplate"/> BODY — a plain key lookup only returns
    /// the still-deferred <see cref="Style"/> object; its <c>ControlTemplate</c> setter's content is only
    /// actually parsed when a real <see cref="PanelChrome"/> instance applies that template. Proven
    /// empirically by a reviewer temporarily typo-ing the <c>AccentIndicatorWidth</c>
    /// <c>StaticResource</c> reference inside that template in a prior round: <c>dotnet build</c> stayed
    /// clean and every other test (including the two above) stayed green, while a real render would have
    /// thrown <see cref="System.Windows.Markup.XamlParseException"/>. Constructing a
    /// <see cref="PanelChrome"/>, applying its (implicit, type-keyed) default style, and forcing
    /// Measure/Arrange is what actually reproduces that failure mode here.
    /// </summary>
    private static void MaterializePanelChromeTemplate(Application app)
    {
        var style = (Style)app.Resources[typeof(PanelChrome)];
        var chrome = new PanelChrome { Style = style };

        chrome.ApplyTemplate();
        chrome.Measure(new Size(400, 300));
        chrome.Arrange(new Rect(0, 0, 400, 300));
    }
}
