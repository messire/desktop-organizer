using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal class DesktopIconsService(INativeApi nativeApi) : IDesktopIconsService
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    /// <inheritdoc />
    public bool AreIconsVisible
    {
        get
        {
            var defView = FindDefView();
            return defView != IntPtr.Zero && nativeApi.IsWindowVisible(defView);
        }
    }

    /// <inheritdoc />
    public DesktopIconsVisibility QueryIconsVisibility()
    {
        try
        {
            var defView = FindDefView();
            if (defView == IntPtr.Zero) return DesktopIconsVisibility.Unknown;

            // Checked before the window: whatever SHELLDLL_DefView looks like, an Explorer-level "off"
            // is the user's decision and outranks any conclusion drawn from it.
            if (nativeApi.IsExplorerDesktopIconsSettingOff()) return DesktopIconsVisibility.HiddenByExplorerSetting;

            return nativeApi.IsWindowVisible(defView) ? DesktopIconsVisibility.Visible : DesktopIconsVisibility.Hidden;
        }
        catch
        {
            // Explorer half-way through (re)starting: "don't know" is the only safe answer.
            return DesktopIconsVisibility.Unknown;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Session-only (ADR-034): written by <see cref="SetIconsVisible"/> (and therefore by
    /// <see cref="Toggle"/>), read by <see cref="ReapplyDesiredVisibility"/>. Never persisted.
    /// </remarks>
    public bool? DesiredVisible { get; private set; }

    /// <inheritdoc />
    public event EventHandler? VisibilityChanged;

    /// <inheritdoc />
    public void SetIconsVisible(bool visible)
    {
        // Recorded before the shell call, and even when SHELLDLL_DefView cannot be found right now:
        // the intent is what an Explorer restart has to restore, not whatever the old window did.
        DesiredVisible = visible;

        var defView = FindDefView();
        if (defView != IntPtr.Zero)
            nativeApi.ShowWindow(defView, visible ? SwShow : SwHide);

        // Raised even when the intent was merely recorded (no DefView yet): the mirrors re-read
        // AreIconsVisible themselves, so a spurious notification costs one lookup, while a missed one
        // leaves a checkmark stale.
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Toggle() => SetIconsVisible(!AreIconsVisible);

    /// <inheritdoc />
    public bool ReapplyDesiredVisibility()
    {
        // No intent, or "visible": a freshly created SHELLDLL_DefView is visible already, and forcing
        // SW_SHOW here could override the user's own Explorer "Show desktop icons" choice.
        if (DesiredVisible is not false) return true;

        try
        {
            var defView = FindDefView();
            if (defView == IntPtr.Zero) return false;

            // ShowWindow's return value is the PREVIOUS visibility, not success — don't read it.
            nativeApi.ShowWindow(defView, SwHide);
        }
        catch
        {
            // Explorer may be half-way through coming back; the caller retries within its bounded
            // window, so degrading to "not yet" is the right answer here.
            return false;
        }

        // Outside the try: a throwing UI handler must surface, not be mistaken for "DefView not yet
        // there" and retried.
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Locates the icon container window (SHELLDLL_DefView). It usually lives under Progman,
    /// but after a WorkerW spawn it can sit under one of the WorkerW windows instead.
    /// </summary>
    private IntPtr FindDefView()
    {
        var progman = nativeApi.FindWindow("Progman", null);
        var defView = nativeApi.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return defView;

        IntPtr found = IntPtr.Zero;
        nativeApi.EnumWindows(
            (hwnd, _) =>
        {
            var child = nativeApi.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero) found = child;
            return true;
        }, IntPtr.Zero);

        return found;
    }
}
