using System.Windows;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.Theming;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Single source of truth for seeding a newly created <see cref="ChildWindowModel"/>'s
/// appearance/workspace-grid defaults from <see cref="IAppSettings"/> (fix-cycle 1 MINOR finding,
/// docs/PLANS/workspace-grid-migration.md §10.5). Before this type existed, the same seven defaults
/// were assigned independently by <c>App.xaml.cs</c>'s <c>NewPanelWithDefaults</c> (lasso gesture) and
/// <c>MainWindowViewModel.AddPanel</c> (toolbar/tray action) — two copies of the same list that could
/// silently drift apart. Both call sites now go through this one type instead.
/// </summary>
public static class PanelDefaults
{
    /// <summary>
    /// Creates a brand-new panel model with its title, page assignment, and every appearance/
    /// workspace-mode default already applied (fix-cycle finding 11,
    /// docs/prompts/settings-window-redesign.md): the single source of truth both
    /// <c>MainWindowViewModel.AddPanel</c> (toolbar/tray "Add panel") and <c>App.xaml.cs</c>'s
    /// <c>NewPanelWithDefaults</c> (lasso gesture) now call, so page assignment and appearance defaults
    /// can never drift apart into two independently maintained copies again — <see cref="Apply"/> already
    /// closed that gap for the appearance half alone. Never assigns <see cref="ChildWindowModel.Position"/>/
    /// <see cref="ChildWindowModel.Size"/> — same contract as <see cref="Apply"/>; call
    /// <see cref="ReconcileGridAlignedSpans"/> afterwards once the caller has finished with those, if it
    /// assigns them at all.
    /// </summary>
    /// <param name="settings">The global application settings providing the defaults.</param>
    /// <param name="pageId">The page the new panel is created on.</param>
    /// <returns>The newly created, not-yet-registered panel model.</returns>
    public static ChildWindowModel CreateNew(IAppSettings settings, string pageId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var model = new ChildWindowModel { Title = "New Panel", PageId = pageId };
        Apply(model, settings);
        return model;
    }

    /// <summary>
    /// Applies every appearance/workspace-mode default from <paramref name="settings"/> onto
    /// <paramref name="model"/>: accent/text colour, surface opacity/style, icon size, and the
    /// <see cref="ChildWindowModel.GeometryMode"/>/<see cref="ChildWindowModel.IconLayoutPolicy"/> pair
    /// (ADR-028/ADR-029). Never touches <see cref="ChildWindowModel.Position"/>/
    /// <see cref="ChildWindowModel.Size"/> — call <see cref="ReconcileGridAlignedSpans"/> afterwards,
    /// once the caller has finished assigning its own <c>Position</c>/<c>Size</c> (if it assigns them at
    /// all), to make a <see cref="PanelGeometryMode.GridAligned"/> panel's spans self-consistent
    /// (MAJOR-1(b), see that method's remarks).
    /// </summary>
    /// <param name="model">The newly created panel model to seed.</param>
    /// <param name="settings">The global application settings providing the defaults.</param>
    public static void Apply(ChildWindowModel model, IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);

        // ResolveDefaultAccent (not the raw value) — CRITICAL-1 (ADR-025 D-10, DO-014):
        // Settings.settings still ships DefaultBackgroundColor="#1E1E1E" (schema v1-v3's legacy
        // default), which renders as a near-invisible accent through the Ambient Accent system;
        // substitute AccentPalette.Neutral for that ONE legacy value, exactly as V3ToV4Migration
        // already does for existing panels.
        model.AccentColor = AccentPalette.ResolveDefaultAccent(settings.DefaultBackgroundColor);
        model.TextColor = settings.DefaultTextColor;
        model.SurfaceOpacity = settings.DefaultOpacity;
        model.SurfaceStyle = PanelSurfaceStyleParser.ParseOrAmbient(settings.DefaultSurfaceStyle);
        model.IconSize = settings.DefaultIconSize;

        // ADR-028/ADR-029 (docs/PLANS/workspace-grid-migration.md): new panels seed from the global
        // defaults; existing panels are never affected by these settings — they keep whatever the
        // v4->v5 migration assigned them.
        model.GeometryMode = PanelGeometryModeParser.ParseOrLegacyFreeform(settings.DefaultPanelGeometryMode);
        model.IconLayoutPolicy = IconLayoutPolicyParser.ParseOrLegacyStretch(settings.DefaultIconLayoutPolicy);
    }

    /// <summary>
    /// MAJOR-1(b) fix-cycle 1 (docs/PLANS/workspace-grid-migration.md §10.5): must be called once, after
    /// the caller has finished assigning <see cref="ChildWindowModel.Position"/>/
    /// <see cref="ChildWindowModel.Size"/> (if it assigns them at all — the plain "Add Panel" action
    /// never does), for every panel <see cref="Apply"/> just set to
    /// <see cref="PanelGeometryMode.GridAligned"/>. A no-op for <see cref="PanelGeometryMode.LegacyFreeform"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Before this fix, both creation call sites could persist
    /// <c>geometryMode: "GridAligned"</c> with <c>widthSpanUnits</c>/<c>heightSpanUnits</c> left
    /// <see langword="null"/> — the panel's actual on-screen bounds were never on the workspace grid,
    /// only its label claimed they were (code-review MAJOR-1). <see cref="PanelStateMapper.FromDto"/>
    /// now defends already-written broken documents by normalizing them back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> on load — this method is the write-side half:
    /// stop creating new broken documents in the first place.
    /// </para>
    /// <para>
    /// <b>Decision on MAJOR-1(b) — size only, never position.</b> A true grid-aligned <em>position</em>
    /// requires the target monitor's work-area origin (ADR-028 "Origin для системы B"), which today is
    /// only resolved later, in <c>WindowManager.ResolveMonitorPlacement</c> — called from
    /// <c>WindowManager.CreateChildWindow</c>, i.e. strictly after both creation call sites finish
    /// building the model — and that resolution is itself gated on ADR-028's still-open Question 2 (the
    /// undocumented <c>Window.Left/Top</c> coordinate space after <c>SetParent(WorkerW)</c> on a monitor
    /// with negative coordinates; manual verify required before writing origin-based coordinates).
    /// Reaching into <c>WindowManager</c>'s monitor-resolution flow from here — or duplicating it — is
    /// out of this fix-cycle's zone (persistence/DTO/mapper/creation-defaults only) and would pre-empt
    /// that still-open architectural question.
    /// </para>
    /// <para>
    /// <b>Consequence, accepted here.</b> A freshly created <see cref="PanelGeometryMode.GridAligned"/>
    /// panel gets a real, self-consistent <see cref="ChildWindowModel.WidthSpanUnits"/>/
    /// <see cref="ChildWindowModel.HeightSpanUnits"/> pair describing its
    /// <see cref="ChildWindowModel.Size"/> — satisfying <see cref="PanelStateMapper.FromDto"/>'s
    /// validity guard, so the panel is never silently demoted back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> on the very next load — but its
    /// <see cref="ChildWindowModel.Position"/> is left exactly as the caller set it (or the CLR
    /// default), not yet snapped to any grid line. TODO(P03): once stage 03 wires
    /// <c>WorkspaceLayoutPolicy.SnapPosition</c> into panel creation with a resolved monitor origin (or
    /// the two creation call sites move to run after monitor resolution), also snap <c>Position</c>
    /// here. Until then this half-consistent state (valid spans, unaligned position) is no worse than
    /// what the "Align to workspace grid" command already tolerates for any manually-dragged
    /// <see cref="PanelGeometryMode.GridAligned"/> panel before that command runs once.
    /// </para>
    /// <para>
    /// <b>Size source.</b> When the caller already assigned a non-degenerate <c>Size</c> (e.g. the lasso
    /// gesture's drawn rectangle), that size is quantised up to the smallest whole span that covers it
    /// (<see cref="WorkspaceLayoutPolicy.BoundsToSpans"/>, <see cref="Math.Ceiling(double)"/> — never
    /// smaller than what the user drew) and <c>Size</c> is replaced with that span's exact DIP size
    /// (<see cref="WorkspaceLayoutPolicy.SpansToBounds"/>) so the two never drift apart. When the caller
    /// leaves <c>Size</c> at its CLR default (the plain "Add Panel" action, which relies on
    /// <c>ChildWindow.xaml</c>'s own default width/height instead), <see cref="PanelMetrics.DefaultPanelSize"/>
    /// — the exact same 260x300 DIP default a <see cref="PanelGeometryMode.LegacyFreeform"/> "Add Panel"
    /// gets from <c>ChildWindow.xaml</c> itself — is used as the seed instead of a bare <c>(0,0)</c>,
    /// which would otherwise quantise to a degenerate 1x1 span.
    /// </para>
    /// <para>
    /// <b>MINOR N-6 fix, fix-cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8).</b> Before this fix,
    /// the seed here was <see cref="PanelSizePreset.Medium"/> (248x232 DIP) — a DIFFERENT, smaller size
    /// than the 260x300 DIP a <see cref="PanelGeometryMode.LegacyFreeform"/> "Add Panel" gets, so the two
    /// geometry modes silently disagreed on what "Add Panel"'s default size even is. Seeding from
    /// <see cref="PanelMetrics.DefaultPanelSize"/> instead — the same constant <c>ChildWindow.xaml</c>'s
    /// own <c>Window.Width</c>/<c>Window.Height</c> now bind to (N-6's other half) — makes both geometry
    /// modes agree again; quantising 260x300 up to the nearest whole span (<see cref="WorkspaceLayoutPolicy.BoundsToSpans"/>)
    /// gives 264x304 DIP, the nearest span-extent (see <c>PanelDefaultsTests</c> for the pinned numbers).
    /// </para>
    /// <para>
    /// <b>MINOR N-5 fix, fix-cycle 2.</b> The quantised span is additionally clamped up to
    /// <see cref="WorkspaceGridMetrics.MinSpanExpanded"/> component-wise — <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/>
    /// alone never enforces that floor (only <see cref="WorkspaceLayoutPolicy.SnapBounds(System.Windows.Rect,System.Windows.Point,double,double,GridSpan)"/>,
    /// the "Align to workspace grid" command's own math, composes the two). Without this, an explicit but
    /// tiny <c>Size</c> (e.g. a very small lasso rectangle) could seed a new <see cref="PanelGeometryMode.GridAligned"/>
    /// panel below the documented minimum — the exact class of inconsistency <see cref="PanelStateMapper.FromDto"/>'s
    /// MAJOR-1(a) guard already defends on the READ side; this closes the equivalent gap on this WRITE side.
    /// </para>
    /// </remarks>
    /// <param name="model">The panel model whose <see cref="ChildWindowModel.WidthSpanUnits"/>/<see cref="ChildWindowModel.HeightSpanUnits"/> are reconciled with its <see cref="ChildWindowModel.Size"/>.</param>
    public static void ReconcileGridAlignedSpans(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.GeometryMode != PanelGeometryMode.GridAligned) return;

        var currentSize = model.Size;
        var seedSize = currentSize is { Width: > 0, Height: > 0 }
            ? new Size(currentSize.Width, currentSize.Height)
            : PanelMetrics.DefaultPanelSize;

        var rawSpan = WorkspaceLayoutPolicy.BoundsToSpans(seedSize, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);
        var span = new GridSpan(
            Math.Max(rawSpan.WidthUnits, WorkspaceGridMetrics.MinSpanExpanded.WidthUnits),
            Math.Max(rawSpan.HeightUnits, WorkspaceGridMetrics.MinSpanExpanded.HeightUnits));
        var snappedSize = WorkspaceLayoutPolicy.SpansToBounds(span, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);

        model.WidthSpanUnits = span.WidthUnits;
        model.HeightSpanUnits = span.HeightUnits;
        model.Size = new DrawingSize((int)Math.Round(snappedSize.Width), (int)Math.Round(snappedSize.Height));
    }
}
