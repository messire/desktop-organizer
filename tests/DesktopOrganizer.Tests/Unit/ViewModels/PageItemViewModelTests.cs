using System.ComponentModel;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="PageItemViewModel"/> (ADR-043, docs/PLANS/page-display-names.md §5.2/§8.5).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PageItemViewModelTests
{
    /// <summary>The constructor sets <see cref="PageItemViewModel.Id"/>/<see cref="PageItemViewModel.Name"/> verbatim.</summary>
    [Fact]
    public void Constructor_SetsIdAndName()
    {
        var sut = new PageItemViewModel("work", "Work");

        Assert.Equal("work", sut.Id);
        Assert.Equal("Work", sut.Name);
    }

    /// <summary>Only the <c>"default"</c> id reports <see cref="PageItemViewModel.IsDefault"/>.</summary>
    /// <param name="id">The page identifier to construct <see cref="PageItemViewModel"/> with.</param>
    /// <param name="expected">The expected <see cref="PageItemViewModel.IsDefault"/> value for <paramref name="id"/>.</param>
    [Theory]
    [InlineData("default", true)]
    [InlineData("work", false)]
    public void IsDefault_ReflectsId(string id, bool expected)
    {
        var sut = new PageItemViewModel(id, null);

        Assert.Equal(expected, sut.IsDefault);
    }

    /// <summary><see cref="PageItemViewModel.Display"/> carries <see cref="PageItemViewModel.Id"/>/<see cref="PageItemViewModel.Name"/> verbatim.</summary>
    [Fact]
    public void Display_ReflectsIdAndName()
    {
        var sut = new PageItemViewModel("work", "Work");

        Assert.Equal(new PageDisplayInfo("work", "Work"), sut.Display);
    }

    /// <summary>Setting <see cref="PageItemViewModel.Name"/> to a different value raises <see cref="PageItemViewModel.PropertyChanged"/> for both <c>Name</c> and <c>Display</c>.</summary>
    [Fact]
    public void Name_SetToDifferentValue_RaisesPropertyChangedForNameAndDisplay()
    {
        var sut = new PageItemViewModel("work", "Work");
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.Name = "Renamed";

        Assert.Equal("Renamed", sut.Name);
        Assert.Equal(new PageDisplayInfo("work", "Renamed"), sut.Display);
        Assert.Contains(nameof(PageItemViewModel.Name), raised);
        Assert.Contains(nameof(PageItemViewModel.Display), raised);
    }

    /// <summary>Setting <see cref="PageItemViewModel.Name"/> to its current value is a no-op — no <see cref="PageItemViewModel.PropertyChanged"/>.</summary>
    [Fact]
    public void Name_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var sut = new PageItemViewModel("work", "Work");
        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        sut.Name = "Work";

        Assert.False(raised);
    }

    /// <summary>Setting <see cref="PageItemViewModel.IsEditing"/> raises <see cref="PageItemViewModel.PropertyChanged"/> exactly once per actual change.</summary>
    [Fact]
    public void IsEditing_SetToDifferentValue_RaisesPropertyChangedOnce()
    {
        var sut = new PageItemViewModel("work", "Work");
        var raisedCount = 0;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageItemViewModel.IsEditing)) raisedCount++;
        };

        sut.IsEditing = true;
        sut.IsEditing = true; // no-op, same value again.

        Assert.True(sut.IsEditing);
        Assert.Equal(1, raisedCount);
    }

    /// <summary>Setting <see cref="PageItemViewModel.EditingName"/> raises <see cref="PageItemViewModel.PropertyChanged"/>.</summary>
    [Fact]
    public void EditingName_Set_RaisesPropertyChanged()
    {
        var sut = new PageItemViewModel("work", "Work");
        var raised = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageItemViewModel.EditingName)) raised = true;
        };

        sut.EditingName = "Wor";

        Assert.Equal("Wor", sut.EditingName);
        Assert.True(raised);
    }

    /// <summary>
    /// <see cref="PageItemViewModel.HasRenameError"/> reflects whether <see cref="PageItemViewModel.RenameError"/>
    /// is set, and both raise <see cref="PageItemViewModel.PropertyChanged"/> together.
    /// </summary>
    [Fact]
    public void RenameError_SetToNonNull_SetsHasRenameErrorAndRaisesBothPropertyChanged()
    {
        var sut = new PageItemViewModel("work", "Work");
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.RenameError = PageRenameResult.Duplicate;

        Assert.Equal(PageRenameResult.Duplicate, sut.RenameError);
        Assert.True(sut.HasRenameError);
        Assert.Contains(nameof(PageItemViewModel.RenameError), raised);
        Assert.Contains(nameof(PageItemViewModel.HasRenameError), raised);
    }

    /// <summary>Clearing <see cref="PageItemViewModel.RenameError"/> back to <see langword="null"/> clears <see cref="PageItemViewModel.HasRenameError"/> too.</summary>
    [Fact]
    public void RenameError_ClearedToNull_ClearsHasRenameError()
    {
        var sut = new PageItemViewModel("work", "Work") { RenameError = PageRenameResult.Empty };

        sut.RenameError = null;

        Assert.Null(sut.RenameError);
        Assert.False(sut.HasRenameError);
    }
}
