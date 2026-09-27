namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Shows the system RGB colour-picker dialog (docs/PLANS/color-presets-and-size-step.md §3.5, §4.1) — the
/// "Custom…" entry point for a panel's accent/text colour, secondary to the ten <c>PanelColorPresets</c>.
/// The concrete implementation wraps <c>System.Windows.Forms.ColorDialog</c> in
/// <c>DesktopOrganizer.Services</c> (the only project with <c>UseWindowsForms</c> enabled); this interface
/// itself carries no WinForms dependency, so <c>DesktopOrganizer.UI</c> ViewModels only ever see the
/// abstraction.
/// </summary>
public interface IColorPickerService
{
    /// <summary>
    /// Shows the system colour-picker dialog, modal to the calling thread's currently active window (this
    /// method never receives or uses an owner HWND — see the plan's §0.2 rationale). Never throws: any
    /// failure to show the dialog is logged and treated the same as a user cancellation.
    /// </summary>
    /// <param name="initialHex">
    /// The colour to preselect, as <c>#RGB</c>/<c>#RRGGBB</c>/<c>#AARRGGBB</c> (alpha, if present, is
    /// ignored). <see langword="null"/> or unparsable input starts the dialog on white.
    /// </param>
    /// <param name="customColors">
    /// Up to 16 hex colours to seed the dialog's custom-color slots with. Entries beyond the 16th are
    /// discarded; entries that fail to parse are skipped (not counted against the 16-slot limit).
    /// </param>
    /// <returns>
    /// The chosen colour as canonical uppercase <c>#RRGGBB</c>, or <see langword="null"/> if the user
    /// cancelled the dialog or it could not be shown.
    /// </returns>
    string? PickColor(string? initialHex, IReadOnlyList<string> customColors);
}
