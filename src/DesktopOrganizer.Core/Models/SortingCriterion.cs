namespace DesktopOrganizer.Core.Models;

/// <summary>
/// Defines the criteria used to match a file for automatic sorting.
/// </summary>
public enum SortingCriterion
{
    /// <summary>File extension (e.g. ".pdf").</summary>
    Extension,

    /// <summary>File name contains the specified substring.</summary>
    FileNameContains,

    /// <summary>File name starts with the specified substring.</summary>
    FileNameStartsWith,

    /// <summary>File name ends with the specified substring.</summary>
    FileNameEndsWith,
}
