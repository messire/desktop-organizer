namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Result of <see cref="IWindowManager.RemoveChildWindow(Guid, bool, bool)"/> (ADR-035): either the panel
/// was removed (<see cref="IsRemoved"/>), or it was <b>kept</b> because some of its app-managed items
/// could not be moved to the Recycle Bin (<see cref="IsBlocked"/>, with those items in
/// <see cref="Remaining"/>). Nothing is ever deleted permanently to make a removal succeed.
/// </summary>
/// <remarks>
/// <c>default(PanelRemovalResult)</c> equals <see cref="Removed"/>, so a loose test double returning the
/// default value behaves like the pre-ADR-035 "always removed" contract.
/// </remarks>
public readonly record struct PanelRemovalResult
{
    private readonly IReadOnlyList<string>? _remaining;

    private PanelRemovalResult(IReadOnlyList<string>? remaining)
    {
        _remaining = remaining;
    }

    /// <summary>Gets the result for a panel that was removed (or didn't exist).</summary>
    public static PanelRemovalResult Removed => default;

    /// <summary>
    /// Gets the managed item paths that are still on disk (and still in the kept panel) because the
    /// Recycle Bin refused them. Empty when <see cref="IsRemoved"/>.
    /// </summary>
    public IReadOnlyList<string> Remaining => _remaining ?? [];

    /// <summary>Gets a value indicating whether the panel was kept because some managed items could not be recycled.</summary>
    public bool IsBlocked => Remaining.Count > 0;

    /// <summary>Gets a value indicating whether the panel was removed.</summary>
    public bool IsRemoved => !IsBlocked;

    /// <summary>
    /// Creates the result for a panel that was kept because <paramref name="remaining"/> could not be recycled.
    /// </summary>
    /// <param name="remaining">The managed paths still on disk. An empty list yields <see cref="Removed"/>.</param>
    /// <returns>The blocked result.</returns>
    public static PanelRemovalResult Blocked(IReadOnlyList<string> remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        return new PanelRemovalResult(remaining);
    }
}
