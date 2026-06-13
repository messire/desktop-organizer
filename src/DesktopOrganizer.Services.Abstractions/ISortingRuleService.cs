using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Manages automatic sorting rules that move files between panels based on criteria.
/// </summary>
public interface ISortingRuleService
{
    /// <summary>Current set of rules in evaluation order.</summary>
    IReadOnlyList<SortingRule> Rules { get; }

    /// <summary>Appends a new rule to the list and persists.</summary>
    void AddRule(SortingRule rule);

    /// <summary>Removes the rule with the given <paramref name="ruleId"/> and persists.</summary>
    void RemoveRule(string ruleId);

    /// <summary>Replaces the rule whose Id matches and persists.</summary>
    void UpdateRule(SortingRule rule);

    /// <summary>
    /// Evaluates all rules against <paramref name="filePath"/> and returns the
    /// <see cref="SortingRule.TargetPanelId"/> of the first matching rule, or
    /// <c>null</c> when no rule matches.
    /// </summary>
    string? FindTargetPanel(string filePath);

    /// <summary>
    /// Iterates all non-portal panels and moves each file item to the panel
    /// indicated by the first matching rule.  Portal panels (those with a
    /// source folder path set) are skipped.
    /// </summary>
    void ApplyRules();

    /// <summary>Persists current rules to application settings.</summary>
    void Save();

    /// <summary>Loads rules from application settings.</summary>
    void Load();
}
