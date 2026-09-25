using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// The single place <see cref="SystemIconsPanelModel"/> meets the storage schema's
/// <see cref="SystemIconsPanelStateDto"/> (ADR-024 D-3/§2.3, C6) — replaces the mapping that used to
/// live inline inside <c>SystemIconsPanelManager.Load</c>/<c>Save</c>.
/// </summary>
internal static class SystemIconsPanelStateMapper
{
    /// <summary>Maps the live <see cref="SystemIconsPanelModel"/> to its schema-v3 DTO.</summary>
    /// <param name="model">The system-icons panel model to map.</param>
    public static SystemIconsPanelStateDto ToDto(SystemIconsPanelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new SystemIconsPanelStateDto
        {
            IsEnabled = model.IsEnabled,
            ShowOnAllVirtualDesktops = model.ShowOnAllVirtualDesktops,
            IconSize = model.IconSize.ToString(),
            AnchorVirtualDesktopId = model.AnchorVirtualDesktopId?.ToString(),
            Items = model.Items.Select(item => new SystemIconsPanelItemStateDto
            {
                Kind = item.Kind,
                IsVisible = item.IsVisible,
                Order = item.Order,
            }).ToList(),
        };
    }

    /// <summary>
    /// Applies <paramref name="dto"/> onto <paramref name="model"/> in place — mirrors the former
    /// <c>SystemIconsPanelManager.Load</c> deserialisation logic exactly, including its
    /// per-field-isolated defaults (a single bad field must never discard the rest — see
    /// <see cref="Dto.SystemIconsPanelStateDto"/>'s remarks on <c>AnchorVirtualDesktopId</c>).
    /// </summary>
    /// <param name="dto">The persisted system-icons panel DTO to apply.</param>
    /// <param name="model">The live model to update in place.</param>
    public static void ApplyTo(SystemIconsPanelStateDto dto, SystemIconsPanelModel model)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(model);

        model.IsEnabled = dto.IsEnabled;

        // Key absent in a document written by an older build -> stays false. No migration needed.
        model.ShowOnAllVirtualDesktops = dto.ShowOnAllVirtualDesktops;

        if (Enum.TryParse<SystemIconSize>(dto.IconSize, out var size))
        {
            model.IconSize = size;
        }

        // TryParse never throws, so a garbage or absent string degrades to "no anchor" without
        // touching anything else read above/below.
        model.AnchorVirtualDesktopId =
            Guid.TryParse(dto.AnchorVirtualDesktopId, out var anchor) ? anchor : null;

        if (dto.Items is not { Count: > 0 })
        {
            return;
        }

        foreach (var itemDto in dto.Items)
        {
            var existing = model.Items.FirstOrDefault(i => i.Kind == itemDto.Kind);
            if (existing is null) continue;
            existing.IsVisible = itemDto.IsVisible;
            existing.Order = itemDto.Order;
        }

        // Re-sort by persisted order.
        var sorted = model.Items.OrderBy(i => i.Order).ToList();
        model.Items.Clear();
        foreach (var item in sorted)
        {
            model.Items.Add(item);
        }
    }
}
