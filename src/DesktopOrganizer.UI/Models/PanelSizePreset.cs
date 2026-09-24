namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Named workspace-grid spans (ADR-028 "Presets `S`/`M`/`L`/`XL`") offered as a convenience shortcut for
/// sizing a <see cref="PanelGeometryMode.GridAligned"/> panel. This is a labelled shortcut ON TOP of
/// arbitrary spans, not the only allowed size: manual resize (quantised to the grid) and "Fit panel to
/// content" remain available for any <see cref="GridSpan"/>, per ADR-028's explicit rejection of a
/// "only S/M/L" restriction. See <see cref="WorkspaceGridMetrics.SpanForPreset"/> for the
/// preset → <see cref="GridSpan"/> mapping and <see cref="WorkspaceLayoutPolicy.PreferredSize"/> for the
/// DIP size it resolves to.
/// </summary>
public enum PanelSizePreset
{
    /// <summary><c>20x20</c> workspace units (ADR-028 table) — closest to the pre-existing 1-2 column minimum.</summary>
    Small,

    /// <summary><c>32x30</c> workspace units — default/"typical" panel size, closest to the legacy 260x300 XAML default.</summary>
    Medium,

    /// <summary><c>44x40</c> workspace units.</summary>
    Large,

    /// <summary><c>60x50</c> workspace units.</summary>
    ExtraLarge,
}
