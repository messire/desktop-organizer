using System.ComponentModel;
using DesktopOrganizer.Services.Abstractions;

// "Color" is ambiguous between System.Drawing.Color and System.Windows.Media.Color — both are global
// usings in this project (GlobalUsings.cs). This file only ever deals with the WinForms/GDI colour type.
using Color = System.Drawing.Color;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// <see cref="System.Windows.Forms.ColorDialog"/>-backed <see cref="IColorPickerService"/>
/// (docs/PLANS/color-presets-and-size-step.md §4.1). Deliberately does not call
/// <c>Application.EnableVisualStyles()</c> — that is global WinForms state and would also change the tray
/// context menu's (<c>NotifyIconAdapter</c>/<c>TrayIconManager</c>) rendering; the classic-button look of
/// <c>ChooseColor</c> without comctl v6 is accepted (ADR-037, plan §0.3, R-3).
/// </summary>
internal sealed class ColorPickerService(IAppLog appLog) : IColorPickerService
{
    /// <inheritdoc/>
    public string? PickColor(string? initialHex, IReadOnlyList<string> customColors)
    {
        var start = ColorDialogColor.TryParseHex(initialHex, out var parsed) ? parsed : Color.White;

        try
        {
            using var dialog = new ColorDialog
            {
                AllowFullOpen = true,
                FullOpen = true,
                AnyColor = true,
                SolidColorOnly = true,
                Color = start,

                // MINOR-2 fix (review round): customColors is declared non-nullable, but this is the
                // boundary of an interface any caller can implement/mock — BuildCustomColors is itself
                // null-safe, same "never throws" contract as the rest of this class.
                CustomColors = ColorDialogColor.BuildCustomColors(customColors),
            };

            return dialog.ShowDialog() == DialogResult.OK ? ColorDialogColor.ToHex(dialog.Color) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException or ThreadStateException or ArgumentException)
        {
            // MINOR-2 fix (review round): ThreadStateException (ShowDialog off an STA thread) and
            // ArgumentException (a malformed CustomColors/owner argument) are both realistic ColorDialog
            // failure modes alongside the three already handled — none of them should ever crash the
            // caller (PanelCustomizeWindow/GlobalDefaults), only degrade to "no colour picked".
            appLog.Warn("ColorPicker", "ShowFailed", new KeyValuePair<string, object?>("exceptionType", ex.GetType().Name));
            return null;
        }
    }
}
