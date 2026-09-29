using System.ComponentModel;
using System.Windows.Input;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel behind <see cref="Controls.PanelColorEditor"/> (ADR-042) — the shared accent/text colour
/// editor used by both <c>PanelCustomizeViewModel.Colors</c> (a single panel) and
/// <c>GlobalDefaultsViewModel.DefaultColors</c> (new-panel defaults). Deliberately owns no colour storage
/// of its own: every read/write proxies through the delegates supplied at construction, the same
/// "thin proxy over an owner's real property" shape <see cref="SelectedPanelSettingsViewModel"/> already
/// uses for its own accent/text proxies.
/// </summary>
/// <remarks>
/// ADR-042 makes the panel's <see cref="PanelThemeMode"/> (Light/Dark) an explicit user choice instead of
/// something baked into a preset. <see cref="Mode"/> is DERIVED from <see cref="TextColor"/> via
/// <see cref="PanelThemeRules.FromTextColor(string?)"/> — there is no separate stored mode field (CLAUDE.md
/// persistence freeze). Three distinct write paths exist, deliberately kept separate:
/// <list type="bullet">
/// <item><description><see cref="ApplyAccent"/> — "choosing an accent" (a preset swatch or the grid's own
/// "Custom…" cell): writes BOTH colours atomically, deriving the text colour from the CURRENT
/// <see cref="Mode"/> (the matching preset's own text-for-that-mode, or <see cref="PanelThemeRules.DefaultTextFor"/>
/// for a non-preset/custom accent).</description></item>
/// <item><description><see cref="SetMode"/> — the Mode segmented control: writes TEXT ONLY, re-deriving it
/// from whatever preset the CURRENT accent matches (or the mode's own default text when it matches none).</description></item>
/// <item><description><see cref="AccentColor"/>/<see cref="TextColor"/> property setters — the Advanced
/// card's raw hex boxes/"Custom…" text picker: each writes exactly ONE colour, independently, with no mode
/// coupling at all (the deliberate "power user escape hatch" this app has always had).</description></item>
/// </list>
/// </remarks>
public sealed class PanelColorEditorViewModel : INotifyPropertyChanged
{
    private readonly IColorPickerService _colorPicker;
    private readonly Func<string> _getAccent;
    private readonly Action<string> _setAccent;
    private readonly Func<string> _getText;
    private readonly Action<string> _setText;
    private readonly Action<string, string> _setBoth;
    private readonly Action? _reset;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelColorEditorViewModel"/> class.
    /// </summary>
    /// <param name="colorPicker">Shows the system "Custom…" colour dialog for <see cref="PickCustomAccentCommand"/>/<see cref="PickCustomTextCommand"/>.</param>
    /// <param name="getAccent">Reads the owner's current accent colour.</param>
    /// <param name="setAccent">Writes the owner's accent colour alone — used by the Advanced card's raw accent hex box only.</param>
    /// <param name="getText">Reads the owner's current text colour.</param>
    /// <param name="setText">Writes the owner's text colour alone — used by <see cref="SetMode"/> and the Advanced card's raw text hex box/"Custom…" picker.</param>
    /// <param name="setBoth">Applies an (accent, text) pair atomically — used by <see cref="ApplyAccent"/>, and by <see cref="ResetCommand"/>'s own default behaviour (when <paramref name="reset"/> is <see langword="null"/>), so the owner can batch both writes into a single save.</param>
    /// <param name="reset">
    /// Release-polish-1.0 review round M7 (per-host Reset semantics): when supplied, REPLACES
    /// <see cref="ResetCommand"/>'s default colour-reset behaviour entirely — e.g. <c>PanelCustomizeViewModel</c>
    /// resets this panel's accent/text/opacity/surface style/icon size to the app's CURRENT new-panel
    /// defaults (read-only <c>IAppSettings</c> use — never mutates settings) as a single batched write.
    /// <see langword="null"/> when the owner IS the defaults themselves (<c>GlobalDefaultsViewModel</c>),
    /// where <see cref="ResetCommand"/> falls back to applying <see cref="PanelColorPresets.Default"/>'s
    /// Dark pair via <paramref name="setBoth"/> (resetting those fields to themselves would be a
    /// meaningless no-op).
    /// </param>
    /// <param name="resetDescriptionKey">
    /// The RESX key of the Advanced card's "Reset to defaults" description (M7: the two hosts describe a
    /// different reset scope — a single panel's own colours vs. the app's new-panel defaults — so this is
    /// supplied per host rather than hard-coded in <c>PanelColorEditor.xaml</c>).
    /// </param>
    public PanelColorEditorViewModel(
        IColorPickerService colorPicker,
        Func<string> getAccent,
        Action<string> setAccent,
        Func<string> getText,
        Action<string> setText,
        Action<string, string> setBoth,
        Action? reset = null,
        string resetDescriptionKey = "Common_ResetToDefaults_Button")
    {
        _colorPicker = colorPicker;
        _getAccent = getAccent;
        _setAccent = setAccent;
        _getText = getText;
        _setText = setText;
        _setBoth = setBoth;
        _reset = reset;
        ResetDescriptionKey = resetDescriptionKey;

        Presets = PanelColorPresets.All
            .Select(preset => new ColorPresetOption(preset, () => ApplyAccent(preset.AccentHex)))
            .ToList();

        // Release-polish-1.0 visual review item 1: a single flat item source (12 presets + one trailing
        // sentinel) instead of a CompositeCollection/CollectionContainer in XAML — the latter's "Collection"
        // binding does not reliably pick up an inherited DataContext outside the visual tree, leaving the
        // swatch grid's preset cells empty. Computed once: neither list's shape ever changes after
        // construction.
        SwatchItems = Presets.Cast<object>().Append(new CustomSwatchSlotMarker()).ToList();

        SetModeCommand = new RelayCommand<PanelThemeMode>(SetMode);
        PickCustomAccentCommand = new RelayCommand(PickCustomAccent);
        PickCustomTextCommand = new RelayCommand(PickCustomText);
        ResetCommand = new RelayCommand(Reset);

        RefreshSelection();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The twelve selectable presets, in <see cref="PanelColorPresets.All"/>'s order.</summary>
    public IReadOnlyList<ColorPresetOption> Presets { get; }

    /// <summary>
    /// The swatch grid's full item source (release-polish-1.0 visual review item 1): <see cref="Presets"/>
    /// plus a single trailing <see cref="CustomSwatchSlotMarker"/> sentinel, so <c>PanelColorEditor.xaml</c>'s
    /// "Custom…" cell renders as a true 13th grid cell — same <see cref="System.Windows.Controls.WrapPanel"/>
    /// flow, Tab stop, and arrow-key cycle as the 12 presets — rather than a separate sibling control the
    /// grid's own wrap layout cannot see.
    /// </summary>
    public IReadOnlyList<object> SwatchItems { get; }

    /// <summary>The RESX key of the Advanced card's "Reset to defaults" description — see this class's own constructor remarks (M7 per-host Reset semantics).</summary>
    public string ResetDescriptionKey { get; }

    /// <summary>The owner's current accent colour (hex). Setting it calls the owner's accent setter ALONE — see this class's own remarks.</summary>
    public string AccentColor
    {
        get => _getAccent();
        set
        {
            _setAccent(value);
            OnPropertyChanged(nameof(AccentColor));
            RefreshSelection();
        }
    }

    /// <summary>The owner's current text colour (hex). Setting it calls the owner's text setter ALONE — see this class's own remarks.</summary>
    public string TextColor
    {
        get => _getText();
        set
        {
            _setText(value);
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(Mode));
            RefreshSelection();
        }
    }

    /// <summary>
    /// The panel's explicit theme mode (ADR-042), DERIVED from <see cref="TextColor"/> via
    /// <see cref="PanelThemeRules.FromTextColor(string?)"/> — there is no separate stored mode.
    /// </summary>
    public PanelThemeMode Mode => PanelThemeRules.FromTextColor(TextColor);

    /// <summary>The preset whose accent exactly matches <see cref="AccentColor"/>, or <see langword="null"/> for a custom accent.</summary>
    public PanelColorPreset? SelectedPreset => PanelColorPresets.FindByAccent(AccentColor);

    /// <summary>
    /// UX#1 (release-polish-1.0 visual review): whether the swatch grid's 13th "Custom…" cell should render
    /// as SELECTED — <see langword="true"/> iff <see cref="AccentColor"/> matches none of the twelve
    /// presets (<see cref="SelectedPreset"/> is <see langword="null"/>). Drives <c>PanelColorEditor.xaml</c>'s
    /// "Custom…" cell template: when <see langword="true"/> it fills its circle with <see cref="AccentColor"/>
    /// and shows the same selection ring/checkmark a selected preset swatch shows, instead of the dashed
    /// "+" placeholder.
    /// </summary>
    public bool IsCustomSelected => SelectedPreset is null;

    /// <summary>Sets <see cref="Mode"/> — the Mode segmented control's Light/Dark options.</summary>
    public ICommand SetModeCommand { get; }

    /// <summary>Opens the system colour dialog for a custom accent (the swatch grid's 13th "Custom…" cell) — applies the picked colour via <see cref="ApplyAccent"/>, exactly like choosing a preset swatch.</summary>
    public ICommand PickCustomAccentCommand { get; }

    /// <summary>Opens the system colour dialog for a custom TEXT colour (Advanced card) — a raw, mode-independent edit; seeded with <see cref="TextColor"/> and <see cref="PanelColorPresets.CustomDialogSeed"/>.</summary>
    public ICommand PickCustomTextCommand { get; }

    /// <summary>
    /// Resets to defaults — see this class's own remarks (<see cref="_reset"/>'s constructor parameter,
    /// documented as <c>reset</c>) for the exact per-host M7 semantics: either the supplied override
    /// (Customize window), or <see cref="PanelColorPresets.Default"/>'s Dark-mode pair (new-panel defaults).
    /// </summary>
    public ICommand ResetCommand { get; }

    /// <summary>
    /// Re-reads <see cref="AccentColor"/>/<see cref="TextColor"/>/<see cref="Mode"/>/<see cref="SelectedPreset"/>
    /// from the owner and re-raises them, updating every <see cref="ColorPresetOption.IsSelected"/> flag —
    /// called by the owner whenever its underlying colours changed from somewhere other than this editor
    /// (e.g. <c>PanelCustomizeViewModel</c> retargeting to a different panel).
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(TextColor));
        OnPropertyChanged(nameof(Mode));
        RefreshSelection();
    }

    /// <summary>
    /// "Choosing an accent" — a preset swatch's <see cref="ColorPresetOption.ApplyCommand"/> or the swatch
    /// grid's own "Custom…" cell (<see cref="PickCustomAccentCommand"/>). Writes BOTH colours atomically
    /// through <see cref="_setBoth"/>: <paramref name="accentHex"/> itself, plus a text colour derived from
    /// the CURRENT <see cref="Mode"/> — <see cref="PanelColorPreset.TextFor"/> when <paramref name="accentHex"/>
    /// matches a preset, <see cref="PanelThemeRules.DefaultTextFor"/> otherwise (a custom, non-preset accent).
    /// </summary>
    /// <param name="accentHex">The accent colour to apply, canonical uppercase <c>#RRGGBB</c>.</param>
    public void ApplyAccent(string accentHex)
    {
        var mode = Mode;
        var preset = PanelColorPresets.FindByAccent(accentHex);
        var text = preset?.TextFor(mode) ?? PanelThemeRules.DefaultTextFor(mode);

        // m11 (backlog 1.x item 4, §4.2b): a re-application is a no-op only when BOTH the accent AND the
        // EXPECTED paired text already match — not the accent alone. Comparing the accent only (the
        // original m11 guard) meant a panel whose accent already matched a preset but whose text had
        // drifted from that preset's own pair (a stale pre-#4 pairing, or a manual Advanced-card edit)
        // could never have the preset's text re-applied by clicking the very same swatch again.
        if (string.Equals(accentHex, AccentColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(text, TextColor, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _setBoth(accentHex, text);
        Refresh();
    }

    /// <summary>
    /// The Mode segmented control's handler — writes TEXT ONLY (the accent never changes here): re-derives
    /// it from whatever preset the CURRENT <see cref="AccentColor"/> matches, evaluated for
    /// <paramref name="mode"/> (<see cref="PanelColorPreset.TextFor"/>), or <see cref="PanelThemeRules.DefaultTextFor"/>
    /// when the current accent matches no preset.
    /// </summary>
    /// <param name="mode">The mode to switch to.</param>
    public void SetMode(PanelThemeMode mode)
    {
        // M3: switching to the mode already in effect writes nothing — same "unchanged edit is a no-op"
        // guard this class's other write paths already apply.
        if (mode == Mode) return;

        var preset = SelectedPreset;
        var text = preset?.TextFor(mode) ?? PanelThemeRules.DefaultTextFor(mode);

        _setText(text);
        Refresh();
    }

    private void PickCustomAccent()
    {
        var picked = _colorPicker.PickColor(AccentColor, PanelColorPresets.CustomDialogSeed);

        // Same "cancelled or unchanged" no-op guard this app has always applied to a "Custom…" picker
        // (case-insensitive: the dialog always returns uppercase "#RRGGBB", the current value could be any case).
        if (picked is null || string.Equals(picked, AccentColor, StringComparison.OrdinalIgnoreCase)) return;

        ApplyAccent(picked);
    }

    private void PickCustomText()
    {
        var picked = _colorPicker.PickColor(TextColor, PanelColorPresets.CustomDialogSeed);

        if (picked is null || string.Equals(picked, TextColor, StringComparison.OrdinalIgnoreCase)) return;

        TextColor = picked;
    }

    private void Reset()
    {
        if (_reset is not null)
        {
            _reset();
        }
        else
        {
            var defaultPreset = PanelColorPresets.Default;
            _setBoth(defaultPreset.AccentHex, defaultPreset.TextFor(PanelThemeMode.Dark));
        }

        Refresh();
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(IsCustomSelected));

        var selected = SelectedPreset;
        var mode = Mode;
        var currentText = TextColor;
        foreach (var option in Presets)
        {
            var isSelected = option.Preset.Equals(selected);
            option.IsSelected = isSelected;

            // Backlog 1.x item 4 (§4.2c/§4.2 "Выделение свотча"): every swatch previews its OWN paired
            // text for the CURRENT mode (not just the accent), and the SELECTED swatch additionally flags
            // itself "modified" when the panel's actual text has drifted from that exact pair — the same
            // drift ApplyAccent's own no-op guard above now re-applies on the next click.
            option.UpdatePreviewText(mode);
            option.SetModified(isSelected && !string.Equals(currentText, option.Preset.TextFor(mode), StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
