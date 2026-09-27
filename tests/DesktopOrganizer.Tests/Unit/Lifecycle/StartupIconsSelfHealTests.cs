using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="StartupIconsSelfHeal"/> (ADR-033 part 2, plan §2) — the "previous process
/// died with icons hidden" recovery applied at the start of every session.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class StartupIconsSelfHealTests
{
    /// <summary>SHELLDLL_DefView found and hidden at startup (a crashed session hid it) → icons are shown.</summary>
    [Fact]
    public void Apply_DefViewFoundAndHidden_ShowsThem()
    {
        var desktopIcons = new Mock<IDesktopIconsService>();
        desktopIcons.Setup(x => x.QueryIconsVisibility()).Returns(DesktopIconsVisibility.Hidden);

        StartupIconsSelfHeal.Apply(desktopIcons.Object);

        desktopIcons.Verify(x => x.SetIconsVisible(true), Times.Once);
    }

    /// <summary>
    /// L5: nothing is touched — and, crucially, no intent is recorded — unless SHELLDLL_DefView was actually
    /// found hidden: already visible, not found yet (autostart at logon), or turned off by the user in
    /// Explorer itself (never overridden).
    /// </summary>
    /// <param name="state">What the service observed.</param>
    [Theory]
    [InlineData(DesktopIconsVisibility.Visible)]
    [InlineData(DesktopIconsVisibility.Unknown)]
    [InlineData(DesktopIconsVisibility.HiddenByExplorerSetting)]
    public void Apply_NotPositivelyHiddenByUs_DoesNothing(DesktopIconsVisibility state)
    {
        var desktopIcons = new Mock<IDesktopIconsService>();
        desktopIcons.Setup(x => x.QueryIconsVisibility()).Returns(state);
        desktopIcons.Setup(x => x.AreIconsVisible).Returns(false); // the legacy bool must not be what decides

        StartupIconsSelfHeal.Apply(desktopIcons.Object);

        desktopIcons.Verify(x => x.SetIconsVisible(It.IsAny<bool>()), Times.Never);
        desktopIcons.Verify(x => x.Toggle(), Times.Never);
    }

    /// <summary>A <see langword="null"/> desktop icons service is rejected.</summary>
    [Fact]
    public void Apply_NullDesktopIcons_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StartupIconsSelfHeal.Apply(null!));
    }
}
