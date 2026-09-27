using System.ComponentModel;
using System.Windows.Input;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// One selectable swatch in <see cref="PanelColorEditorViewModel.Presets"/> — wraps a single
/// <see cref="Theming.PanelColorPreset"/> with the mutable <see cref="IsSelected"/> state the swatch grid's
/// <c>RadioButton</c>s bind against (ADR-042). Unlike the pre-ADR-042 shape, a preset no longer carries a
/// single display text colour of its own — the swatch renders as a plain accent-filled circle (tooltip =
/// localized name), and applying it derives the actual text colour from the editor's CURRENT
/// <see cref="PanelThemeMode"/> via <see cref="PanelColorPreset.TextFor"/> (see
/// <see cref="PanelColorEditorViewModel.ApplyAccent"/>), not a value this option itself exposes.
/// </summary>
public sealed class ColorPresetOption : INotifyPropertyChanged
{
    private readonly Action _apply;
    private bool _isSelected;

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

    /// <summary>Applies this preset's accent colour — see <see cref="PanelColorEditorViewModel.ApplyAccent"/> for the exact write shape.</summary>
    public ICommand ApplyCommand { get; }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
