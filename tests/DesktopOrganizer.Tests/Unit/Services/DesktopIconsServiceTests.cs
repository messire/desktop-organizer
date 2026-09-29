using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="DesktopIconsService"/>'s session-only visibility intent (ADR-034): what the
/// application asked for is remembered in memory and re-applied to the new <c>SHELLDLL_DefView</c> after
/// an Explorer restart.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopIconsServiceTests
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    private static readonly IntPtr Progman = new(0x100);
    private static readonly IntPtr DefView = new(0x200);
    private static readonly IntPtr NewDefView = new(0x300);

    private readonly Mock<INativeApi> _nativeApi = new();
    private readonly DesktopIconsService _sut;
    private IntPtr _defView = DefView;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopIconsServiceTests"/> class: SHELLDLL_DefView
    /// lives directly under Progman (whatever <see cref="_defView"/> currently is).
    /// </summary>
    public DesktopIconsServiceTests()
    {
        _nativeApi.Setup(api => api.FindWindow("Progman", null)).Returns(Progman);
        _nativeApi.Setup(api => api.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null)).Returns(() => _defView);
        _sut = new DesktopIconsService(_nativeApi.Object);
    }

    /// <summary>No intent recorded before the application ever changed visibility.</summary>
    [Fact]
    public void DesiredVisible_IsNull_Initially()
    {
        Assert.Null(_sut.DesiredVisible);
    }

    /// <summary>SetIconsVisible records the intent.</summary>
    /// <param name="visible">Requested visibility.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetIconsVisible_RecordsIntent(bool visible)
    {
        _sut.SetIconsVisible(visible);

        Assert.Equal(visible, _sut.DesiredVisible);
    }

    /// <summary>The intent is recorded even while SHELLDLL_DefView cannot be found (Explorer down).</summary>
    [Fact]
    public void SetIconsVisible_WithoutDefView_StillRecordsIntent()
    {
        _defView = IntPtr.Zero;

        _sut.SetIconsVisible(false);

        Assert.False(_sut.DesiredVisible);
        _nativeApi.Verify(api => api.ShowWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>Toggle records the resulting intent.</summary>
    [Fact]
    public void Toggle_RecordsResultingIntent()
    {
        _nativeApi.Setup(api => api.IsWindowVisible(DefView)).Returns(true);

        _sut.Toggle();

        Assert.False(_sut.DesiredVisible);
        _nativeApi.Verify(api => api.ShowWindow(DefView, SwHide), Times.Once);
    }

    /// <summary>
    /// Every UI mirror (tray checkmark, settings toggle) is told about a change made through
    /// SetIconsVisible — raised after the shell call, so a handler re-reading AreIconsVisible sees the
    /// new state.
    /// </summary>
    /// <param name="visible">Requested visibility.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetIconsVisible_RaisesVisibilityChanged_AfterShellCall(bool visible)
    {
        var raised = 0;
        var showWindowCallsAtRaise = -1;
        var showWindowCalls = 0;
        _nativeApi.Setup(api => api.ShowWindow(DefView, It.IsAny<int>())).Callback(() => showWindowCalls++).Returns(true);
        _sut.VisibilityChanged += (sender, _) =>
        {
            raised++;
            showWindowCallsAtRaise = showWindowCalls;
            Assert.Same(_sut, sender);
        };

        _sut.SetIconsVisible(visible);

        Assert.Equal(1, raised);
        Assert.Equal(1, showWindowCallsAtRaise);
    }

    /// <summary>Toggle goes through SetIconsVisible and therefore notifies exactly once.</summary>
    [Fact]
    public void Toggle_RaisesVisibilityChangedOnce()
    {
        _nativeApi.Setup(api => api.IsWindowVisible(DefView)).Returns(true);
        var raised = 0;
        _sut.VisibilityChanged += (_, _) => raised++;

        _sut.Toggle();

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// The intent is recorded (and the mirrors notified) even while SHELLDLL_DefView is gone: a missed
    /// notification would leave a checkmark stale, a spurious one costs the mirrors a single lookup.
    /// </summary>
    [Fact]
    public void SetIconsVisible_WithoutDefView_StillRaisesVisibilityChanged()
    {
        _defView = IntPtr.Zero;
        var raised = 0;
        _sut.VisibilityChanged += (_, _) => raised++;

        _sut.SetIconsVisible(false);

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// Re-hiding the new SHELLDLL_DefView after an Explorer restart is a visibility change too — the
    /// mirrors must learn about it the same way.
    /// </summary>
    [Fact]
    public void ReapplyDesiredVisibility_HiddenIntentApplied_RaisesVisibilityChanged()
    {
        _sut.SetIconsVisible(false);
        _defView = NewDefView;
        var raised = 0;
        _sut.VisibilityChanged += (_, _) => raised++;

        Assert.True(_sut.ReapplyDesiredVisibility());
        Assert.Equal(1, raised);
    }

    /// <summary>Nothing changed (no intent / "visible" intent / DefView not there yet) → no notification.</summary>
    /// <param name="setVisibleFirst">Whether a "visible" intent is recorded before reapplying.</param>
    /// <param name="defViewPresent">Whether SHELLDLL_DefView can be found at reapply time.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReapplyDesiredVisibility_NothingToApply_DoesNotRaiseVisibilityChanged(bool setVisibleFirst, bool defViewPresent)
    {
        if (setVisibleFirst) _sut.SetIconsVisible(true);
        _defView = defViewPresent ? DefView : IntPtr.Zero;
        var raised = 0;
        _sut.VisibilityChanged += (_, _) => raised++;

        _sut.ReapplyDesiredVisibility();

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// After an Explorer restart the NEW SHELLDLL_DefView (visible by default) is hidden again when the
    /// session intent is "hidden".
    /// </summary>
    [Fact]
    public void ReapplyDesiredVisibility_HiddenIntent_HidesNewDefView()
    {
        _sut.SetIconsVisible(false);
        _defView = NewDefView;

        var applied = _sut.ReapplyDesiredVisibility();

        Assert.True(applied);
        _nativeApi.Verify(api => api.ShowWindow(NewDefView, SwHide), Times.Once);
    }

    /// <summary>With no intent, or a "visible" intent, nothing is forced onto the shell.</summary>
    /// <param name="setVisibleFirst">Whether the application asked for "visible" first.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReapplyDesiredVisibility_NoOrVisibleIntent_TouchesNothing(bool setVisibleFirst)
    {
        if (setVisibleFirst)
        {
            _sut.SetIconsVisible(true);
            _nativeApi.Invocations.Clear();
        }

        var applied = _sut.ReapplyDesiredVisibility();

        Assert.True(applied);
        _nativeApi.Verify(api => api.ShowWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>"Hidden" intent while the new SHELLDLL_DefView does not exist yet → false, so the caller retries.</summary>
    [Fact]
    public void ReapplyDesiredVisibility_DefViewNotYetThere_ReturnsFalse()
    {
        _sut.SetIconsVisible(false);
        _defView = IntPtr.Zero;
        _nativeApi.Setup(api => api.EnumWindows(It.IsAny<EnumWindowsCallback>(), IntPtr.Zero)).Returns(true);

        Assert.False(_sut.ReapplyDesiredVisibility());
    }

    /// <summary>A throwing native layer degrades to "not yet" instead of escaping.</summary>
    [Fact]
    public void ReapplyDesiredVisibility_NativeThrows_ReturnsFalse()
    {
        _sut.SetIconsVisible(false);
        _nativeApi.Setup(api => api.FindWindow("Progman", null)).Throws(new InvalidOperationException("explorer restarting"));

        Assert.False(_sut.ReapplyDesiredVisibility());
    }

    // ── QueryIconsVisibility (release-hardening review L5): tri-state, read-only ──

    /// <summary>SHELLDLL_DefView not found (autostart at logon) → Unknown, never "hidden".</summary>
    [Fact]
    public void QueryIconsVisibility_DefViewNotFound_ReturnsUnknown()
    {
        _defView = IntPtr.Zero;
        _nativeApi.Setup(api => api.EnumWindows(It.IsAny<EnumWindowsCallback>(), IntPtr.Zero)).Returns(true);

        Assert.Equal(DesktopIconsVisibility.Unknown, _sut.QueryIconsVisibility());
        Assert.False(_sut.AreIconsVisible); // the legacy bool folds "not found" into "hidden" — hence the query
    }

    /// <summary>DefView found → Visible/Hidden by the window's own visibility.</summary>
    /// <param name="visible">What <c>IsWindowVisible</c> answers.</param>
    /// <param name="expected">The expected state.</param>
    [Theory]
    [InlineData(true, DesktopIconsVisibility.Visible)]
    [InlineData(false, DesktopIconsVisibility.Hidden)]
    public void QueryIconsVisibility_DefViewFound_ReflectsWindowVisibility(bool visible, DesktopIconsVisibility expected)
    {
        _nativeApi.Setup(api => api.IsWindowVisible(DefView)).Returns(visible);

        Assert.Equal(expected, _sut.QueryIconsVisibility());
    }

    /// <summary>Explorer's own "Show desktop icons = off" outranks whatever the window looks like.</summary>
    /// <param name="defViewVisible">What <c>IsWindowVisible</c> answers for SHELLDLL_DefView.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueryIconsVisibility_ExplorerSettingOff_ReturnsHiddenByExplorerSetting(bool defViewVisible)
    {
        _nativeApi.Setup(api => api.IsWindowVisible(DefView)).Returns(defViewVisible);
        _nativeApi.Setup(api => api.IsExplorerDesktopIconsSettingOff()).Returns(true);

        Assert.Equal(DesktopIconsVisibility.HiddenByExplorerSetting, _sut.QueryIconsVisibility());
    }

    /// <summary>The query is read-only: it never shows/hides a window and never records an intent.</summary>
    [Fact]
    public void QueryIconsVisibility_IsReadOnly()
    {
        _nativeApi.Setup(api => api.IsWindowVisible(DefView)).Returns(false);

        _sut.QueryIconsVisibility();

        _nativeApi.Verify(api => api.ShowWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
        Assert.Null(_sut.DesiredVisible);
    }

    /// <summary>A throwing native layer answers Unknown instead of escaping.</summary>
    [Fact]
    public void QueryIconsVisibility_NativeThrows_ReturnsUnknown()
    {
        _nativeApi.Setup(api => api.FindWindow("Progman", null)).Throws(new InvalidOperationException("explorer restarting"));

        Assert.Equal(DesktopIconsVisibility.Unknown, _sut.QueryIconsVisibility());
    }
}
