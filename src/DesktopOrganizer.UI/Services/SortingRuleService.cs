using System.IO;
using System.Text.Json;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Implements automatic sorting rules: matches files by criterion and moves them
/// between non-portal panels.
/// </summary>
internal sealed class SortingRuleService : ISortingRuleService
{
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly List<SortingRule> _rules = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Initializes a new instance of <see cref="SortingRuleService"/>.
    /// </summary>
    public SortingRuleService(IAppSettings appSettings, IWindowManager windowManager)
    {
        _appSettings = appSettings;
        _windowManager = windowManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<SortingRule> Rules => _rules.AsReadOnly();

    /// <inheritdoc />
    public void AddRule(SortingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(rule);
        Save();
    }

    /// <inheritdoc />
    public void RemoveRule(string ruleId)
    {
        var existing = _rules.Find(r => r.Id == ruleId);
        if (existing is null) return;
        _rules.Remove(existing);
        Save();
    }

    /// <inheritdoc />
    public void UpdateRule(SortingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var index = _rules.FindIndex(r => r.Id == rule.Id);
        if (index < 0) return;
        _rules[index] = rule;
        Save();
    }

    /// <inheritdoc />
    public string? FindTargetPanel(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath);

        foreach (var rule in _rules)
        {
            var comparison = rule.IgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var matched = rule.Criterion switch
            {
                SortingCriterion.Extension =>
                    string.Equals(extension, rule.Value, comparison),

                SortingCriterion.FileNameContains =>
                    fileName.Contains(rule.Value, comparison),

                SortingCriterion.FileNameStartsWith =>
                    fileName.StartsWith(rule.Value, comparison),

                SortingCriterion.FileNameEndsWith =>
                    fileName.EndsWith(rule.Value, comparison),

                _ => false,
            };

            if (matched && !string.IsNullOrWhiteSpace(rule.TargetPanelId))
                return rule.TargetPanelId;
        }

        return null;
    }

    /// <inheritdoc />
    public void ApplyRules()
    {
        if (_rules.Count == 0) return;

        // Build a lookup from panelId → model for quick access.
        var panels = _windowManager.GetAllChildWindows()
            .ToDictionary(p => p.Id.ToString());

        // Collect moves first to avoid mutating collections while iterating.
        var moves = new List<(FileItem File, Models.ChildWindowModel Source, Models.ChildWindowModel Target)>();

        foreach (var panel in panels.Values)
        {
            // Skip portal panels — their content is managed by the folder watcher.
            if (!string.IsNullOrEmpty(panel.SourceFolderPath)) continue;

            foreach (var file in panel.FileItems.ToList())
            {
                var targetId = FindTargetPanel(file.Path);
                if (targetId is null) continue;
                if (!panels.TryGetValue(targetId, out var target)) continue;

                // Already in the correct panel.
                if (target.Id == panel.Id) continue;

                // Portal targets are also skipped.
                if (!string.IsNullOrEmpty(target.SourceFolderPath)) continue;

                moves.Add((file, panel, target));
            }
        }

        foreach (var (file, source, target) in moves)
        {
            source.FileItems.Remove(file);
            source.FilePaths.Remove(file.Path);

            if (!target.FilePaths.Contains(file.Path))
            {
                target.FilePaths.Add(file.Path);
                target.FileItems.Add(file);
            }
        }

        if (moves.Count > 0)
            _windowManager.SaveWindowsState();
    }

    /// <inheritdoc />
    public void Save()
    {
        _appSettings.SortingRules = JsonSerializer.Serialize(_rules, JsonOptions);
        _appSettings.Save();
    }

    /// <inheritdoc />
    public void Load()
    {
        var json = _appSettings.SortingRules;
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            _rules.Clear();
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<SortingRule>>(json);
            _rules.Clear();
            if (loaded is not null)
                _rules.AddRange(loaded);
        }
        catch (JsonException)
        {
            // Corrupted data — start with empty rule set.
            _rules.Clear();
        }
    }
}
