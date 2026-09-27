namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// What <see cref="IPanelItemStorageService.RecycleManagedItem"/> did with a managed panel item.
/// </summary>
/// <remarks>
/// <c>default(ManagedRecycleKind)</c> is <see cref="NotManaged"/> ("nothing happened"), so a loose test double
/// returning the default value never makes a caller drop an item from its panel.
/// </remarks>
public enum ManagedRecycleKind
{
    /// <summary>The path is not a single managed item (outside managed storage, the storage root, a bare per-item directory). Nothing was touched.</summary>
    NotManaged,

    /// <summary>The item is gone: it is in the Recycle Bin (or was already gone). Its manifest entry was released.</summary>
    Recycled,

    /// <summary>
    /// Nothing was deleted — the shell did not recycle the item (the user cancelled, declined the "delete
    /// permanently?" warning, or the operation failed). The item still exists at
    /// <see cref="ManagedRecycleResult.Path"/>, which may differ from the path passed in (see
    /// <see cref="IPanelItemStorageService.RecycleManagedItem"/>).
    /// </summary>
    Kept,
}

/// <summary>
/// Result of <see cref="IPanelItemStorageService.RecycleManagedItem"/>.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Path">Where the item is now when <paramref name="Kind"/> is <see cref="ManagedRecycleKind.Kept"/>; otherwise <see langword="null"/>.</param>
public readonly record struct ManagedRecycleResult(ManagedRecycleKind Kind, string? Path);
