using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// The single point that writes a panel's on-screen bounds — <see cref="Window.Left"/>/<see cref="Window.Top"/>/
/// <see cref="FrameworkElement.Width"/>/<see cref="FrameworkElement.Height"/> — AND the corresponding
/// <see cref="ChildWindowModel"/> fields (<see cref="ChildWindowModel.Position"/>/<see cref="ChildWindowModel.Size"/>,
/// and — for a <see cref="PanelGeometryMode.GridAligned"/> write — the span/monitor id) in one atomic
/// act (docs/PLANS/workspace-grid-03-04-integration.md §1.5).
/// </summary>
/// <remarks>
/// <b>Problem this guards against (baseline risk 17).</b> <c>ChildWindowModel.GeometryMode</c>/
/// <c>WidthSpanUnits</c>/<c>HeightSpanUnits</c> raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
/// (unlike <c>Position</c>/<c>Size</c>, which do not). If P03/P04 code naively wrote
/// <c>Window.Width</c>/<c>Height</c> from an <c>OnModelPropertyChanged</c> subscriber reacting to a
/// span change, the result would be a synchronous cycle:
/// <c>ChildWindow.OnRenderSizeChanged -&gt; model.Size -&gt; (a span write) -&gt; INPC -&gt; a subscriber
/// -&gt; Window.Width -&gt; OnRenderSizeChanged</c>. This type breaks that cycle two ways: (1) it is the
/// only place that writes a span as part of a geometry change, so no such subscriber is ever needed
/// (see <see cref="Apply"/>'s parameters); (2) <see cref="IsApplying"/> lets
/// <c>ChildWindow.OnLocationChanged</c>/<c>OnRenderSizeChanged</c> skip the redundant model write they
/// would otherwise perform while a call to <see cref="Apply"/> is already in flight and has written the
/// same values directly.
/// <para>
/// <b>Visibility (P03).</b> Public rather than <see langword="internal"/> only because
/// <see cref="PanelDragMoveBehavior"/> — itself public — takes one of these as a constructor parameter
/// (C# forbids a public member exposing a less accessible type). No code outside this assembly
/// constructs or calls it.
/// </para>
/// <para>
/// <b>release-hardening F (ADR-037).</b> <see cref="Apply"/>/<see cref="ApplyPosition"/>'s bounds are
/// DESKTOP-DIP — the single space every model/persisted geometry lives in — not literally
/// <see cref="Window.Left"/>/<see cref="Window.Top"/>'s own space, which can now differ from desktop-DIP
/// on a mixed-DPI TopLevel desktop layer. <see cref="Interaction.PanelWindowCoordinates"/> performs that
/// one conversion, right here, so every caller keeps passing desktop-DIP bounds exactly as it always did
/// (identity on any uniform snapshot — see that type's own remarks).
/// </para>
/// </remarks>
public sealed class PanelGeometryWriter(Window window, Func<ChildWindowModel?> getModel, IDesktopScaleProvider scaleProvider)
{
    private bool _applying;

    /// <summary>
    /// <see langword="true"/> for the duration of a call to <see cref="Apply"/> — including while any
    /// WPF layout callback (<c>OnLocationChanged</c>/<c>OnRenderSizeChanged</c>) fires synchronously as
    /// a side effect of the <see cref="Window.Left"/>/<see cref="Window.Top"/>/<see cref="FrameworkElement.Width"/>/
    /// <see cref="FrameworkElement.Height"/> assignments inside it.
    /// </summary>
    public bool IsApplying => _applying;

    /// <summary>
    /// Writes <paramref name="boundsWindowSpace"/> onto the window and mirrors it (plus
    /// <paramref name="span"/>/<paramref name="monitorDeviceId"/>, when supplied) onto the currently
    /// attached model, all under <see cref="IsApplying"/>. A no-op if no model is currently attached
    /// (<c>ChildWindow.Initialize</c> not yet called) — there is nothing to keep in sync with in that
    /// case, and writing the window alone without its model would itself create the very drift this
    /// type exists to prevent.
    /// </summary>
    /// <param name="boundsWindowSpace">
    /// The panel's new bounds, already in <see cref="Window.Left"/>/<see cref="Window.Top"/>'s own
    /// coordinate space (see <see cref="DesktopCoordinateSpace"/>) — this type performs no coordinate
    /// translation of its own.
    /// </param>
    /// <param name="span">
    /// When supplied, also written onto <see cref="ChildWindowModel.WidthSpanUnits"/>/
    /// <see cref="ChildWindowModel.HeightSpanUnits"/>. Pass <see langword="null"/> for a
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> write, which never has a meaningful span.
    /// </param>
    /// <param name="monitorDeviceId">
    /// When supplied, also written onto <see cref="ChildWindowModel.MonitorDeviceId"/> (F-6). Pass
    /// <see langword="null"/> to leave the model's current value untouched.
    /// </param>
    public void Apply(Rect boundsWindowSpace, GridSpan? span, string? monitorDeviceId)
        => Write(boundsWindowSpace, span, monitorDeviceId, positionOnly: false);

    /// <summary>
    /// Position-only sibling of <see cref="Apply"/> (P03): writes <see cref="Window.Left"/>/<see cref="Window.Top"/>
    /// and <see cref="ChildWindowModel.Position"/> (plus <paramref name="span"/>/<paramref name="monitorDeviceId"/>
    /// when supplied) and deliberately leaves <see cref="FrameworkElement.Width"/>/<see cref="FrameworkElement.Height"/> and
    /// <see cref="ChildWindowModel.Size"/> untouched — used by every P03 path, all of which MOVE a panel
    /// without resizing it (drop-time snap, cross-monitor <c>MonitorDeviceId</c> update, "Align to
    /// workspace grid" on a rolled-up panel).
    /// </summary>
    /// <remarks>
    /// Needed as its own entry point rather than "call <see cref="Apply"/> with the current size" for two
    /// reasons a move-only caller cannot work around: (1) while a panel is rolled up,
    /// <see cref="FrameworkElement.Height"/> is the collapsed header height and <see cref="ChildWindowModel.Size"/>
    /// deliberately still holds the EXPANDED height for restore (<c>ChildWindow.OnRenderSizeChanged</c>
    /// skips its write while <see cref="ChildWindowModel.IsRolledUp"/>) — feeding the live height back
    /// through <see cref="Apply"/> would overwrite that saved expanded height with the collapsed one;
    /// (2) a window whose <c>Width</c>/<c>Height</c> were never explicitly assigned reports
    /// <see cref="double.NaN"/>, which <see cref="Apply"/> would convert into a nonsensical integer
    /// <see cref="ChildWindowModel.Size"/>.
    /// </remarks>
    /// <param name="topLeftWindowSpace">The panel's new top-left, already in <see cref="Window.Left"/>/<see cref="Window.Top"/>'s own space.</param>
    /// <param name="span">Same contract as <see cref="Apply"/>'s own <c>span</c> parameter.</param>
    /// <param name="monitorDeviceId">Same contract as <see cref="Apply"/>'s own <c>monitorDeviceId</c> parameter.</param>
    public void ApplyPosition(System.Windows.Point topLeftWindowSpace, GridSpan? span, string? monitorDeviceId)
        => Write(new Rect(topLeftWindowSpace, new System.Windows.Size(0d, 0d)), span, monitorDeviceId, positionOnly: true);

    private void Write(Rect boundsWindowSpace, GridSpan? span, string? monitorDeviceId, bool positionOnly)
    {
        if (getModel() is not { } model) return;

        // Defence in depth: Apply is not expected to re-enter synchronously (nothing it does calls back
        // into itself), but guarding it the same way the callbacks below are guarded costs nothing and
        // keeps the invariant "IsApplying implies exactly one Apply is on the stack" airtight.
        if (_applying) return;

        _applying = true;
        try
        {
            var scale = scaleProvider.Current;

            if (positionOnly)
                PanelWindowCoordinates.WriteDesktopPosition(window, boundsWindowSpace.TopLeft, scale);
            else
                PanelWindowCoordinates.WriteDesktopBounds(window, boundsWindowSpace, scale);

            // m-1 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2, code-review): Math.Round,
            // not a truncating (int) cast — see WindowManager.WriteModelGeometry's remarks for why a plain
            // cast (asymmetric toward zero) disagreed with LassoPanelGeometry's own Math.Round for negative
            // DIP coordinates. Model geometry is desktop-DIP, so it is written from boundsWindowSpace
            // (the parameter's real, desktop-DIP contents — see this type's remarks) directly, never from
            // whatever PanelWindowCoordinates just translated onto the window.
            model.Position = new Point((int)Math.Round(boundsWindowSpace.X), (int)Math.Round(boundsWindowSpace.Y));

            if (!positionOnly)
                model.Size = new Size((int)Math.Round(boundsWindowSpace.Width), (int)Math.Round(boundsWindowSpace.Height));

            if (span is { } s)
            {
                model.WidthSpanUnits = s.WidthUnits;
                model.HeightSpanUnits = s.HeightUnits;
            }

            if (monitorDeviceId is not null)
                model.MonitorDeviceId = monitorDeviceId;
        }
        finally
        {
            _applying = false;
        }
    }
}
