using System.Windows;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Immutable snapshot of how the desktop layer maps physical screen pixels to the single
/// <b>desktop-DIP</b> space the model (<c>ChildWindowModel</c>, <see cref="MonitorInfo"/>, persisted
/// geometry) lives in (release-hardening F §2, ADR-037). Pure: no P/Invoke, no WPF window access —
/// built by <see cref="IDesktopScaleProvider"/> from native measurements and testable on its own.
/// </summary>
/// <remarks>
/// <para><b>Notation.</b> <c>px</c> — physical pixels; <c>s_M</c> — effective DPI of monitor M / 96;
/// <c>s_ref</c> — system DPI / 96 (<see cref="ReferenceScale"/>, exactly what 0.6.1 divided by);
/// <c>s_L</c> — DPI of the panels' root window / 96 (<see cref="LayerScale"/> in <see cref="DesktopLayerMode.Embedded"/>);
/// <c>s_w</c> — the current DPI scale of a particular WPF window.</para>
/// <para><b>Uniform snapshots</b> (<see cref="IsUniform"/>): Embedded mode (one scale <c>s_L</c> for the
/// whole layer), a top-level snapshot whose monitors all share one scale, or the overlap fallback below.
/// Every conversion then is literally <c>px / s</c> / <c>dip * s</c> — the very expression 0.6.1's
/// <c>MonitorService</c> used (<c>r.Left / scale</c>, <c>r.Width / scale</c>), never
/// <c>origin + (px − origin) / s</c> — so single-scale configurations produce bit-identical numbers and
/// no persisted geometry moves (plan §2 "Инвариант неизменности persistence").</para>
/// <para><b>Anchored snapshots</b> (top-level mode, monitors with different scales): each monitor maps
/// piecewise-linearly, <c>dip = dipOrigin(M) + (px − pxOrigin(M)) / s_M</c>. The DIP origin is anchored
/// on the edge facing the primary monitor P: along X, a monitor lying entirely left of P
/// (<c>R ≤ P.L</c>) keeps its right edge (<c>dipR = R / s_ref</c>, <c>dipL = dipR − (R − L) / s_M</c>),
/// any other monitor keeps its left edge (<c>dipL = L / s_ref</c>); along Y the same with
/// <c>B ≤ P.T</c> and the bottom edge. If the anchored DIP rectangles still overlap (exotic 3+ monitor
/// layouts) the snapshot degrades to the uniform <c>px / s_ref</c> mapping (0.6.1 geometry) and flags
/// <see cref="IsAnchorOverlapFallback"/>.</para>
/// <para><b>Manual verify (Win10/Win11):</b> the anchoring rule is the plan's default (QF-1); whether it
/// matches the coordinates a system-aware 0.6.1 process saw on mixed-DPI setups (experiment E6) is
/// still unmeasured — see docs/PLANS/release-hardening-1.0-F-dpi.md §6 F1.0.</para>
/// </remarks>
public sealed class DesktopScaleModel
{
    /// <summary>
    /// The DPI that corresponds to a scale of <c>1.0</c> (100 %). The ONLY place the literal 96 may take
    /// part in arithmetic (guard test <c>DpiLiteralGuardTests</c>, plan §7).
    /// </summary>
    public const double BaseDpi = 96d;

    /// <summary>
    /// Tolerance used when testing anchored DIP rectangles for overlap, so rounding noise on a shared
    /// edge is not mistaken for an overlap.
    /// </summary>
    private const double OverlapEpsilon = 1e-6;

    private readonly MonitorMapping[] _mappings;

    private DesktopScaleModel(
        DesktopLayerMode mode,
        double layerScale,
        double referenceScale,
        IReadOnlyList<MonitorScale> monitors,
        bool isUniform,
        double uniformScale,
        bool isAnchorOverlapFallback,
        MonitorMapping[] mappings)
    {
        Mode = mode;
        LayerScale = layerScale;
        ReferenceScale = referenceScale;
        Monitors = monitors;
        IsUniform = isUniform;
        UniformScale = uniformScale;
        IsAnchorOverlapFallback = isAnchorOverlapFallback;
        _mappings = mappings;
    }

    /// <summary>Gets the desktop layer mode this snapshot was built for.</summary>
    public DesktopLayerMode Mode { get; }

    /// <summary>
    /// Gets the scale of the desktop layer: in <see cref="DesktopLayerMode.Embedded"/> the DPI scale of
    /// the panels' root window (<c>WorkerW</c>); otherwise equal to <see cref="ReferenceScale"/>.
    /// </summary>
    public double LayerScale { get; }

    /// <summary>Gets the system DPI scale (<c>s_ref</c>) — the coefficient 0.6.1 divided every rectangle by.</summary>
    public double ReferenceScale { get; }

    /// <summary>
    /// Gets a value indicating whether the whole desktop maps with one scale (<see cref="UniformScale"/>):
    /// Embedded mode, all monitors at the same scale, or <see cref="IsAnchorOverlapFallback"/>.
    /// </summary>
    public bool IsUniform { get; }

    /// <summary>
    /// Gets the single px/DIP scale used when <see cref="IsUniform"/>: <see cref="LayerScale"/> in
    /// Embedded mode, the common monitor scale when all monitors share one, <see cref="ReferenceScale"/>
    /// in the overlap fallback or without monitors. Meaningless (equal to <see cref="ReferenceScale"/>)
    /// for an anchored snapshot.
    /// </summary>
    public double UniformScale { get; }

    /// <summary>
    /// Gets a value indicating whether anchoring produced overlapping DIP monitor rectangles and the
    /// snapshot therefore fell back to the uniform <c>px / s_ref</c> mapping.
    /// </summary>
    public bool IsAnchorOverlapFallback { get; }

    /// <summary>Gets the monitors of this snapshot (sanitized scales), in enumeration order.</summary>
    public IReadOnlyList<MonitorScale> Monitors { get; }

    /// <summary>
    /// Converts a DPI value into a scale factor (<c>dpi / 96</c>).
    /// </summary>
    /// <param name="dpi">The DPI value; <c>0</c> means "unknown".</param>
    /// <param name="fallback">Returned when <paramref name="dpi"/> is <c>0</c>.</param>
    /// <returns>The scale, <c>1.0</c> at 96 DPI.</returns>
    public static double ScaleFromDpi(uint dpi, double fallback = 1d) => dpi > 0 ? dpi / BaseDpi : fallback;

    /// <summary>
    /// Builds a snapshot. Invalid scales (non-positive, NaN, infinite) are sanitized: the reference scale
    /// to <c>1.0</c>, the layer scale and monitor scales to the reference scale.
    /// </summary>
    /// <param name="mode">The desktop layer mode. <see cref="DesktopLayerMode.Unknown"/> maps like <see cref="DesktopLayerMode.TopLevel"/>.</param>
    /// <param name="layerScale">DPI scale of the panels' root window (used in Embedded mode only).</param>
    /// <param name="referenceScale">System DPI scale (<c>s_ref</c>).</param>
    /// <param name="monitors">The monitors, physical pixels plus effective scale.</param>
    /// <returns>The snapshot.</returns>
    public static DesktopScaleModel Create(
        DesktopLayerMode mode,
        double layerScale,
        double referenceScale,
        IReadOnlyList<MonitorScale> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var reference = IsValidScale(referenceScale) ? referenceScale : 1d;
        var sanitized = monitors
            .Where(m => m is not null)
            .Select(m => IsValidScale(m.Scale) ? m : m with { Scale = reference })
            .ToArray();
        var readOnlyMonitors = Array.AsReadOnly(sanitized);

        if (mode == DesktopLayerMode.Embedded)
        {
            var layer = IsValidScale(layerScale) ? layerScale : reference;
            return CreateUniform(mode, layer, reference, readOnlyMonitors, sanitized, layer, isAnchorOverlapFallback: false);
        }

        if (sanitized.Length == 0)
            return CreateUniform(mode, reference, reference, readOnlyMonitors, sanitized, reference, isAnchorOverlapFallback: false);

        var common = sanitized[0].Scale;
        if (sanitized.All(m => m.Scale.Equals(common)))
            return CreateUniform(mode, reference, reference, readOnlyMonitors, sanitized, common, isAnchorOverlapFallback: false);

        var anchored = Anchor(sanitized, reference);
        if (HasOverlap(anchored))
            return CreateUniform(mode, reference, reference, readOnlyMonitors, sanitized, reference, isAnchorOverlapFallback: true);

        return new DesktopScaleModel(mode, reference, reference, readOnlyMonitors, isUniform: false, reference, isAnchorOverlapFallback: false, anchored);
    }

    /// <summary>
    /// Returns whether <paramref name="other"/> describes exactly the same mapping (same mode, scales and
    /// monitors) — the provider raises <see cref="IDesktopScaleProvider.Changed"/> only when this is false.
    /// </summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when both snapshots are equivalent.</returns>
    public bool IsEquivalentTo(DesktopScaleModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Mode == other.Mode
               && LayerScale.Equals(other.LayerScale)
               && ReferenceScale.Equals(other.ReferenceScale)
               && Monitors.SequenceEqual(other.Monitors);
    }

    /// <summary>
    /// The px→desktop-DIP ratio on the given monitor: <see cref="LayerScale"/> in Embedded mode,
    /// <see cref="UniformScale"/> for any uniform snapshot, the monitor's own scale when anchored.
    /// Unknown device → <see cref="UniformScale"/>.
    /// </summary>
    /// <param name="deviceName">Monitor device name.</param>
    /// <returns>The scale.</returns>
    public double GetGeometryScale(string deviceName)
    {
        if (IsUniform) return UniformScale;
        return TryFindMapping(deviceName, out var mapping) ? mapping.Scale : UniformScale;
    }

    /// <summary>
    /// The scale a top-level window (sidebar, customize window, dialogs) gets on the given monitor — always
    /// the monitor's effective scale, in every mode. Unknown device → <see cref="ReferenceScale"/>.
    /// </summary>
    /// <param name="deviceName">Monitor device name.</param>
    /// <returns>The scale.</returns>
    public double GetTopLevelScale(string deviceName)
        => TryFindMapping(deviceName, out var mapping) ? mapping.Monitor.Scale : ReferenceScale;

    /// <summary>
    /// The full monitor rectangle in desktop-DIP. Unknown device → <see cref="Rect.Empty"/>.
    /// </summary>
    /// <param name="deviceName">Monitor device name.</param>
    /// <returns>The rectangle.</returns>
    public Rect GetBoundsDip(string deviceName)
        => TryFindMapping(deviceName, out var mapping) ? MapPxRect(mapping, mapping.Monitor.BoundsPx) : Rect.Empty;

    /// <summary>
    /// The monitor work area in desktop-DIP. Unknown device → <see cref="Rect.Empty"/>.
    /// </summary>
    /// <param name="deviceName">Monitor device name.</param>
    /// <returns>The rectangle.</returns>
    public Rect GetWorkAreaDip(string deviceName)
        => TryFindMapping(deviceName, out var mapping) ? MapPxRect(mapping, mapping.Monitor.WorkAreaPx) : Rect.Empty;

    /// <summary>
    /// Converts a physical-pixel screen point to desktop-DIP, through the monitor containing it
    /// (otherwise the nearest monitor).
    /// </summary>
    /// <param name="x">Horizontal screen coordinate, physical pixels.</param>
    /// <param name="y">Vertical screen coordinate, physical pixels.</param>
    /// <returns>The desktop-DIP point.</returns>
    public Point PxToDip(double x, double y)
    {
        if (IsUniform) return new Point(x / UniformScale, y / UniformScale);

        var mapping = FindByPxPoint(x, y);
        return new Point(
            mapping.DipLeft + ((x - mapping.Monitor.BoundsPx.Left) / mapping.Scale),
            mapping.DipTop + ((y - mapping.Monitor.BoundsPx.Top) / mapping.Scale));
    }

    /// <summary>
    /// Converts a physical-pixel screen rectangle to desktop-DIP, through the monitor it intersects most
    /// (tie → primary; no intersection → the monitor nearest to its centre). The size is divided by that
    /// monitor's scale.
    /// </summary>
    /// <param name="px">The rectangle in physical pixels.</param>
    /// <returns>The desktop-DIP rectangle; <see cref="Rect.Empty"/> for an empty input.</returns>
    public Rect PxToDip(Rect px)
    {
        if (px.IsEmpty) return Rect.Empty;
        if (IsUniform) return new Rect(px.X / UniformScale, px.Y / UniformScale, px.Width / UniformScale, px.Height / UniformScale);

        var mapping = FindByRect(px, dipSpace: false);
        return new Rect(
            mapping.DipLeft + ((px.X - mapping.Monitor.BoundsPx.Left) / mapping.Scale),
            mapping.DipTop + ((px.Y - mapping.Monitor.BoundsPx.Top) / mapping.Scale),
            px.Width / mapping.Scale,
            px.Height / mapping.Scale);
    }

    /// <summary>
    /// Converts a desktop-DIP point to physical screen pixels, through the monitor whose DIP rectangle
    /// contains it (otherwise the nearest one).
    /// </summary>
    /// <param name="dip">The desktop-DIP point.</param>
    /// <returns>The point in physical pixels (not rounded).</returns>
    public Point DipToPx(Point dip)
    {
        if (IsUniform) return new Point(dip.X * UniformScale, dip.Y * UniformScale);

        var mapping = FindByDipPoint(dip.X, dip.Y);
        return new Point(
            mapping.Monitor.BoundsPx.Left + ((dip.X - mapping.DipLeft) * mapping.Scale),
            mapping.Monitor.BoundsPx.Top + ((dip.Y - mapping.DipTop) * mapping.Scale));
    }

    /// <summary>
    /// Converts a desktop-DIP rectangle to physical screen pixels, through the monitor whose DIP rectangle
    /// it intersects most (tie → primary; no intersection → nearest to its centre).
    /// </summary>
    /// <param name="dip">The desktop-DIP rectangle.</param>
    /// <returns>The rectangle in physical pixels (not rounded); <see cref="Rect.Empty"/> for an empty input.</returns>
    public Rect DipToPx(Rect dip)
    {
        if (dip.IsEmpty) return Rect.Empty;
        if (IsUniform) return new Rect(dip.X * UniformScale, dip.Y * UniformScale, dip.Width * UniformScale, dip.Height * UniformScale);

        var mapping = FindByRect(dip, dipSpace: true);
        return new Rect(
            mapping.Monitor.BoundsPx.Left + ((dip.X - mapping.DipLeft) * mapping.Scale),
            mapping.Monitor.BoundsPx.Top + ((dip.Y - mapping.DipTop) * mapping.Scale),
            dip.Width * mapping.Scale,
            dip.Height * mapping.Scale);
    }

    /// <summary>
    /// Converts a panel window's WPF bounds (<c>Left/Top/Width/Height</c>) into desktop-DIP. Embedded:
    /// identity. Top-level: the window's px position is <c>Left · s_w</c>, mapped through
    /// <see cref="PxToDip(Rect)"/>; the size is returned unchanged (WPF DIP size = desktop-DIP size on the
    /// window's own monitor). Uniform with <c>s_w</c> equal to <see cref="UniformScale"/>: identity.
    /// </summary>
    /// <param name="windowBounds">The window's <c>Left/Top/Width/Height</c>.</param>
    /// <param name="windowScale">The window's current DPI scale (<c>s_w</c>); invalid → <see cref="UniformScale"/>.</param>
    /// <returns>The desktop-DIP rectangle.</returns>
    public Rect WindowToDesktop(Rect windowBounds, double windowScale)
    {
        if (Mode == DesktopLayerMode.Embedded || windowBounds.IsEmpty) return windowBounds;

        var ws = IsValidScale(windowScale) ? windowScale : UniformScale;
        if (IsUniform)
        {
            if (ws.Equals(UniformScale)) return windowBounds;
            return new Rect(windowBounds.X * ws / UniformScale, windowBounds.Y * ws / UniformScale, windowBounds.Width, windowBounds.Height);
        }

        var pxRect = new Rect(windowBounds.X * ws, windowBounds.Y * ws, windowBounds.Width * ws, windowBounds.Height * ws);
        var mapping = FindByRect(pxRect, dipSpace: false);
        return new Rect(
            mapping.DipLeft + ((pxRect.X - mapping.Monitor.BoundsPx.Left) / mapping.Scale),
            mapping.DipTop + ((pxRect.Y - mapping.Monitor.BoundsPx.Top) / mapping.Scale),
            windowBounds.Width,
            windowBounds.Height);
    }

    /// <summary>
    /// Converts a desktop-DIP rectangle into the WPF <c>Left/Top</c> a panel window must be given.
    /// Embedded: identity. Top-level: <c>DipToPx(rect).TopLeft / s_w</c>. Uniform with <c>s_w</c> equal
    /// to <see cref="UniformScale"/>: identity.
    /// </summary>
    /// <param name="desktopBounds">The desktop-DIP rectangle (model geometry).</param>
    /// <param name="windowScale">The window's current DPI scale (<c>s_w</c>); invalid → <see cref="UniformScale"/>.</param>
    /// <returns>The window's <c>Left/Top</c>.</returns>
    public Point DesktopToWindow(Rect desktopBounds, double windowScale)
    {
        if (desktopBounds.IsEmpty) return new Point(0, 0);
        if (Mode == DesktopLayerMode.Embedded) return desktopBounds.TopLeft;

        var ws = IsValidScale(windowScale) ? windowScale : UniformScale;
        if (IsUniform)
        {
            if (ws.Equals(UniformScale)) return desktopBounds.TopLeft;
            return new Point(desktopBounds.X * UniformScale / ws, desktopBounds.Y * UniformScale / ws);
        }

        var px = DipToPx(desktopBounds);
        return new Point(px.X / ws, px.Y / ws);
    }

    private static bool IsValidScale(double scale) => scale > 0 && double.IsFinite(scale);

    private static DesktopScaleModel CreateUniform(
        DesktopLayerMode mode,
        double layerScale,
        double referenceScale,
        IReadOnlyList<MonitorScale> monitors,
        MonitorScale[] sanitized,
        double uniformScale,
        bool isAnchorOverlapFallback)
    {
        // Uniform mappings keep the per-monitor record only for name lookups; every conversion takes
        // the px / s fast path and never reads DipLeft/DipTop.
        var mappings = sanitized
            .Select(m => new MonitorMapping(m, m.BoundsPx.Left / uniformScale, m.BoundsPx.Top / uniformScale, uniformScale))
            .ToArray();
        return new DesktopScaleModel(mode, layerScale, referenceScale, monitors, isUniform: true, uniformScale, isAnchorOverlapFallback, mappings);
    }

    private static MonitorMapping[] Anchor(MonitorScale[] monitors, double reference)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var p = primary.BoundsPx;

        return monitors
            .Select(m =>
            {
                var b = m.BoundsPx;
                var dipLeft = b.Right <= p.Left
                    ? (b.Right / reference) - (b.Width / m.Scale)
                    : b.Left / reference;
                var dipTop = b.Bottom <= p.Top
                    ? (b.Bottom / reference) - (b.Height / m.Scale)
                    : b.Top / reference;
                return new MonitorMapping(m, dipLeft, dipTop, m.Scale);
            })
            .ToArray();
    }

    private static bool HasOverlap(MonitorMapping[] mappings)
    {
        for (var i = 0; i < mappings.Length; i++)
        {
            var a = mappings[i].DipBounds;
            for (var j = i + 1; j < mappings.Length; j++)
            {
                var b = mappings[j].DipBounds;
                var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (w > OverlapEpsilon && h > OverlapEpsilon) return true;
            }
        }

        return false;
    }

    private static double IntersectionArea(Rect a, Rect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static double DistanceSquared(Rect r, double x, double y)
    {
        var dx = x < r.Left ? r.Left - x : x > r.Right ? x - r.Right : 0;
        var dy = y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0;
        return (dx * dx) + (dy * dy);
    }

    private static Rect ToRect(System.Drawing.Rectangle r) => new(r.Left, r.Top, r.Width, r.Height);

    private Rect MapPxRect(MonitorMapping mapping, System.Drawing.Rectangle px)
    {
        if (IsUniform)
        {
            // Bit-identical to 0.6.1 MonitorService.ToDipRect: every component divided separately.
            var s = UniformScale;
            return new Rect(px.Left / s, px.Top / s, px.Width / s, px.Height / s);
        }

        return new Rect(
            mapping.DipLeft + ((px.Left - mapping.Monitor.BoundsPx.Left) / mapping.Scale),
            mapping.DipTop + ((px.Top - mapping.Monitor.BoundsPx.Top) / mapping.Scale),
            px.Width / mapping.Scale,
            px.Height / mapping.Scale);
    }

    private bool TryFindMapping(string deviceName, out MonitorMapping mapping)
    {
        foreach (var candidate in _mappings)
        {
            if (string.Equals(candidate.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                mapping = candidate;
                return true;
            }
        }

        mapping = default;
        return false;
    }

    private MonitorMapping FindByPxPoint(double x, double y)
    {
        foreach (var mapping in _mappings)
        {
            var b = mapping.Monitor.BoundsPx;
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return mapping;
        }

        return Nearest(x, y, dipSpace: false);
    }

    private MonitorMapping FindByDipPoint(double x, double y)
    {
        foreach (var mapping in _mappings)
        {
            var b = mapping.DipBounds;
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return mapping;
        }

        return Nearest(x, y, dipSpace: true);
    }

    private MonitorMapping FindByRect(Rect rect, bool dipSpace)
    {
        MonitorMapping? best = null;
        var bestArea = 0d;

        foreach (var mapping in _mappings)
        {
            var area = IntersectionArea(rect, dipSpace ? mapping.DipBounds : ToRect(mapping.Monitor.BoundsPx));
            if (area <= 0) continue;

            // Strictly larger wins; an exact tie goes to the primary monitor, otherwise the earlier one stays.
            if (best is null || area > bestArea || (area.Equals(bestArea) && mapping.Monitor.IsPrimary && !best.Value.Monitor.IsPrimary))
            {
                best = mapping;
                bestArea = area;
            }
        }

        if (best is { } found) return found;

        var cx = rect.X + (rect.Width / 2);
        var cy = rect.Y + (rect.Height / 2);
        return Nearest(cx, cy, dipSpace);
    }

    private MonitorMapping Nearest(double x, double y, bool dipSpace)
    {
        var best = _mappings[0];
        var bestDistance = double.MaxValue;

        foreach (var mapping in _mappings)
        {
            var distance = DistanceSquared(dipSpace ? mapping.DipBounds : ToRect(mapping.Monitor.BoundsPx), x, y);
            if (distance < bestDistance || (distance.Equals(bestDistance) && mapping.Monitor.IsPrimary && !best.Monitor.IsPrimary))
            {
                best = mapping;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// One monitor's piecewise-linear mapping: <c>dip = (DipLeft, DipTop) + (px − BoundsPx.TopLeft) / Scale</c>.
    /// </summary>
    /// <param name="Monitor">The monitor.</param>
    /// <param name="DipLeft">Desktop-DIP x of the monitor's left edge.</param>
    /// <param name="DipTop">Desktop-DIP y of the monitor's top edge.</param>
    /// <param name="Scale">px per desktop-DIP on this monitor.</param>
    private readonly record struct MonitorMapping(MonitorScale Monitor, double DipLeft, double DipTop, double Scale)
    {
        /// <summary>Gets the monitor rectangle in desktop-DIP.</summary>
        public Rect DipBounds => new(DipLeft, DipTop, Monitor.BoundsPx.Width / Scale, Monitor.BoundsPx.Height / Scale);
    }
}
