using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Builds a rounded-rectangle clip <see cref="Geometry"/> from an element's own
/// <c>(ActualWidth, ActualHeight, CornerRadius)</c> — docs/PLANS/workspace-grid-07-visual.md "Border
/// radius". A <see cref="Border"/> paints its own <see cref="Border.Background"/>/
/// <see cref="Border.BorderBrush"/> with rounded corners natively, but does NOT clip arbitrary CHILD
/// content to that same rounded shape — a square-cornered descendant (a
/// <see cref="System.Windows.Controls.ListBox"/>'s scrollbar, a selection rectangle sitting flush
/// against the edge) can render past the rounded corner into the transparent area outside it.
/// <para>
/// ADR-030 (docs/PLANS/reference-panel-chrome.md §1 D-2/D-12) moved this converter's ONE consumer from
/// <c>PART_Chrome</c>'s own <see cref="UIElement.Clip"/> to <c>PART_SurfaceLayer</c>'s
/// (<c>Resources/Styles/Panels.xaml</c>) — the layered template's single shared clip owner: every
/// decorative layer inside <c>PART_SurfaceLayer</c> (<c>PART_Chrome</c> included) now obeys ONE rounded
/// rectangle instead of each layer clipping itself independently. This converter's own output is
/// unchanged either way; it makes whatever it is bound to obey the exact same rounded rectangle the
/// chrome's own background paints, which is what makes widening <c>Radius.Panel</c> (Spacing.xaml) safe
/// in the first place.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>WPF-internal hit-test scoping (verified, still true after fix-cycle 1):</b> <c>ResizeOverlay</c>
/// (<c>Views/ChildWindow.xaml</c>) is a SIBLING of <c>PanelChrome</c>, not a descendant —
/// <see cref="UIElement.Clip"/> only ever affects the element it is set on and that element's own
/// subtree, so this converter's output can never reach the WPF <c>Thumb</c> resize handles' OWN
/// hit-test routing. Nor does it touch the native <c>WM_NCHITTEST</c> path (<c>NativeHitTestMapper</c>),
/// which classifies purely against the window's rectangular <c>GetWindowRect</c>/border-strip geometry
/// and has no notion of corner radius at all. <see cref="UIElement.Clip"/> DOES affect hit-testing for
/// whatever it IS set on (<c>PART_Chrome</c>'s own children — the header/title bar and content host): a
/// click on the now-rounded-away corner pixel is correctly treated as a miss THERE, matching what is
/// actually painted (WYSIWYG).
/// </para>
/// <para>
/// <b>A separate, real risk this scoping claim does NOT rule out (MAJOR-1, docs/PLANS/workspace-grid-07-visual.md
/// fix-cycle 1):</b> the panel window carries <c>AllowsTransparency="True"</c> — a layered window whose
/// mouse-input routing is decided by the OS from the FINAL COMPOSITED PIXEL ALPHA at each screen
/// location, entirely independent of WPF's own visual-tree hit-testing. A screen pixel that ends up with
/// alpha 0 in the composited window bitmap — which is exactly what the corner square OUTSIDE a rounded
/// <c>Border</c>'s own quarter-circle arc always is, at ANY <c>CornerRadius</c> &gt; 0, whether or not this
/// converter's <see cref="UIElement.Clip"/> is even applied, since <c>Border.Background</c> paints a
/// rounded shape natively — routes the mouse-down straight through to whatever is beneath this window
/// (the desktop), and this window's WPF message loop never sees it at all. This affects the resize
/// <c>Thumb</c> sitting at that same screen location too: its own <c>Background="Transparent"</c>
/// (<c>Controls.xaml</c>'s <c>ResizeHandleStyle</c>) paints nothing opaque there either, so nothing in
/// either layer gives the OS a non-zero-alpha pixel to route the click to. The always-transparent lens'
/// area (<c>r² · (1 - π/4)</c>, ≈13.7 DIP² at the widened <c>r=8</c> — see
/// <see cref="DesktopOrganizer.UI.Models.PanelMetrics.ResizeCornerThickness"/>'s own remarks) GREW when
/// <c>Radius.Panel</c> widened 6 -&gt; 8 in this same fix-cycle; widening the resize grip to match (also
/// this fix-cycle) does not shrink that lens, but does shrink the fraction of the grip's own footprint it
/// occupies, so most of a diagonal-resize gesture's initial mouse-down still lands on a live pixel.
/// </para>
/// <para>
/// <b>ADR-030 update (D-5):</b> <c>PART_HitCatcher</c> (<c>Resources/Styles/Panels.xaml</c>, a
/// near-invisible but OPAQUE <see cref="Border"/> spanning the WHOLE window rect, underneath everything
/// else) closes this always-transparent lens on BOTH the WPF <c>Thumb</c> path and the native
/// <c>WM_NCHITTEST</c> path: the OS now always finds a non-zero-alpha pixel to route the click to,
/// regardless of where the panel's own rounded corner leaves the composited bitmap transparent. The
/// click-through risk this remark describes is therefore closed at the WINDOW level, not by anything this
/// converter's own output does.
/// </para>
/// <para>
/// Same defensive contract as the other Ambient Accent converters (never throws): missing/wrongly-typed
/// values or a non-positive/non-finite size return <see cref="Binding.DoNothing"/> rather than a
/// degenerate empty geometry — <see cref="Binding.DoNothing"/> leaves <see cref="UIElement.Clip"/> at
/// whatever it already was (initially unset/<see langword="null"/>, i.e. unclipped), the same
/// visually-inert state <c>PART_Chrome</c> had before this converter existed, rather than clipping the
/// whole panel away to nothing during the brief window before the first real layout pass reports a size.
/// </para>
/// </remarks>
public sealed class RoundedRectClipConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 3)
            return Binding.DoNothing;

        if (values[0] is not double width || values[1] is not double height || values[2] is not CornerRadius radius)
            return Binding.DoNothing;

        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ||
            double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            return Binding.DoNothing;

        // RectangleGeometry only supports a single uniform (RadiusX, RadiusY) pair. Every consumer of
        // this converter (Resources/Styles/Spacing.xaml's Radius.Panel) declares a uniform CornerRadius
        // (all four corners equal) — reading just TopLeft loses nothing for this codebase's tokens.
        var cornerRadius = Math.Max(0, radius.TopLeft);

        var geometry = new RectangleGeometry(new Rect(0, 0, width, height), cornerRadius, cornerRadius);
        geometry.Freeze();
        return geometry;
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
