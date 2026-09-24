using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default <see cref="ILayoutEditModeService"/> implementation. Registered as a DI singleton (see
/// <c>DiConfig.ConfigureServices</c>) — one instance for the whole process, published as the
/// <c>LayoutEditMode</c> application resource (D-12) and shared by every <c>ChildWindow</c>'s
/// <c>ModeChanged</c> subscription (D-4).
/// </summary>
/// <remarks>
/// Not thread-safe: like <see cref="PanelSelectionCoordinator"/>, this service is only ever touched from
/// the UI thread (menu clicks, keyboard shortcuts, settings-window bindings) — no synchronization is
/// used or needed. <see cref="IsLayoutLocked"/> defers to the injected <see cref="IAppSettings"/> as its
/// single source of truth rather than caching a private copy, so it always reflects whatever the rest of
/// the app (e.g. a future settings-window binding writing directly to <see cref="IAppSettings"/>) last
/// wrote there. <see cref="Mode"/>, by contrast, is pure in-memory state — it is never read from or
/// written to <see cref="IAppSettings"/> (D-8/R-11): every instance starts in
/// <see cref="LayoutInteractionMode.Normal"/> regardless of what the previous session ended in.
/// </remarks>
public sealed class LayoutEditModeService : ILayoutEditModeService
{
    private readonly IAppSettings _appSettings;
    private readonly IGestureDiagnostics _diagnostics;
    private LayoutInteractionMode _mode = LayoutInteractionMode.Normal;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutEditModeService"/> class.
    /// </summary>
    /// <param name="appSettings">Backs <see cref="IsLayoutLocked"/> (D-8). <see cref="Mode"/> itself is never read from or written to this.</param>
    /// <param name="diagnostics">Recipient of <see cref="IGestureDiagnostics.LayoutModeChanged"/> on every real transition (D-11).</param>
    public LayoutEditModeService(IAppSettings appSettings, IGestureDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(appSettings);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _appSettings = appSettings;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public event EventHandler<LayoutModeChangedEventArgs>? ModeChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public LayoutInteractionMode Mode => _mode;

    /// <inheritdoc />
    public bool IsLayoutLocked
    {
        get => _appSettings.IsLayoutLocked;
        set
        {
            if (_appSettings.IsLayoutLocked == value) return;

            _appSettings.IsLayoutLocked = value;
            OnPropertyChanged(nameof(IsLayoutLocked));
            OnPropertyChanged(nameof(CanEnterEditLayout));

            // D-3/R-9: locking while editing must never leave the service in the contradictory
            // "locked but editing" state — force an immediate exit, through the same transition path
            // (and diagnostics/event contract) as an ordinary ExitEditLayout call.
            if (value && _mode == LayoutInteractionMode.EditLayout)
            {
                TransitionTo(LayoutInteractionMode.Normal, LayoutModeChangeSource.Programmatic);
            }
        }
    }

    /// <inheritdoc />
    public bool CanEnterEditLayout => !IsLayoutLocked;

    /// <inheritdoc />
    public bool IsEditing => _mode == LayoutInteractionMode.EditLayout;

    /// <inheritdoc />
    public bool TryEnterEditLayout(LayoutModeChangeSource source)
    {
        if (IsLayoutLocked) return false;

        // No-op re-entry into the mode already active: succeeds without raising anything.
        if (_mode == LayoutInteractionMode.EditLayout) return true;

        TransitionTo(LayoutInteractionMode.EditLayout, source);
        return true;
    }

    /// <inheritdoc />
    public void ExitEditLayout(LayoutModeChangeSource source)
    {
        if (_mode == LayoutInteractionMode.Normal) return;

        TransitionTo(LayoutInteractionMode.Normal, source);
    }

    /// <inheritdoc />
    public bool Toggle(LayoutModeChangeSource source)
    {
        if (_mode == LayoutInteractionMode.Normal)
        {
            return TryEnterEditLayout(source);
        }

        ExitEditLayout(source);
        return true;
    }

    /// <summary>
    /// Applies a real <see cref="Mode"/> transition (the caller has already ruled out every no-op case)
    /// — updates state, raises <see cref="PropertyChanged"/> for <see cref="Mode"/>/<see cref="IsEditing"/>,
    /// then <see cref="ModeChanged"/>, then <see cref="IGestureDiagnostics.LayoutModeChanged"/>, exactly
    /// once each (D-11).
    /// </summary>
    private void TransitionTo(LayoutInteractionMode newMode, LayoutModeChangeSource source)
    {
        var previous = _mode;
        _mode = newMode;

        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsEditing));

        ModeChanged?.Invoke(this, new LayoutModeChangedEventArgs(previous, newMode, source));
        _diagnostics.LayoutModeChanged(previous, newMode, source, IsLayoutLocked);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
