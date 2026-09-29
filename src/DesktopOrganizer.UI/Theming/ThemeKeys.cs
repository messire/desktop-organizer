namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// The complete set of "Palette T" (application chrome) resource-dictionary keys
/// (docs/PLANS/design-system-1x.md §2.2/§2.3/§2.4, ADR-047 D-2/D-4/D-5). Every key here resolves, in
/// every dictionary <see cref="ThemeResourceBuilder.Build"/> produces (Light/Dark/HighContrast), to
/// either a frozen <see cref="System.Windows.Media.Brush"/> (the <c>Theme.*</c> colour keys) or a plain
/// <see cref="double"/>/<see cref="System.Windows.Thickness"/>/<see cref="System.Windows.CornerRadius"/>
/// (the <c>Font.Size.*</c>/geometry keys) — never a raw <see cref="System.Windows.Media.Color"/>: unlike
/// Palette P (<c>Color.*</c> + <c>Brush.*</c> pairs), Palette T has exactly one key per token because
/// nothing outside <see cref="ThemePalette"/> itself needs the raw <see cref="System.Windows.Media.Color"/>.
/// Consumers only ever reference these through <c>{DynamicResource}</c> — never <c>{StaticResource}</c>
/// (a guard test enforces this) — because a theme switch replaces the whole dictionary in
/// <see cref="System.Windows.Application.Resources"/>, and only <c>DynamicResource</c> re-resolves after
/// that swap.
/// </summary>
public static class ThemeKeys
{
    // ── Surfaces ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Window/sidebar background.</summary>
    public const string SurfaceWindow = "Theme.Surface.Window";

    /// <summary>Card background.</summary>
    public const string SurfaceCard = "Theme.Surface.Card";

    /// <summary>Card background while hovered.</summary>
    public const string SurfaceCardHover = "Theme.Surface.CardHover";

    /// <summary>Menu/tooltip/dialog/InfoBar base background.</summary>
    public const string SurfaceFlyout = "Theme.Surface.Flyout";

    // ── Text ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Primary (highest-emphasis) text.</summary>
    public const string TextPrimary = "Theme.Text.Primary";

    /// <summary>Secondary (description/caption) text.</summary>
    public const string TextSecondary = "Theme.Text.Secondary";

    /// <summary>Disabled text.</summary>
    public const string TextDisabled = "Theme.Text.Disabled";

    /// <summary>Text/icons drawn on top of an <c>Accent.*</c> fill.</summary>
    public const string TextOnAccent = "Theme.Text.OnAccent";

    /// <summary>Caution-severity text (replaces the pre-6.1 Settings window's fixed "Warning" brush).</summary>
    public const string TextCaution = "Theme.Text.Caution";

    /// <summary>Critical/error-severity text.</summary>
    public const string TextCritical = "Theme.Text.Critical";

    /// <summary>Success-severity text.</summary>
    public const string TextSuccess = "Theme.Text.Success";

    // ── Accent (primary buttons, checked/selected states) ────────────────────────────────────────────

    /// <summary>Accent fill (primary button, checked toggle).</summary>
    public const string AccentFill = "Theme.Accent.Fill";

    /// <summary>Accent fill, hovered.</summary>
    public const string AccentHover = "Theme.Accent.Hover";

    /// <summary>Accent fill, pressed.</summary>
    public const string AccentPressed = "Theme.Accent.Pressed";

    /// <summary>Accent fill, disabled.</summary>
    public const string AccentDisabled = "Theme.Accent.Disabled";

    // ── Control (secondary buttons, text/combo boxes) ────────────────────────────────────────────────

    /// <summary>Secondary control fill (button, field).</summary>
    public const string ControlFill = "Theme.Control.Fill";

    /// <summary>Secondary control fill, hovered.</summary>
    public const string ControlHover = "Theme.Control.Hover";

    /// <summary>Secondary control fill, pressed.</summary>
    public const string ControlPressed = "Theme.Control.Pressed";

    /// <summary>Secondary control fill, disabled.</summary>
    public const string ControlDisabled = "Theme.Control.Disabled";

    /// <summary>Subtle-button/menu-item/sidebar-item hover wash.</summary>
    public const string ControlSubtleHover = "Theme.Control.Subtle.Hover";

    /// <summary>Subtle-button/menu-item/sidebar-item pressed wash.</summary>
    public const string ControlSubtlePressed = "Theme.Control.Subtle.Pressed";

    // ── Stroke ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Card border.</summary>
    public const string StrokeCard = "Theme.Stroke.Card";

    /// <summary>Divider line between groups/rows.</summary>
    public const string StrokeDivider = "Theme.Stroke.Divider";

    /// <summary>Button/generic control border.</summary>
    public const string StrokeControl = "Theme.Stroke.Control";

    /// <summary>TextBox/ComboBox border, toggle-off track, radio/checkbox border.</summary>
    public const string StrokeInput = "Theme.Stroke.Input";

    /// <summary>Keyboard-focus visual, outer ring.</summary>
    public const string StrokeFocus = "Theme.Stroke.Focus";

    /// <summary>Keyboard-focus visual, inner ring.</summary>
    public const string StrokeFocusInner = "Theme.Stroke.FocusInner";

    // ── Selection ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The non-text "this item is selected" indicator (sidebar pill, selected segment/swatch ring).</summary>
    public const string SelectionIndicator = "Theme.Selection.Indicator";

    /// <summary>Selected row/segment fill.</summary>
    public const string SelectionFill = "Theme.Selection.Fill";

    /// <summary>Text colour on a <see cref="SelectionFill"/> background.</summary>
    public const string SelectionText = "Theme.Selection.Text";

    // ── InfoBar ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Informational InfoBar background.</summary>
    public const string InfoBarInfoBackground = "Theme.InfoBar.Info.Background";

    /// <summary>Success InfoBar background.</summary>
    public const string InfoBarSuccessBackground = "Theme.InfoBar.Success.Background";

    /// <summary>Caution InfoBar background.</summary>
    public const string InfoBarCautionBackground = "Theme.InfoBar.Caution.Background";

    /// <summary>Critical InfoBar background.</summary>
    public const string InfoBarCriticalBackground = "Theme.InfoBar.Critical.Background";

    // ── Typography (§2.3) — sys:Double sizes, recomputed from SystemFonts.MessageFontSize ────────────

    /// <summary>Caption size: M (descriptions, badges, compact segment labels, menu items).</summary>
    public const string FontSizeCaption = "Font.Size.Caption";

    /// <summary>Body size: M + 2 (card headers, control text, dialog/InfoBar body).</summary>
    public const string FontSizeBody = "Font.Size.Body";

    /// <summary>BodyStrong size: M + 2, SemiBold weight (group headers, InfoBar title).</summary>
    public const string FontSizeBodyStrong = "Font.Size.BodyStrong";

    /// <summary>Subtitle size: M + 6 (dialog title).</summary>
    public const string FontSizeSubtitle = "Font.Size.Subtitle";

    /// <summary>Title size: M + 8 (Settings/Customize page header).</summary>
    public const string FontSizeTitle = "Font.Size.Title";

    // ── Geometry (§2.4) — theme-independent, present in every dictionary for T-2 coverage ────────────

    /// <summary>Card inner padding (16,12).</summary>
    public const string CardPadding = "Theme.Card.Padding";

    /// <summary>Gap between cards within a group (4).</summary>
    public const string CardGap = "Theme.Card.Gap";

    /// <summary>Gap above a group header (24).</summary>
    public const string GroupGapTop = "Theme.Group.GapTop";

    /// <summary>Gap below a group header (8).</summary>
    public const string GroupGapBottom = "Theme.Group.GapBottom";

    /// <summary>Settings page content padding (24,16).</summary>
    public const string PagePaddingSettings = "Theme.Page.Padding.Settings";

    /// <summary>Customize (420 DIP) page content padding (16,12).</summary>
    public const string PagePaddingCustomize = "Theme.Page.Padding.Customize";

    /// <summary>Minimum control (button/field/combo) height (32).</summary>
    public const string ControlMinHeight = "Theme.Control.MinHeight";

    /// <summary>Minimum menu-item height (30).</summary>
    public const string MenuItemMinHeight = "Theme.MenuItem.MinHeight";

    /// <summary>Menu-item padding (12,6).</summary>
    public const string MenuItemPadding = "Theme.MenuItem.Padding";

    /// <summary>Corner radius for Palette T controls (4) — distinct from Palette P's <c>Radius.Control</c>.</summary>
    public const string RadiusControl = "Theme.Radius.Control";

    /// <summary>Corner radius for Palette T popups (8).</summary>
    public const string RadiusPopup = "Theme.Radius.Popup";

    /// <summary>Corner radius for Palette T cards (6).</summary>
    public const string RadiusCard = "Theme.Radius.Card";

    /// <summary>Every colour-token key (<c>Theme.*</c> excluding geometry), for coverage tests.</summary>
    public static IReadOnlyList<string> ColorKeys { get; } =
    [
        SurfaceWindow, SurfaceCard, SurfaceCardHover, SurfaceFlyout,
        TextPrimary, TextSecondary, TextDisabled, TextOnAccent, TextCaution, TextCritical, TextSuccess,
        AccentFill, AccentHover, AccentPressed, AccentDisabled,
        ControlFill, ControlHover, ControlPressed, ControlDisabled, ControlSubtleHover, ControlSubtlePressed,
        StrokeCard, StrokeDivider, StrokeControl, StrokeInput, StrokeFocus, StrokeFocusInner,
        SelectionIndicator, SelectionFill, SelectionText,
        InfoBarInfoBackground, InfoBarSuccessBackground, InfoBarCautionBackground, InfoBarCriticalBackground,
    ];

    /// <summary>Every typography key.</summary>
    public static IReadOnlyList<string> FontSizeKeys { get; } =
    [
        FontSizeCaption, FontSizeBody, FontSizeBodyStrong, FontSizeSubtitle, FontSizeTitle,
    ];

    /// <summary>Every geometry key (theme-independent).</summary>
    public static IReadOnlyList<string> GeometryKeys { get; } =
    [
        CardPadding, CardGap, GroupGapTop, GroupGapBottom, PagePaddingSettings, PagePaddingCustomize,
        ControlMinHeight, MenuItemMinHeight, MenuItemPadding, RadiusControl, RadiusPopup, RadiusCard,
    ];

    /// <summary>Every key this type declares, across all four groups — what a built dictionary must contain.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. ColorKeys, .. FontSizeKeys, .. GeometryKeys];
}
