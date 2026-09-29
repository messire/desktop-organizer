using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Localization;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Views;

using DrawingSize = System.Drawing.Size;

/// <summary>
/// P06 (docs/fix-prompts/06-fit-content.md, ADR-029 Решение 7): the "Fit panel to content"/"Auto-size
/// to content"/"Icon layout" context-menu actions and the <see cref="PanelSizingMode.AutoContent"/>
/// passive recompute subscriptions. A new partial, added alongside the existing per-concern partials
/// (<c>ChildWindow.Sizing.cs</c> — resize gestures/presets, P04; <c>ChildWindow.WorkspaceGrid.cs</c> —
/// "Align to workspace grid", P03) rather than folded into either: this concern reads BOTH of their
/// domains (icon-grid math AND workspace-grid geometry) but owns neither — see
/// <c>docs/PLANS/workspace-grid-06-fit-content.md</c> for the full design and interaction contract with
/// those two files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single write path.</b> Every height this partial ever applies to a NOT-rolled-up panel goes
/// through the same <see cref="Behaviors.PanelGeometryWriter"/> instance <c>ChildWindow.xaml.cs</c>'s constructor
/// already built (<c>_geometryWriter</c>) — never a direct <c>Height</c> assignment — so
/// <c>OnRenderSizeChanged</c>'s own model-write guard (<see cref="Behaviors.PanelGeometryWriter.IsApplying"/>)
/// already covers this partial for free, with no changes to that method required.
/// </para>
/// <para>
/// <b>Roll-up (ADR-029 Решение 7's trigger table).</b> Both the explicit action and the passive
/// <see cref="PanelSizingMode.AutoContent"/> recompute apply to the panel's REMEMBERED expanded height
/// (<c>_rollUpBehavior.ExpandedHeight</c> plus <see cref="Models.ChildWindowModel.Size"/>'s height
/// component) while <see cref="Models.ChildWindowModel.IsRolledUp"/> is <see langword="true"/>, never to
/// the live (collapsed) <see cref="FrameworkElement.Height"/> — the same "expanded height survives while
/// collapsed" contract <c>RollUpBehavior</c> itself already implements for a plain roll-up/roll-down
/// toggle. <c>RollUpBehavior.cs</c> itself is not modified — this partial only reads/writes its already-
/// public <see cref="Behaviors.RollUpBehavior.ExpandedHeight"/> property.
/// </para>
/// <para>
/// <b>Grid mode only (ADR-029 "Открытые вопросы" Q3 = B).</b> Every entry point below no-ops for a
/// List-mode panel — <see cref="Models.PanelContentSizer"/> has no List-mode formula, and List's own
/// <c>ListViewTemplate</c> does not use <see cref="Models.GridMetrics"/>/<see cref="Models.AdaptiveGridLayout"/>
/// at all (ADR-018 precedent).
/// </para>
/// </remarks>
public partial class ChildWindow
{
    private readonly DispatcherTimer _autoFitDebounceTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(200),
    };

    private bool _contentSizingAttached;

    // MINOR m-1 (P06 fix-cycle, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): true once this
    // partial has observed its first IsVisibleChanged(true) — see
    // OnWindowIsVisibleChangedForContentSizing's remarks for why the FIRST transition is deliberately
    // skipped.
    private bool _contentSizingShownOnce;

    /// <summary>
    /// Wires the two subscriptions that only need this <see cref="Window"/> instance itself (not yet a
    /// live <see cref="ChildWindowModel"/>) — called once, from the constructor. Deliberately does NOT
    /// touch <c>ChildWindow.xaml</c>'s root <c>&lt;Window&gt;</c> element with a
    /// <c>SizeChanged</c>/<c>IsVisibleChanged</c> XAML attribute (this partial's XAML ownership is
    /// limited to the <c>ContextMenu</c> block) — both are plain WPF events, so a code-behind
    /// <c>+=</c> subscription here is exactly equivalent.
    /// </summary>
    private void InitializeContentSizing()
    {
        _autoFitDebounceTimer.Tick += OnAutoFitDebounceTick;
        SizeChanged += OnWindowSizeChangedForContentSizing;
        IsVisibleChanged += OnWindowIsVisibleChangedForContentSizing;
    }

    /// <summary>
    /// Wires the subscriptions that need a live <see cref="ChildWindowModel"/>/<see cref="ViewModels.ChildWindowViewModel"/>
    /// — called once from <c>Initialize</c>, AFTER <c>model.PropertyChanged += OnModelPropertyChanged</c>
    /// (the P04 resize-snap subscription): multiple subscribers on the same
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> event run in subscription order, so this
    /// partial's own <see cref="OnModelPropertyChangedForContentSizing"/> is guaranteed to observe
    /// <see cref="FrameworkElement.Width"/> AFTER any <c>IconSize</c>/<c>ViewMode</c>/<c>IconLayoutPolicy</c>-driven
    /// re-snap that same property change already triggered, instead of computing a fit height against a
    /// stale width. Deliberately does NOT force an immediate recompute on load — loading a panel must
    /// never mutate its saved bounds (ADR-029 Q2 amendment/ADR-028 "no jump on first launch"), the exact
    /// same invariant <c>Initialize</c> itself upholds by no longer calling <c>SnapWidthToGrid()</c>
    /// unconditionally.
    /// </summary>
    private void AttachContentSizing(ChildWindowModel model)
    {
        if (_contentSizingAttached) return;
        _contentSizingAttached = true;

        model.PropertyChanged += OnModelPropertyChangedForContentSizing;
        _viewModel.FileItems.CollectionChanged += OnFileItemsChangedForContentSizing;
    }

    /// <summary>
    /// Reverses <see cref="InitializeContentSizing"/>/<see cref="AttachContentSizing"/> — called from
    /// <c>OnClosing</c>.
    /// </summary>
    private void DetachContentSizing()
    {
        _autoFitDebounceTimer.Stop();
        _autoFitDebounceTimer.Tick -= OnAutoFitDebounceTick;
        SizeChanged -= OnWindowSizeChangedForContentSizing;
        IsVisibleChanged -= OnWindowIsVisibleChangedForContentSizing;

        if (_contentSizingAttached)
        {
            if (_model is not null)
                _model.PropertyChanged -= OnModelPropertyChangedForContentSizing;
            _viewModel.FileItems.CollectionChanged -= OnFileItemsChangedForContentSizing;
            _contentSizingAttached = false;
        }
    }

    /// <summary>
    /// "Fit panel to content" — one-shot action, independent of <see cref="ChildWindowModel.SizingMode"/>
    /// (ADR-029 Решение 7: works even while <see cref="PanelSizingMode.Manual"/>).
    /// </summary>
    private void FitPanelToContentMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (_layoutEditMode.IsLayoutLocked) return; // ADR-026 gating — defensive; the menu item is already disabled for this case.
        if (_model.ViewMode != PanelViewMode.Grid) return; // Q3 = B — no List-mode formula.

        ApplyFitHeight();
    }

    /// <summary>
    /// "Auto-size to content" — toggles <see cref="ChildWindowModel.SizingMode"/> between
    /// <see cref="PanelSizingMode.Manual"/> and <see cref="PanelSizingMode.AutoContent"/>. Turning
    /// AutoContent ON immediately fits the panel once (via
    /// <see cref="OnModelPropertyChangedForContentSizing"/> reacting synchronously to the
    /// <see cref="ChildWindowModel.SizingMode"/> change below) — otherwise the mode would sit inert until
    /// the next unrelated trigger (add/remove icon, resize, …), which would read as "the toggle didn't do
    /// anything" the moment the user checks it. Turning it OFF leaves the current height exactly as-is
    /// (ADR-029 Решение 7: "переключение AutoContent→Manual сохраняет текущую высоту").
    /// </summary>
    private void AutoSizeToContentMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (_layoutEditMode.IsLayoutLocked) return; // ADR-026 gating — defensive.
        if (_model.ViewMode != PanelViewMode.Grid) return; // Q3 = B — the menu item is disabled for this case; defensive here too.

        _model.SizingMode = _model.SizingMode == PanelSizingMode.AutoContent
            ? PanelSizingMode.Manual
            : PanelSizingMode.AutoContent;

        // The SizingMode assignment above already triggered MaybeAutoFit() synchronously (when turning
        // ON) via OnModelPropertyChangedForContentSizing, which itself calls SaveWindowsState() as part
        // of ApplyFitHeight(). An explicit call here is still required to persist the SizingMode flag
        // change ITSELF when turning OFF (Manual never calls ApplyFitHeight, so nothing else would save
        // it) — SaveWindowsState() is debounced, so this is never a redundant synchronous write.
        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// "Apply to all panels…" under the "Icon layout" submenu (P06 UX requirement from the P05 review):
    /// broadcasts THIS panel's current <see cref="ChildWindowModel.IconLayoutPolicy"/> to every panel
    /// <see cref="IWindowManager.GetAllChildWindows"/> returns (all pages — see
    /// <c>docs/PLANS/workspace-grid-06-fit-content.md</c> for why "all pages", not just the current one,
    /// was chosen), after a confirmation dialog naming the panel count. Each live panel already reacts to
    /// its own model's <see cref="ChildWindowModel.PropertyChanged"/> independently (this panel's own
    /// resize-snap/AutoContent-recompute subscriptions, mirrored per-panel), so this method only needs to
    /// assign the property — it does not itself resize/re-snap anything beyond that.
    /// </summary>
    private void ApplyIconLayoutPolicyToAllMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (_layoutEditMode.IsLayoutLocked) return; // P06 fix-cycle MAJOR-1 — defensive; the menu item is already disabled for this case.

        var targetPolicy = _model.IconLayoutPolicy;
        var panels = _windowManager.GetAllChildWindows().ToList();
        if (panels.Count == 0) return;

        var loc = LocalizationSource.Current;
        var policyName = loc.GetString(targetPolicy == IconLayoutPolicy.FixedGap
            ? "Enum_IconLayoutPolicy_FixedGap"
            : "Enum_IconLayoutPolicy_LegacyStretch");
        var panelsText = loc.FormatPlural("Plural_Panels", panels.Count);

        // Scope decision (P06 fix-cycle, orchestrator): both broadcasts apply to every panel on every
        // page (see docs/PLANS/workspace-grid-06-fit-content.md §4) — the dialog text says so explicitly,
        // distinct from P03's "Align all", which is current-page-only.
        // L-5 fix (review round, design-system-1x.md §2.8): irreversible/hard-to-undo bulk action — the
        // Warning icon defaults Enter to "No" (ConfirmationService's own remarks) instead of the
        // Question default's "Yes", guarding this against an accidental Enter.
        if (!_confirmation.Confirm(
                loc.Format("Panel_Confirm_ApplyIconLayout_Format", policyName, panelsText),
                loc.GetString("Panel_Confirm_ApplyIconLayout_Title"),
                MessageBoxImage.Warning))
            return;

        foreach (var panel in panels)
            panel.IconLayoutPolicy = targetPolicy;

        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// "Apply sizing mode to all panels…" — the <see cref="ChildWindowModel.SizingMode"/> counterpart of
    /// <see cref="ApplyIconLayoutPolicyToAllMenu_Click"/>; see that method's remarks for the shared
    /// broadcast mechanism/rationale.
    /// </summary>
    private void ApplySizingModeToAllMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (_layoutEditMode.IsLayoutLocked) return; // P06 fix-cycle MAJOR-1 — the menu item is now gated too (see ChildWindow.xaml), this is the defensive half.
        if (_model.ViewMode != PanelViewMode.Grid) return; // now nested under "Panel sizing" (P06 fix-cycle UX-P2), which itself gates on ViewMode == Grid — defensive here too.

        var targetMode = _model.SizingMode;
        var panels = _windowManager.GetAllChildWindows().ToList();
        if (panels.Count == 0) return;

        var loc = LocalizationSource.Current;
        var modeName = loc.GetString(targetMode == PanelSizingMode.AutoContent
            ? "Enum_PanelSizingMode_AutoContent"
            : "Enum_PanelSizingMode_Manual");
        var panelsText = loc.FormatPlural("Plural_Panels", panels.Count);

        // Scope decision (P06 fix-cycle, orchestrator): see ApplyIconLayoutPolicyToAllMenu_Click's
        // remarks — same "all pages" scope and unified "cannot be undone" wording (P06 fix-cycle UX-P1).
        // L-5 fix (review round): see that same method's own remarks — Warning, not the Question default.
        if (!_confirmation.Confirm(
                loc.Format("Panel_Confirm_ApplySizingMode_Format", modeName, panelsText),
                loc.GetString("Panel_Confirm_ApplySizingMode_Title"),
                MessageBoxImage.Warning))
            return;

        foreach (var panel in panels)
            panel.SizingMode = targetMode;

        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// The <see cref="PanelSizingMode.AutoContent"/> passive-recompute trigger for every model property
    /// this feature cares about (ADR-029 Решение 7's trigger table): <c>IconSize</c>/<c>ViewMode</c>
    /// (feed the fit formula directly), <c>IconLayoutPolicy</c> (changes column capacity), and
    /// <c>SizingMode</c> itself (so turning AutoContent ON — from ANY source, including "Apply sizing mode
    /// to all panels" on a DIFFERENT panel's menu — immediately fits, not just this panel's own menu
    /// Click handler).
    /// </summary>
    private void OnModelPropertyChangedForContentSizing(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChildWindowModel.SizingMode):
            case nameof(ChildWindowModel.IconSize):
            case nameof(ChildWindowModel.ViewMode):
            case nameof(ChildWindowModel.IconLayoutPolicy):
                MaybeAutoFit();
                break;
        }
    }

    /// <summary>
    /// Add/remove/drop/portal-refresh/content-filter trigger (ADR-029's "Добавление/удаление иконки" and
    /// "смена вкладки/набора" rows — a content-filter change also rebuilds
    /// <see cref="ViewModels.BaseViewModel.FileItems"/> via <c>ReplaceFileItems</c>, so it is covered here
    /// too, for free). <see cref="ViewModels.BaseViewModel.FileItems"/> is a stable instance for this
    /// <see cref="ViewModels.ChildWindowViewModel"/>'s whole lifetime (see <see cref="AttachContentSizing"/>'s
    /// remarks) — no re-subscription is ever needed after the first.
    /// </summary>
    private void OnFileItemsChangedForContentSizing(object? sender, NotifyCollectionChangedEventArgs e) => MaybeAutoFit();

    /// <summary>
    /// Ручной resize-жест по ширине trigger (ADR-029's "Ручной resize по ширине" row) — debounced so the
    /// height recompute runs once after the gesture settles, not on every intermediate frame (the row's
    /// own "не в реальном времени внутри одного и того же resize-жеста" requirement). Filters on
    /// <see cref="SizeChangedInfo.WidthChanged"/>(<see cref="SizeChangedEventArgs.WidthChanged"/>) —  a
    /// HEIGHT-only change (including one THIS partial itself just applied via
    /// <see cref="Behaviors.PanelGeometryWriter"/>) never reaches this debounce at all, so no
    /// <see cref="Behaviors.PanelGeometryWriter.IsApplying"/> re-entrancy check is even needed here: a
    /// pure <c>Height</c> assignment does not raise <c>WidthChanged</c>.
    /// </summary>
    private void OnWindowSizeChangedForContentSizing(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        _autoFitDebounceTimer.Stop();
        _autoFitDebounceTimer.Start();
    }

    /// <summary>
    /// MAJOR M-3 (P06 fix-cycle, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): a resize/drag gesture
    /// may still be in progress when this 200ms debounce fires — e.g. the user paused mid-drag with the
    /// mouse button still down for more than 200ms. Applying a fit height then would fight the still-live
    /// gesture (both writing <see cref="FrameworkElement.Height"/> the same frame) and visibly jump. Recharge the
    /// timer instead of firing, using the SAME shared <see cref="PanelInteractionTracker"/> every OTHER
    /// gesture in this window already reports through (ADR-021) — no new state needed.
    /// </summary>
    private void OnAutoFitDebounceTick(object? sender, EventArgs e)
    {
        if (_interactionTracker.State is PanelInteractionState.Resizing or PanelInteractionState.Dragging)
        {
            _autoFitDebounceTimer.Stop();
            _autoFitDebounceTimer.Start();
            return;
        }

        _autoFitDebounceTimer.Stop();
        MaybeAutoFit();
    }

    /// <summary>
    /// "смена вкладки/набора (PageManagerService), при активации панели" trigger (ADR-029's trigger
    /// table): recomputes once this panel's <see cref="Window"/> actually becomes visible again — e.g.
    /// switching back to the page this panel lives on. A no-op transition to invisible is ignored.
    /// </summary>
    /// <remarks>
    /// MINOR m-1 (P06 fix-cycle): the FIRST <c>Show()</c> ALSO raises <c>IsVisibleChanged(true)</c> —
    /// recomputing there would write a fresh geometry/span for an <see cref="PanelSizingMode.AutoContent"/>
    /// panel the very moment it appears, which is exactly the "no jump on first launch" invariant
    /// ADR-028 already enforces for the resize-snap path (P05 fix-cycle MAJOR-1, <c>Initialize</c> no
    /// longer force-re-snaps). That first transition is skipped unconditionally; every SUBSEQUENT true
    /// transition (switching back to this panel's page) still recomputes exactly as documented above.
    /// </remarks>
    private void OnWindowIsVisibleChangedForContentSizing(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        if (!_contentSizingShownOnce)
        {
            _contentSizingShownOnce = true;
            return;
        }

        MaybeAutoFit();
    }

    /// <summary>
    /// The single gate every AutoContent trigger above funnels through: no-ops unless
    /// <see cref="ChildWindowModel.SizingMode"/> is <see cref="PanelSizingMode.AutoContent"/>
    /// (ADR-029 Решение 7: "Manual никогда не меняет высоту"), the panel is in Grid view (Q3 = B), and no
    /// <see cref="Behaviors.PanelGeometryWriter"/> write from ANY source (this partial or another) is
    /// currently in flight (defence in depth against the synchronous <c>PropertyChanged</c> reentrancy
    /// this method's own callers already mostly rule out by construction).
    /// </summary>
    /// <remarks>
    /// MINOR m-2 (P06 fix-cycle, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): also defers while a
    /// roll-up/roll-down <see cref="Behaviors.RollUpBehavior.IsAnimating"/> animation (150ms) is in
    /// flight — a plain <see cref="FrameworkElement.Height"/> assignment made mid-animation is silently ignored by
    /// WPF (the animation clock still owns <see cref="FrameworkElement.HeightProperty"/> at a higher priority, see
    /// <see cref="Behaviors.RollUpBehavior.ApplyRollUp"/>'s own H3 remarks), but <see cref="ApplyFitHeight"/>
    /// would still go on to persist <see cref="ChildWindowModel.Size"/>/spans as though it had taken
    /// effect — drifting the model away from the height the panel actually ends up showing once the
    /// animation completes. Reuses the same debounce timer every OTHER trigger already goes through
    /// (retried well after the 150ms animation has finished).
    /// </remarks>
    private void MaybeAutoFit()
    {
        if (_model is null) return;
        if (_model.SizingMode != PanelSizingMode.AutoContent) return;
        if (_model.ViewMode != PanelViewMode.Grid) return;
        if (_geometryWriter.IsApplying) return;

        if (_rollUpBehavior.IsAnimating)
        {
            _autoFitDebounceTimer.Stop();
            _autoFitDebounceTimer.Start();
            return;
        }

        ApplyFitHeight();
    }

    /// <summary>
    /// MAJOR M-4 (P06 fix-cycle, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): called via
    /// <see cref="Behaviors.IPanelSizingGridHost.NotifyHeightResizeCommitted"/> once a completed user
    /// resize gesture (Thumb OR native <c>WM_SIZING</c>) that affected the panel's HEIGHT has been
    /// committed. Fences-like: an explicit, deliberate vertical resize is a stronger signal than the
    /// passive <see cref="PanelSizingMode.AutoContent"/> auto-fit, so it demotes the panel back to
    /// <see cref="PanelSizingMode.Manual"/> — the same "manual wins" rule <see cref="MaybeAutoFit"/>
    /// already enforces for every OTHER trigger. A no-op when the panel is already
    /// <see cref="PanelSizingMode.Manual"/> (avoids a redundant <c>PropertyChanged</c>/diagnostics event)
    /// or when <c>_model</c> is not yet attached.
    /// </summary>
    private void OnUserHeightResizeCommitted()
    {
        if (_model is null) return;
        if (_model.SizingMode != PanelSizingMode.AutoContent) return;

        _model.SizingMode = PanelSizingMode.Manual;
        _diagnostics.SizingModeChanged(_model.Id, PanelSizingMode.AutoContent, PanelSizingMode.Manual, "HeightResizeCommitted");
    }

    /// <summary>
    /// Computes ADR-029 Решение 7's fit height for the CURRENT model/window state and applies it — the
    /// single implementation both <see cref="FitPanelToContentMenu_Click"/> (Manual-safe one-shot) and
    /// <see cref="MaybeAutoFit"/> (AutoContent passive recompute) call.
    /// </summary>
    private void ApplyFitHeight()
    {
        if (_model is null) return;

        var itemCount = _viewModel.FileItems.Count;
        var iconSize = _model.IconSize;
        var windowWidth = Width;
        var layoutPolicy = _model.IconLayoutPolicy;
        var verticalScrollBarWidth = SystemParameters.VerticalScrollBarWidth;
        var raw = PanelContentSizer.ComputeRawContentHeight(itemCount, iconSize, windowWidth, layoutPolicy, verticalScrollBarWidth);

        if (_model.GeometryMode == PanelGeometryMode.GridAligned)
        {
            // Defensive null-guard: IMonitorService.GetMonitors's own contract always returns a non-null
            // (possibly empty) snapshot, and PanelGridStateResolver.Resolve already treats an EMPTY list
            // as its own fail-safe branch (degrades to PanelGridState.Legacy for this frame) — but a
            // stray null here would otherwise NRE before ever reaching that fail-safe.
            var monitors = (_monitorService.GetMonitors() ?? [])
                .Select(m => (m.DeviceName, m.Bounds, m.WorkArea))
                .ToList();
            var expandedHeight = _model.IsRolledUp ? _rollUpBehavior.ExpandedHeight : Height;
            var currentBounds = new Rect(Left, Top, Width, expandedHeight);

            // Инвариант В1 (docs/PLANS/workspace-grid-03-04-integration.md §0.1): the single point that
            // reads ChildWindowModel.GeometryMode to decide grid participation for THIS frame — also
            // lazily migrates WidthSpanUnits/HeightSpanUnits onto the model when they are missing/stale.
            //
            // P06 fix-cycle MAJOR-2: the RETURNED state — not _model.GeometryMode — is what must gate the
            // rest of this branch. Resolve degrades to PanelGridState.Legacy on its own fail-safe paths
            // (no monitors; non-finite/non-positive currentBounds) WITHOUT writing WidthSpanUnits back
            // onto the model — the previous code ignored this return value and fell through to
            // `_model.WidthSpanUnits ?? WorkspaceGridMetrics.MinWidthSpanExpanded`, which silently
            // quantised an unmigrated panel down to the workspace-grid MINIMUM width span (16 units, 120
            // DIP) regardless of its real (e.g. 352 DIP) width — the next span->bounds consumer
            // (RollUpBehavior, align, restore) would then shrink the panel to that bogus minimum.
            var state = PanelGridStateResolver.Resolve(_model, currentBounds, monitors, _model.IsRolledUp);

            if (!state.IsGridAligned)
            {
                // Fail-safe for THIS frame only — the model itself is untouched (Resolve's own contract),
                // so a later frame gets another chance to migrate. Round as LegacyFreeform in the
                // meantime rather than ever reading a stale/never-migrated WidthSpanUnits.
                ApplyHeight(PanelContentSizer.RoundForLegacyFreeform(raw), span: null);
                return;
            }

            var heightUnits = PanelContentSizer.RoundHeightUnitsForGridAligned(raw, state.UnitSize, state.Gap);

            // Never read _model.WidthSpanUnits directly — derive it from the SAME currentBounds this
            // method already used to compute `raw`/columnCapacity, the same BoundsToSpans conversion
            // PanelSizingGridSupport.PersistSpans uses at the end of every other grid-aligned resize/
            // commit path, so a fit height never disagrees with what committing the CURRENT width would
            // derive (and never falls back to the unrelated workspace-grid minimum span).
            var widthUnits = WorkspaceLayoutPolicy.BoundsToSpans(currentBounds.Size, state.UnitSize, state.Gap).WidthUnits;
            var span = new GridSpan(widthUnits, heightUnits);
            var size = WorkspaceLayoutPolicy.SpansToBounds(span, state.UnitSize, state.Gap);

            ApplyHeight(size.Height, span);
        }
        else
        {
            ApplyHeight(PanelContentSizer.RoundForLegacyFreeform(raw), span: null);
        }
    }

    /// <summary>
    /// Writes <paramref name="newHeight"/> onto the panel — through <see cref="Behaviors.PanelGeometryWriter"/>
    /// when expanded, or onto the remembered expanded state when
    /// <see cref="ChildWindowModel.IsRolledUp"/> (ADR-029 Решение 7's roll-up row — see this partial's
    /// class remarks).
    /// </summary>
    private void ApplyHeight(double newHeight, GridSpan? span)
    {
        if (_model is null) return;

        if (_model.IsRolledUp)
        {
            _rollUpBehavior.ExpandedHeight = newHeight;
            _model.Size = new DrawingSize(_model.Size.Width, (int)Math.Round(newHeight));

            if (span is { } s)
            {
                _model.WidthSpanUnits = s.WidthUnits;
                _model.HeightSpanUnits = s.HeightUnits;
            }

            _windowManager.SaveWindowsState();
            return;
        }

        _geometryWriter.Apply(new Rect(Left, Top, Width, newHeight), span, monitorDeviceId: null);
        _windowManager.SaveWindowsState();
    }
}
