using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages the lifecycle, persistence, and desktop embedding of the system icons panel.
/// </summary>
internal sealed class SystemIconsPanelManager : ISystemIconsPanelManager, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppSettings _appSettings;
    private readonly IDesktopIntegrationService _desktopIntegration;
    private readonly IMonitorService _monitorService;
    private readonly Func<SystemIconsPanelWindow> _windowFactory;

    private SystemIconsPanelWindow? _window;

    /// <summary>
    /// Initializes a new instance of <see cref="SystemIconsPanelManager"/>.
    /// </summary>
    public SystemIconsPanelManager(
        IAppSettings appSettings,
        IDesktopIntegrationService desktopIntegration,
        IMonitorService monitorService,
        Func<SystemIconsPanelWindow> windowFactory)
    {
        _appSettings = appSettings;
        _desktopIntegration = desktopIntegration;
        _monitorService = monitorService;
        _windowFactory = windowFactory;

        _monitorService.MonitorsChanged += OnMonitorsChanged;
    }

    /// <inheritdoc />
    public SystemIconsPanelModel Model { get; } = new();

    /// <inheritdoc />
    public void Load()
    {
        var json = _appSettings.SystemIconsPanel;
        if (string.IsNullOrWhiteSpace(json))
        {
            // Use built-in defaults already set in Model
            return;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PanelDto>(json, JsonOptions);
            if (dto is null) return;

            Model.IsEnabled = dto.IsEnabled;

            if (Enum.TryParse<SystemIconSize>(dto.IconSize, out var size))
            {
                Model.IconSize = size;
            }

            if (dto.Items is { Count: > 0 })
            {
                foreach (var itemDto in dto.Items)
                {
                    var existing = Model.Items.FirstOrDefault(i => i.Kind == itemDto.Kind);
                    if (existing is null) continue;
                    existing.IsVisible = itemDto.IsVisible;
                    existing.Order = itemDto.Order;
                }

                // Re-sort by persisted order
                var sorted = Model.Items.OrderBy(i => i.Order).ToList();
                Model.Items.Clear();
                foreach (var item in sorted)
                {
                    Model.Items.Add(item);
                }
            }
        }
        catch
        {
            // Malformed JSON — silently fall back to current (default) state
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        var dto = new PanelDto
        {
            IsEnabled = Model.IsEnabled,
            IconSize = Model.IconSize.ToString(),
            Items = Model.Items.Select((item, _) => new ItemDto
            {
                Kind = item.Kind,
                IsVisible = item.IsVisible,
                Order = item.Order,
            }).ToList(),
        };

        _appSettings.SystemIconsPanel = JsonSerializer.Serialize(dto, JsonOptions);
        _appSettings.Save();
    }

    /// <inheritdoc />
    public void Apply()
    {
        if (Model.IsEnabled)
        {
            OpenPanel();
        }
        else
        {
            ClosePanel();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _monitorService.MonitorsChanged -= OnMonitorsChanged;
        ClosePanel();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void OpenPanel()
    {
        // Close any stale instance first (e.g. on monitor change)
        ClosePanel();

        try
        {
            _window = _windowFactory();
            PositionWindow(_window);

            // EmbedInDesktop is called inside SystemIconsPanelWindow.OnSourceInitialized,
            // which fires when Show() triggers HWND creation — BEFORE the first render.
            // This prevents the window from briefly flashing above all other windows.
            _window.Show();
        }
        catch
        {
            // Window creation/embed failure — ensure VM is disposed before nulling field.
            try { _window?.Close(); } catch { }
            _window = null;
        }
    }

    private void ClosePanel()
    {
        if (_window is null) return;
        try { _window.Close(); }
        catch { /* ignore */ }
        _window = null;
    }

    private void PositionWindow(Window window)
    {
        var primary = _monitorService.GetPrimaryMonitor();
        var workArea = primary.WorkArea;

        window.Left = workArea.Left;
        window.Top = workArea.Top;
        window.Height = workArea.Height;
    }

    private void OnMonitorsChanged()
    {
        if (_window is null) return;

        // Re-embed with updated position when primary monitor geometry changes
        try
        {
            PositionWindow(_window);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                _desktopIntegration.EmbedInDesktop(hwnd);
            }
        }
        catch { /* degrade silently */ }
    }

    // -------------------------------------------------------------------------
    // JSON DTOs (private, internal to this class)
    // -------------------------------------------------------------------------

    private sealed class PanelDto
    {
        public bool IsEnabled { get; set; }
        public string IconSize { get; set; } = nameof(SystemIconSize.Medium);
        public List<ItemDto>? Items { get; set; }
    }

    private sealed class ItemDto
    {
        public SystemIconKind Kind { get; set; }
        public bool IsVisible { get; set; }
        public int Order { get; set; }
    }
}
