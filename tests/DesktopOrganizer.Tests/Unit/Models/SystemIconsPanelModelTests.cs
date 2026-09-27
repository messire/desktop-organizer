using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="SystemIconsPanelModel.RefreshLabels"/> (docs/PLANS/localization.md §2.9).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SystemIconsPanelModelTests
{
    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelModel.RefreshLabels"/> re-resolves every item's
    /// <see cref="SystemIconItemModel.Label"/> via the supplied resolver, keyed by <see cref="SystemIconItemModel.Kind"/>.
    /// </summary>
    [Fact]
    public void RefreshLabels_SetsEachItemLabel_FromResolver()
    {
        var model = new SystemIconsPanelModel();

        model.RefreshLabels(kind => $"[{kind}]");

        Assert.All(model.Items, item => Assert.Equal($"[{item.Kind}]", item.Label));
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelModel.RefreshLabels"/> mutates the existing
    /// <see cref="SystemIconItemModel"/> instances in place rather than replacing
    /// <see cref="SystemIconsPanelModel.Items"/> — bindings (e.g. the panel window's <c>IconsListBox</c>)
    /// must keep pointing at the same objects across a language switch.
    /// </summary>
    [Fact]
    public void RefreshLabels_DoesNotReplaceItemInstances()
    {
        var model = new SystemIconsPanelModel();
        var originalInstances = model.Items.ToList();

        model.RefreshLabels(kind => kind.ToString());

        Assert.Equal(originalInstances, model.Items, ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelModel.RefreshLabels"/> throws for a
    /// <see langword="null"/> resolver rather than silently no-oping.
    /// </summary>
    [Fact]
    public void RefreshLabels_NullResolver_Throws()
    {
        var model = new SystemIconsPanelModel();

        Assert.Throws<ArgumentNullException>(() => model.RefreshLabels(null!));
    }
}
