namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// Snapshot of every value <see cref="ResizeGeometryCalculator.Apply"/> needs besides the gesture's own
/// raw extent — computed once per resize gesture (<c>ResizeConstraintsFactory.BuildConstraints</c>, mirrors
/// the <c>_snapCellWidth</c>/<c>_snapCellHeight</c> fields set once in the pre-extraction
/// <c>OnResizeDragStarted</c>) and reused, unchanged, for every subsequent delta of that gesture.
/// docs/PLANS/phase-1-resize-stabilization.md §2.4.
/// </summary>
/// <param name="MinWidth">
/// Grid mode: <c>GridMetrics.MinPanelWidth(iconSize, iconLayoutPolicy, verticalScrollBarWidth)</c> (the
/// policy-aware overload, ADR-029 Решение 4/M-2 — reduces to the bare <c>MinPanelWidth(iconSize)</c>
/// value for <c>IconLayoutPolicy.LegacyStretch</c>). List mode: <c>GridMetrics.MinPanelSize</c> (the two
/// floors are deliberately different — ADR-018).
/// </param>
/// <param name="MinHeight">Always <c>GridMetrics.MinPanelSize</c> in both view modes.</param>
/// <param name="SnapCellWidth"><c>0</c> means the width axis is free-form (pixel-accurate, no quantization).</param>
/// <param name="SnapCellHeight"><c>0</c> means the height axis is free-form (pixel-accurate, no quantization).</param>
/// <param name="TitleBarHeight"><c>ChildWindow.TitleBarHeight</c> — chrome stripped before quantising height.</param>
/// <param name="HorizontalChrome">
/// <c>GridMetrics.ContentHorizontalChrome(iconLayoutPolicy, verticalScrollBarWidth)</c> (ADR-029 Решение 4/
/// F-3/M-2) — chrome stripped before quantising width; <c>= GridMetrics.FileListHPadding</c> for
/// <c>IconLayoutPolicy.LegacyStretch</c> (no M-2 reservation), and for List mode/a <see langword="null"/>
/// model (<see cref="Behaviors.ResizeConstraintsFactory.BuildConstraints"/> resolves both to
/// <c>LegacyStretch</c> before computing this field, ADR-029 Решение 5).
/// </param>
/// <param name="VerticalChrome"><c>GridMetrics.FileListVPadding</c> — chrome stripped before quantising height.</param>
/// <param name="SizeGridStep">
/// ADR-011 Amendment 2026-09-24 (resize extension): the workspace-panel grid step
/// (<c>IAppSettings.GridSize</c>, ADR-011) a resize gesture's WIDTH/HEIGHT quantise to, in DIP —
/// <c>0</c> (the default, preserving every pre-existing caller/test bit-for-bit) means "not active for
/// this gesture", the same "<c>0</c> means free-form" convention <see cref="SnapCellWidth"/>/
/// <see cref="SnapCellHeight"/> already use. When positive, <see cref="ResizeGeometryCalculator.Apply"/>
/// quantises the RAW window <c>Width</c>/<c>Height</c> directly to the nearest multiple of this value
/// (via <see cref="Models.GridSizeNormalizer.NormalizeExtent"/> — reused, not duplicated; round-half-up,
/// unconditional, unlike <see cref="Models.GridSnap.SnapAxis"/>'s threshold magnet) IN PLACE OF the
/// icon-grid snap <see cref="SnapCellWidth"/>/<see cref="SnapCellHeight"/> would otherwise apply —
/// <see cref="Behaviors.ResizeConstraintsFactory.BuildConstraints"/> never sets both a non-zero
/// <see cref="SizeGridStep"/> and a non-zero <see cref="SnapCellWidth"/>/<see cref="SnapCellHeight"/> at
/// the same time, but <see cref="ResizeGeometryCalculator.Apply"/> also checks <see cref="SizeGridStep"/>
/// FIRST as defense in depth. Unlike <see cref="SnapCellWidth"/>/<see cref="SnapCellHeight"/> (icon-grid
/// content geometry, stripped of chrome before quantising), this value quantises the WHOLE window
/// extent directly — <c>GridSize</c> is a whole-panel concept (the same one
/// <see cref="Models.GridSizeNormalizer"/>/<c>WindowManager</c> already apply to a panel's persisted
/// bounds at restore), unrelated to icon-grid chrome.
/// </param>
public readonly record struct ResizeConstraints(
    double MinWidth,
    double MinHeight,
    double SnapCellWidth,
    double SnapCellHeight,
    double TitleBarHeight,
    double HorizontalChrome,
    double VerticalChrome,
    double SizeGridStep = 0d);
