using System.ComponentModel;
using System.Windows.Input;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// One selectable swatch in <see cref="PanelColorEditorViewModel.Presets"/> — wraps a single
/// <see cref="Theming.PanelColorPreset"/> with the mutable <see cref="IsSelected"/>/<see cref="PreviewTextHex"/>/
/// <see cref="IsModified"/> state the swatch grid's <c>RadioButton</c>s bind against (ADR-042; backlog 1.x
/// item 4, §4.2c). Applying this option derives the actual text colour from the editor's CURRENT
/// <see cref="PanelThemeMode"/> via <see cref="PanelColorPreset.TextFor"/> (see
/// <see cref="PanelColorEditorViewModel.ApplyAccent"/>) — <see cref="PreviewTextHex"/> mirrors that SAME
/// value purely for display, so the swatch itself shows both halves of the (accent, text) pair.
/// </summary>
public sealed class ColorPresetOption : INotifyPropertyChanged
{
    private readonly Action _apply;
    private bool _isSelected;
    private bool _isModified;
    private string _previewTextHex = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="ColorPresetOption"/> class.
    /// </summary>
    /// <param name="preset">The preset this option wraps.</param>
    /// <param name="apply">Invoked by <see cref="ApplyCommand"/> — applies <paramref name="preset"/>'s accent, deriving the paired text colour from the editor's current mode.</param>
    public ColorPresetOption(PanelColorPreset preset, Action apply)
    {
        Preset = preset;
        _apply = apply;
        ApplyCommand = new RelayCommand(() => _apply());
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The preset this option wraps.</summary>
    public PanelColorPreset Preset { get; }

    /// <summary>The RESX key of this preset's localized display name — <see cref="PanelColorPreset.NameKey"/>.</summary>
    public string NameKey => Preset.NameKey;

    /// <summary>The preset's accent colour, canonical uppercase <c>#RRGGBB</c>.</summary>
    public string AccentHex => Preset.AccentHex;

    /// <summary>
    /// Whether this preset's accent exactly matches the currently edited accent — set by
    /// <see cref="PanelColorEditorViewModel"/>, never by this option itself.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    /// <summary>
    /// This preset's OWN text colour for the editor's current <see cref="PanelThemeMode"/>
    /// (<see cref="Theming.PanelColorPreset.TextFor"/>) — set by <see cref="PanelColorEditorViewModel"/> via
    /// <see cref="UpdatePreviewText"/>, never by this option itself. Drives the swatch's small "Aa" text-half
    /// preview (backlog 1.x item 4, §4.2c) so a user can see BOTH colours a preset would apply, not just
    /// its accent.
    /// </summary>
    public string PreviewTextHex
    {
        get => _previewTextHex;
        private set
        {
            if (_previewTextHex == value) return;
            _previewTextHex = value;
            OnPropertyChanged(nameof(PreviewTextHex));
        }
    }

    /// <summary>
    /// Whether this option is the SELECTED swatch (its accent matches the panel's current accent) AND the
    /// panel's actual text no longer matches this preset's own <see cref="PreviewTextHex"/> for the current
    /// mode — e.g. a stale pre-#4 pairing, or a manual Advanced-card text edit. Drives a small "modified"
    /// marker on the swatch (§4.2 "Выделение свотча"); clicking <see cref="ApplyCommand"/> again re-applies
    /// this preset's own pair, per <see cref="PanelColorEditorViewModel.ApplyAccent"/>'s no-op guard. Set by
    /// <see cref="PanelColorEditorViewModel"/> via <see cref="SetModified"/>, never by this option itself.
    /// </summary>
    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (_isModified == value) return;
            _isModified = value;
            OnPropertyChanged(nameof(IsModified));
        }
    }

    /// <summary>Applies this preset's accent colour — see <see cref="PanelColorEditorViewModel.ApplyAccent"/> for the exact write shape.</summary>
    public ICommand ApplyCommand { get; }

    /// <summary>Recomputes <see cref="PreviewTextHex"/> from this option's <see cref="Preset"/> for <paramref name="mode"/> — called by <see cref="PanelColorEditorViewModel"/> whenever the editor's mode may have changed.</summary>
    /// <param name="mode">The editor's current <see cref="PanelThemeMode"/>.</param>
    internal void UpdatePreviewText(PanelThemeMode mode) => PreviewTextHex = Preset.TextFor(mode);

    /// <summary>Sets <see cref="IsModified"/> — called by <see cref="PanelColorEditorViewModel"/> whenever selection/mode/text may have changed.</summary>
    /// <param name="isModified">The new <see cref="IsModified"/> value.</param>
    internal void SetModified(bool isModified) => IsModified = isModified;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
