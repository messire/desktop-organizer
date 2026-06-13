using Microsoft.Win32;

namespace DesktopOrganizer.UI.Services;

/// <inheritdoc />
internal class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopOrganizer";

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Autostart read failed: {ex.Message}");
                return false;
            }
        }

        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null) return;

                if (value)
                {
                    var path = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(path))
                        key.SetValue(ValueName, $"\"{path}\"");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Autostart write failed: {ex.Message}");
            }
        }
    }
}
