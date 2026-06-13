namespace DesktopOrganizer.Core.Models;

/// <summary>
/// A rule that maps matching files to a target desktop panel.
/// </summary>
public class SortingRule
{
    /// <summary>Stable unique identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The criterion used to match a file.</summary>
    public SortingCriterion Criterion { get; set; }

    /// <summary>
    /// Pattern value: an extension like ".pdf", or a substring like "Report".
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Id of the panel the matched file should be placed in.</summary>
    public string TargetPanelId { get; set; } = string.Empty;

    /// <summary>Whether the comparison is case-insensitive. Defaults to <c>true</c>.</summary>
    public bool IgnoreCase { get; set; } = true;
}
