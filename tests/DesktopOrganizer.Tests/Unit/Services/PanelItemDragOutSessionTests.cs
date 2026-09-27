using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PanelItemDragOutSession"/> — the process-wide, UI-thread-only
/// coordination point for a panel-item drag-out gesture (ADR-036, release-hardening-1.0.md §E).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelItemDragOutSessionTests
{
    private const string Path = @"C:\PanelItems\abc\file.txt";

    /// <summary>Verifies that begin makes the session active with the given source and path.</summary>
    [Fact]
    public void Begin_MakesTheSessionActive_WithTheGivenSourceAndPath()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();

        sut.Begin(sourceId, Path);

        Assert.True(sut.IsActive);
        Assert.Equal(sourceId, sut.SourcePanelId);
        Assert.Equal(Path, sut.Path);
    }

    /// <summary>Verifies that try claim different target with matching path returns true and end resolves to transferred to.</summary>
    [Fact]
    public void TryClaim_DifferentTargetWithMatchingPath_ReturnsTrue_AndEndResolvesToTransferredTo()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        sut.Begin(sourceId, Path);

        var claimed = sut.TryClaim(targetId, Path);

        Assert.True(claimed);

        var outcome = sut.End();

        Assert.Equal(DragOutOutcomeKind.TransferredTo, outcome.Kind);
        Assert.Equal(targetId, outcome.TargetPanelId);
    }

    /// <summary>Verifies that try claim by source itself returns false and end resolves to reordered.</summary>
    [Fact]
    public void TryClaim_BySourceItself_ReturnsFalse_AndEndResolvesToReordered()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();
        sut.Begin(sourceId, Path);

        // A self-claim (the source panel's own drag landing outside its file list) must never be
        // reported as an accepted transfer...
        var claimed = sut.TryClaim(sourceId, Path);
        Assert.False(claimed);

        // ...but the gesture is still recorded as "handled" — End() resolves to Reordered, not External,
        // so the source panel never starts a MovedOutVerifier existence-check for a file that never
        // actually left.
        var outcome = sut.End();
        Assert.Equal(DragOutOutcomeKind.Reordered, outcome.Kind);
        Assert.Null(outcome.TargetPanelId);
    }

    /// <summary>Verifies that try claim mismatched path returns false and does not consume the claim.</summary>
    [Fact]
    public void TryClaim_MismatchedPath_ReturnsFalse_AndDoesNotConsumeTheClaim()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        sut.Begin(sourceId, Path);

        var claimed = sut.TryClaim(targetId, @"C:\PanelItems\other\different.txt");

        Assert.False(claimed);

        // The mismatched claim must not have consumed the single-claim slot — a second, correct claim
        // from a different panel can still succeed.
        var secondTargetId = Guid.NewGuid();
        Assert.True(sut.TryClaim(secondTargetId, Path));

        var outcome = sut.End();
        Assert.Equal(DragOutOutcomeKind.TransferredTo, outcome.Kind);
        Assert.Equal(secondTargetId, outcome.TargetPanelId);
    }

    /// <summary>Verifies that try claim when not active returns false.</summary>
    [Fact]
    public void TryClaim_WhenNotActive_ReturnsFalse()
    {
        var sut = new PanelItemDragOutSession();

        Assert.False(sut.TryClaim(Guid.NewGuid(), Path));
    }

    /// <summary>Verifies that try claim already claimed by someone else returns false single claim only.</summary>
    [Fact]
    public void TryClaim_AlreadyClaimedBySomeoneElse_ReturnsFalse_SingleClaimOnly()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        sut.Begin(sourceId, Path);

        Assert.True(sut.TryClaim(firstTarget, Path));
        Assert.False(sut.TryClaim(secondTarget, Path));

        var outcome = sut.End();
        Assert.Equal(DragOutOutcomeKind.TransferredTo, outcome.Kind);
        Assert.Equal(firstTarget, outcome.TargetPanelId);
    }

    /// <summary>Verifies that end with no claim resolves to external.</summary>
    [Fact]
    public void End_WithNoClaim_ResolvesToExternal()
    {
        var sut = new PanelItemDragOutSession();
        sut.Begin(Guid.NewGuid(), Path);

        var outcome = sut.End();

        Assert.Equal(DragOutOutcomeKind.External, outcome.Kind);
        Assert.Null(outcome.TargetPanelId);
    }

    /// <summary>Verifies that end resets state session becomes inactive and reusable.</summary>
    [Fact]
    public void End_ResetsState_SessionBecomesInactiveAndReusable()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();
        sut.Begin(sourceId, Path);

        sut.End();

        Assert.False(sut.IsActive);
        Assert.Null(sut.SourcePanelId);
        Assert.Null(sut.Path);

        // A brand-new gesture can start right away — End() must have fully reset every field, not just
        // the ones End()'s own return value happened to read.
        var newSourceId = Guid.NewGuid();
        const string newPath = @"C:\PanelItems\xyz\other.txt";
        sut.Begin(newSourceId, newPath);

        Assert.True(sut.IsActive);
        Assert.Equal(newSourceId, sut.SourcePanelId);
        Assert.Equal(newPath, sut.Path);
    }

    /// <summary>Verifies that try claim path comparison is case insensitive.</summary>
    [Fact]
    public void TryClaim_PathComparisonIsCaseInsensitive()
    {
        var sut = new PanelItemDragOutSession();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        sut.Begin(sourceId, Path);

        Assert.True(sut.TryClaim(targetId, Path.ToUpperInvariant()));
    }
}
