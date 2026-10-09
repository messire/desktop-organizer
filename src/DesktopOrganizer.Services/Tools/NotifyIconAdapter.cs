using System.IO;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Windows Forms <see cref="NotifyIcon"/>-backed <see cref="INotifyIconAdapter"/>.
/// </summary>
internal class NotifyIconAdapter : INotifyIconAdapter
{
    /// <summary>The longest text <see cref="NotifyIcon.Text"/> accepts.</summary>
    internal const int MaxToolTipLength = 127;

    private readonly NotifyIcon _notifyIcon = new () { Visible = true, };

    // Tracks the GDI icon handle currently assigned to the tray icon so it can be disposed once
    // it is no longer referenced by NotifyIcon — SetIcon previously leaked one native icon handle
    // per call.
    private Icon? _currentIcon;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler DoubleClick
    {
        add => _notifyIcon.DoubleClick += value;
        remove => _notifyIcon.DoubleClick -= value;
    }

    /// <inheritdoc/>
    public void SetIcon(string iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            throw new FileNotFoundException("Icon file not found.", iconPath);
        }

        Icon newIcon;
        try
        {
            // File.Exists above is only a best-effort check — the file can still disappear or be
            // mid-write by the time the constructor reads it, which throws IOException rather than
            // the FileNotFoundException this method's contract documents. Normalize that race to
            // the documented exception type instead of letting an unrelated exception escape.
            newIcon = new Icon(iconPath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new FileNotFoundException("Icon file not found or could not be read.", iconPath, ex);
        }

        var previousIcon = _currentIcon;

        // Assign the new icon before disposing the previous one, so NotifyIcon never
        // momentarily holds a reference to an already-destroyed handle.
        _notifyIcon.Icon = newIcon;
        _currentIcon = newIcon;
        previousIcon?.Dispose();
    }

    /// <inheritdoc/>
    public void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon)
        => _notifyIcon.ShowBalloonTip(timeout, title, text, icon);

    /// <inheritdoc/>
    public void SetToolTip(string text)
        => _notifyIcon.Text = TruncateToolTip(text);

    /// <inheritdoc/>
    public void SetContextMenu(ContextMenuStrip menu)
        => _notifyIcon.ContextMenuStrip = menu;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }

    /// <summary>
    /// <see cref="NotifyIcon.Text"/> throws <see cref="ArgumentOutOfRangeException"/> for more than
    /// <see cref="MaxToolTipLength"/> characters (the Win32 <c>NOTIFYICONDATA.szTip</c> limit) - cut the text to
    /// fit rather than letting a long localized tooltip take the tray icon down.
    /// </summary>
    /// <param name="text">The requested tooltip (may be <see langword="null"/> from a loose caller).</param>
    /// <returns>The text, at most <see cref="MaxToolTipLength"/> characters long.</returns>
    internal static string TruncateToolTip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= MaxToolTipLength) return text;

        // Do not leave a lone high surrogate at the cut.
        var length = char.IsHighSurrogate(text[MaxToolTipLength - 1]) ? MaxToolTipLength - 1 : MaxToolTipLength;
        return text[..length];
    }
}
