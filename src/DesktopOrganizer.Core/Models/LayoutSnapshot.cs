namespace DesktopOrganizer.Core;

/// <summary>
/// Represents a named point-in-time snapshot of the entire desktop layout.
/// Stores positions, sizes, page assignments and file lists of all panels.
/// </summary>
public class LayoutSnapshot
{
    /// <summary>
    /// Unique identifier of this snapshot.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Human-readable name chosen by the user.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the snapshot was captured.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Serialised copies of every panel at capture time.
    /// Each entry is a raw JSON string representing a <c>ChildWindowModel</c>.
    /// Stored as JSON strings so the Core layer has no dependency on the UI layer.
    /// </summary>
    public List<string> PanelJsonItems { get; set; } = [];
}
