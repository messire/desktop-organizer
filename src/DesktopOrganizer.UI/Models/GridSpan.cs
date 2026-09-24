namespace DesktopOrganizer.UI.Models;

/// <summary>
/// A <see cref="PanelGeometryMode.GridAligned"/> panel's SIZE (never its position — see remarks)
/// expressed as an integer count of workspace grid units (ADR-028 "Spans, а не абсолютные размеры")
/// instead of raw DIP. Storing spans rather than DIP is what makes a <see cref="PanelGeometryMode.GridAligned"/>
/// panel's SIZE resilient to the workspace-grid origin moving (taskbar resize/auto-hide toggle, DPI
/// change, monitor hotplug): a panel with a given <c>(WidthUnits, HeightUnits)</c> occupies the exact
/// same DIP extent after <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> is re-evaluated against a new
/// origin, with no explicit migration step (see ADR-028, "Origin для системы B").
/// </summary>
/// <remarks>
/// <b>Fix-cycle 1, MAJOR-5(a) doc fix.</b> An earlier revision of this comment described
/// <see cref="GridSpan"/> as also carrying POSITION ("a panel sitting in slot
/// <c>(WidthUnits, HeightUnits)</c>") — that was never accurate: this type has no X/Y slot fields, only
/// <see cref="WidthUnits"/>/<see cref="HeightUnits"/> (a size). The POSITION-side counterpart —
/// converting a DIP position to/from whole (column, row) SLOT indices relative to an origin, the same way
/// this type converts a DIP SIZE to/from whole units — is <see cref="WorkspaceLayoutPolicy.PositionToSlot"/>/
/// <see cref="WorkspaceLayoutPolicy.SlotToPosition"/>, which did not exist when the earlier comment was
/// written; they return a plain <c>(int Col, int Row)</c> tuple, not this type, precisely because a slot
/// pair and a size are different concepts that happen to share the same "whole units from an origin"
/// shape.
/// </remarks>
/// <param name="WidthUnits">
/// Width, in whole workspace grid units (<see cref="WorkspaceGridMetrics.UnitSize"/> DIP each). Always
/// <c>&gt;= 1</c> for a span that has ever gone through <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/>
/// or <see cref="WorkspaceLayoutPolicy.SnapBounds(System.Windows.Rect,System.Windows.Point,double,double,GridSpan)"/>,
/// but this type itself does not enforce that — it is a plain data carrier, same as
/// <see cref="DesktopOrganizer.UI.Interaction.ResizeRawExtent"/>.
/// </param>
/// <param name="HeightUnits">Height, in whole workspace grid units. Same caveats as <paramref name="WidthUnits"/>.</param>
public readonly record struct GridSpan(int WidthUnits, int HeightUnits);
