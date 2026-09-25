using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.State.Dto;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for SystemIconsPanelManager.
/// IApplicationStateService, IDesktopIntegrationService, IMonitorService and the window factory
/// are all mocked — no WPF window is created.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SystemIconsPanelManagerTests
{
    private const nint PanelHwnd = 0x1234;

    private static readonly Guid PersistedGuid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SystemDefaultGuid = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PanelCurrentGuid = new("33333333-3333-3333-3333-333333333333");

    private readonly Mock<IApplicationStateService> _stateServiceMock = new();
    private readonly Mock<IDesktopIntegrationService> _desktopMock = new();
    private readonly Mock<IMonitorService> _monitorMock = new();
    private readonly Mock<IVirtualDesktopService> _virtualDesktopMock = new();

    // Tracks whether the factory delegate was invoked.
    private bool _factoryInvoked;

    /// <summary>
    /// Gets the persisted/systemDefault/panelCurrent/expected combinations exercised by
    /// <see cref="ResolveAnchorDesktopId_TruthTable"/>.
    /// </summary>
    public static IEnumerable<object?[]> ResolveAnchorDesktopIdCases()
    {
        // persisted, systemDefault, panelCurrent, expected
        yield return new object?[] { null, null, null, null };
        yield return new object?[] { PersistedGuid, null, null, PersistedGuid };
        yield return new object?[] { null, SystemDefaultGuid, null, SystemDefaultGuid };
        yield return new object?[] { null, null, PanelCurrentGuid, PanelCurrentGuid };
        yield return new object?[] { PersistedGuid, SystemDefaultGuid, null, SystemDefaultGuid };
        yield return new object?[] { PersistedGuid, null, PanelCurrentGuid, PersistedGuid };
        yield return new object?[] { null, SystemDefaultGuid, PanelCurrentGuid, SystemDefaultGuid };
        yield return new object?[] { PersistedGuid, SystemDefaultGuid, PanelCurrentGuid, SystemDefaultGuid };
    }

    // ── Load: empty settings → defaults ──────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> defaults
    /// <see cref="DesktopOrganizer.UI.Models.SystemIconsPanelModel.IsEnabled"/> to <see langword="false"/>
    /// when no state was ever persisted.
    /// </summary>
    [Fact]
    public void Load_WithEmptySettings_UsesDefaults_IsEnabledFalse()
    {
        // Arrange
        var sut = BuildSut(initialState: null);

        // Act
        sut.Load();

        // Assert — feature is off by default
        Assert.False(sut.Model.IsEnabled);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> seeds all five well-known
    /// <see cref="SystemIconKind"/> items when no state was ever persisted.
    /// </summary>
    [Fact]
    public void Load_WithEmptySettings_UsesDefaults_AllFiveIconsPresent()
    {
        // Arrange
        var sut = BuildSut(initialState: null);

        // Act
        sut.Load();

        // Assert — all 5 well-known icons are present
        var allKinds = Enum.GetValues<SystemIconKind>();
        foreach (var kind in allKinds)
        {
            Assert.Contains(sut.Model.Items, i => i.Kind == kind);
        }

        Assert.Equal(allKinds.Length, sut.Model.Items.Count);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> defaults every icon item's visibility to
    /// <see langword="true"/> when no state was ever persisted.
    /// </summary>
    [Fact]
    public void Load_WithEmptySettings_UsesDefaults_AllIconsVisible()
    {
        // Arrange
        var sut = BuildSut(initialState: null);

        // Act
        sut.Load();

        // Assert — default visibility is true for every icon
        Assert.All(sut.Model.Items, item => Assert.True(item.IsVisible));
    }

    // ── Load: saved JSON restores model ──────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> restores
    /// <see cref="DesktopOrganizer.UI.Models.SystemIconsPanelModel.IsEnabled"/> from persisted state.
    /// </summary>
    [Fact]
    public void Load_WithSavedJson_RestoresIsEnabled()
    {
        // Arrange
        var json = BuildState(isEnabled: true);
        var sut = BuildSut(initialState: json);

        // Act
        sut.Load();

        // Assert
        Assert.True(sut.Model.IsEnabled);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> restores each item's persisted
    /// visibility flag.
    /// </summary>
    [Fact]
    public void Load_WithSavedJson_RestoresItemVisibility()
    {
        // Arrange — ControlPanel is marked invisible
        var items = new[]
        {
            (SystemIconKind.ThisPc,       true,  0),
            (SystemIconKind.ControlPanel, false, 1),
            (SystemIconKind.RecycleBin,   true,  2),
            (SystemIconKind.Network,      true,  3),
            (SystemIconKind.UserFolder,   true,  4),
        };
        var json = BuildState(isEnabled: false, items: items);
        var sut = BuildSut(initialState: json);

        // Act
        sut.Load();

        // Assert — ControlPanel was saved as invisible
        var cp = sut.Model.Items.Single(i => i.Kind == SystemIconKind.ControlPanel);
        Assert.False(cp.IsVisible);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> restores each item's persisted display
    /// order.
    /// </summary>
    [Fact]
    public void Load_WithSavedJson_RestoresItemOrder()
    {
        // Arrange — items in reversed order
        var items = new[]
        {
            (SystemIconKind.UserFolder,   true, 0),
            (SystemIconKind.Network,      true, 1),
            (SystemIconKind.RecycleBin,   true, 2),
            (SystemIconKind.ControlPanel, true, 3),
            (SystemIconKind.ThisPc,       true, 4),
        };
        var json = BuildState(isEnabled: false, items: items);
        var sut = BuildSut(initialState: json);

        // Act
        sut.Load();

        // Assert — order values are restored
        Assert.Equal(4, sut.Model.Items.Single(i => i.Kind == SystemIconKind.ThisPc).Order);
        Assert.Equal(0, sut.Model.Items.Single(i => i.Kind == SystemIconKind.UserFolder).Order);
    }

    // ── Save: delegates to IApplicationStateService.SaveNow() ────────────────
    //
    // ADR-024 C6: Save() no longer builds/writes a DTO itself — ApplicationStateService.SaveNow()
    // pulls the live Model straight from ISystemIconsPanelManager at write time (uniform pull model,
    // same as WindowManager/PageManagerService/LayoutSnapshotService), so the manager's own contract
    // here is purely "did it ask for a save". Mapping-correctness (Model -> SystemIconsPanelStateDto,
    // including ShowOnAllVirtualDesktops/AnchorVirtualDesktopId round-tripping) is covered by
    // SystemIconsPanelStateMapperTests instead.

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Save"/> asks
    /// <see cref="IApplicationStateService.SaveNow"/> to flush the whole document to disk.
    /// </summary>
    [Fact]
    public void Save_CallsApplicationStateServiceSaveNow()
    {
        // Arrange
        var sut = BuildSut(initialState: null);
        sut.Load();

        // Act
        sut.Save();

        // Assert — IApplicationStateService.SaveNow() was called to flush the whole document to disk.
        _stateServiceMock.Verify(s => s.SaveNow(), Times.Once);
    }

    // ── Apply: disabled → factory not invoked ────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Apply"/> never invokes the window factory when
    /// the panel is disabled.
    /// </summary>
    [Fact]
    public void Apply_WhenDisabled_DoesNotInvokeWindowFactory()
    {
        // Arrange
        var sut = BuildSut(initialState: null);
        sut.Load();

        Assert.False(sut.Model.IsEnabled); // precondition

        // Act
        sut.Apply();

        // Assert — window factory was never invoked
        Assert.False(_factoryInvoked);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Apply"/> invokes the window factory when the
    /// panel is enabled.
    /// </summary>
    [Fact]
    public void Apply_WhenEnabled_InvokesWindowFactory()
    {
        // Arrange
        var json = BuildState(isEnabled: true);
        var sut = BuildSut(initialState: json);
        sut.Load();

        // Act — factory throws (no WPF dispatcher), but we can detect it was called
        sut.Apply();

        // Assert
        Assert.True(_factoryInvoked);
    }

    // ── Load: no persisted state → does not throw, uses defaults ─────────────
    //
    // ADR-024 C6: SystemIconsPanelManager no longer parses raw JSON itself — the store/
    // ApplicationStateService layer owns document validity (recovery ladder, SchemaContractTests),
    // so a garbage-JSON scenario can no longer reach this manager at all. The one input shape left
    // for this layer to defend is "no state was ever persisted" (SystemIconsPanel == null).

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> does not throw when no state was ever
    /// persisted (<c>IApplicationStateService.SystemIconsPanel == null</c>).
    /// </summary>
    [Fact]
    public void Load_NullState_DoesNotThrow()
    {
        // Arrange
        var sut = BuildSut(initialState: null);

        // Act & Assert
        var ex = Record.Exception(() => sut.Load());
        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> falls back to the default model (disabled,
    /// all five icon kinds) when no state was ever persisted.
    /// </summary>
    [Fact]
    public void Load_NullState_UsesDefaultModel()
    {
        // Arrange
        var sut = BuildSut(initialState: null);

        // Act
        sut.Load();

        // Assert — fell back to defaults (IsEnabled=false, 5 items)
        Assert.False(sut.Model.IsEnabled);
        Assert.Equal(Enum.GetValues<SystemIconKind>().Length, sut.Model.Items.Count);
    }

    // ── Block E: "Show on all virtual desktops" persistence ──────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> defaults
    /// <c>ShowOnAllVirtualDesktops</c> to <see langword="false"/> (opt-in feature) when no state was
    /// ever persisted.
    /// </summary>
    [Fact]
    public void Load_WithEmptySettings_ShowOnAllVirtualDesktopsIsFalseByDefault()
    {
        // Arrange
        var sut = BuildSut(initialState: null);

        // Act
        sut.Load();

        // Assert — opt-in feature, off unless the user turned it on
        Assert.False(sut.Model.ShowOnAllVirtualDesktops);
    }

    /// <summary>
    /// Verifies that a document written before the "show on all virtual desktops" setting existed loads
    /// without error and defaults the flag to <see langword="false"/> — no migration is needed.
    /// </summary>
    [Fact]
    public void Load_LegacyStateWithoutVirtualDesktopFlag_DefaultsToFalse()
    {
        // Arrange — a document written before the setting existed degrades to false (no migration
        // needed) — see BuildState's remarks.
        var state = BuildState(isEnabled: true, showOnAllVirtualDesktops: null);
        Assert.False(state.ShowOnAllVirtualDesktops);
        var sut = BuildSut(initialState: state);

        // Act
        sut.Load();

        // Assert — reads without error, missing key means false (no migration needed)
        Assert.True(sut.Model.IsEnabled);
        Assert.False(sut.Model.ShowOnAllVirtualDesktops);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> restores a persisted
    /// "show on all virtual desktops" flag in both states.
    /// </summary>
    /// <param name="stored">The persisted flag value under test.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_WithSavedJson_RestoresShowOnAllVirtualDesktops(bool stored)
    {
        // Arrange
        var json = BuildState(isEnabled: true, showOnAllVirtualDesktops: stored);
        var sut = BuildSut(initialState: json);

        // Act
        sut.Load();

        // Assert
        Assert.Equal(stored, sut.Model.ShowOnAllVirtualDesktops);
    }

    // Save()'s effect on ShowOnAllVirtualDesktops content is no longer inspectable through
    // IApplicationStateService (pull model — see the region comment above); covered instead by
    // SystemIconsPanelStateMapperTests' round trip, and by Load_WithSavedJson_RestoresShowOnAllVirtualDesktops
    // above for the Load() half.

    // ── Block E: virtual desktop poll timer policy (risk E-R4) ───────────────

    /// <summary>
    /// Verifies that the virtual desktop poll timer never starts while the panel is on but the
    /// "show on all virtual desktops" setting is off.
    /// </summary>
    [Fact]
    public void Apply_WhenSettingOff_DoesNotStartVirtualDesktopTimer()
    {
        // Arrange — panel on, "all virtual desktops" off
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();

        // Act
        sut.Apply();

        // Assert
        Assert.False(sut.IsVirtualDesktopTimerRunning);
    }

    /// <summary>
    /// Verifies that the virtual desktop poll timer never starts when the panel itself is disabled, even
    /// with the setting on, since there is no window to follow.
    /// </summary>
    [Fact]
    public void Apply_WhenPanelDisabled_DoesNotStartVirtualDesktopTimer()
    {
        // Arrange — setting on, but the panel itself is off, so there is no window to follow
        var sut = BuildSut(BuildState(isEnabled: false, showOnAllVirtualDesktops: true));
        sut.Load();

        // Act
        sut.Apply();

        // Assert
        Assert.False(sut.IsVirtualDesktopTimerRunning);
    }

    /// <summary>
    /// Verifies that the virtual desktop poll timer never starts when window creation failed, since
    /// there is nothing to poll for.
    /// </summary>
    [Fact]
    public void Apply_WhenWindowMissing_DoesNotStartVirtualDesktopTimer()
    {
        // Arrange — everything enabled, but window creation fails in unit tests (_window stays null)
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();

        // Act
        sut.Apply();

        // Assert — no window => nothing to poll for
        Assert.True(_factoryInvoked);
        Assert.False(sut.IsVirtualDesktopTimerRunning);
    }

    /// <summary>
    /// Verifies that the virtual desktop poll timer never starts when the virtual desktop COM object is
    /// unavailable on the system.
    /// </summary>
    [Fact]
    public void Apply_WhenVirtualDesktopsUnsupported_DoesNotStartVirtualDesktopTimer()
    {
        // Arrange — COM object unavailable on this system
        var sut = BuildSut(
            BuildState(isEnabled: true, showOnAllVirtualDesktops: true),
            virtualDesktopSupported: false);
        sut.Load();

        // Act
        sut.Apply();

        // Assert
        Assert.False(sut.IsVirtualDesktopTimerRunning);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Dispose"/> leaves the virtual desktop poll timer
    /// stopped.
    /// </summary>
    [Fact]
    public void Dispose_LeavesVirtualDesktopTimerStopped()
    {
        // Arrange
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();
        sut.Apply();

        // Act
        sut.Dispose();

        // Assert
        Assert.False(sut.IsVirtualDesktopTimerRunning);
    }

    // ── Block E: silent degradation when the COM object is unavailable ───────

    /// <summary>
    /// Verifies that a full Load/Apply/Save/Dispose lifecycle stays silent (no exception) when the
    /// virtual desktop COM object is unavailable, and that the setting is still persisted, just inert.
    /// </summary>
    [Fact]
    public void UnsupportedVirtualDesktops_DoesNotBreakManagerLifecycle()
    {
        // Arrange — IsSupported == false for every call
        var sut = BuildSut(
            BuildState(isEnabled: true, showOnAllVirtualDesktops: true),
            virtualDesktopSupported: false);

        // Act & Assert — load / apply / save / dispose all stay silent
        var ex = Record.Exception(() =>
        {
            sut.Load();
            sut.Apply();
            sut.Save();
            sut.Dispose();
        });

        Assert.Null(ex);
        Assert.True(sut.Model.ShowOnAllVirtualDesktops); // setting is still persisted, just inert
    }

    /// <summary>
    /// Verifies that no virtual desktop polling happens at all when the feature is unsupported.
    /// </summary>
    [Fact]
    public void UnsupportedVirtualDesktops_NeverQueriesWindowPosition()
    {
        // Arrange
        var sut = BuildSut(
            BuildState(isEnabled: true, showOnAllVirtualDesktops: true),
            virtualDesktopSupported: false);
        sut.Load();

        // Act
        sut.Apply();

        // Assert — no polling at all when the feature is unsupported
        _virtualDesktopMock.Verify(v => v.IsWindowOnCurrentDesktop(It.IsAny<IntPtr>()), Times.Never);
        _virtualDesktopMock.Verify(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    // ── Poll policy: ShouldPollVirtualDesktop truth table ────────────────────
    //
    // The timer policy lives in a pure static so it can be tested for every combination,
    // including the "a window exists" leg that a unit test cannot produce for real.

    /// <summary>
    /// Verifies every combination of <see cref="SystemIconsPanelManager.ShouldPollVirtualDesktop"/>'s
    /// pure policy inputs, including the "a window exists" leg a unit test cannot exercise through a
    /// real window.
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is enabled.</param>
    /// <param name="showOnAll">Whether the "show on all virtual desktops" setting is on.</param>
    /// <param name="hasWindow">Whether the panel currently has a live window.</param>
    /// <param name="supported">Whether the virtual desktop COM object is available.</param>
    /// <param name="expected">The expected poll decision for this combination.</param>
    [Theory]

    // panelEnabled, showOnAll, hasWindow, supported  =>  expected
    [InlineData(true,  true,  true,  true,  true)] // the one and only case that polls
    [InlineData(false, true,  true,  true,  false)] // panel off
    [InlineData(true,  false, true,  true,  false)] // setting off
    [InlineData(true,  true,  false, true,  false)] // no window to follow
    [InlineData(true,  true,  true,  false, false)] // no virtual desktop COM object
    [InlineData(false, false, true,  true,  false)]
    [InlineData(false, true,  false, true,  false)]
    [InlineData(false, true,  true,  false, false)]
    [InlineData(true,  false, false, true,  false)]
    [InlineData(true,  false, true,  false, false)]
    [InlineData(true,  true,  false, false, false)]
    [InlineData(false, false, false, true,  false)]
    [InlineData(false, false, true,  false, false)]
    [InlineData(false, true,  false, false, false)]
    [InlineData(true,  false, false, false, false)]
    [InlineData(false, false, false, false, false)]
    public void ShouldPollVirtualDesktop_TruthTable(
        bool panelEnabled, bool showOnAll, bool hasWindow, bool supported, bool expected)
    {
        var actual = SystemIconsPanelManager.ShouldPollVirtualDesktop(
            panelEnabled, showOnAll, hasWindow, supported);

        Assert.Equal(expected, actual);
    }

    // ── Z-order policy: ShouldReassertZOrder truth table ─────────────────────
    //
    // Same shape as the poll policy above: a pure static, so the "a window exists" leg a unit test
    // cannot produce for real is still covered.

    /// <summary>
    /// Verifies every combination of <see cref="SystemIconsPanelManager.ShouldReassertZOrder"/>'s pure
    /// policy inputs, including the "a window exists" leg a unit test cannot exercise through a real
    /// window.
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is enabled.</param>
    /// <param name="hasWindow">Whether the panel currently has a live window.</param>
    /// <param name="expected">The expected re-assert decision for this combination.</param>
    [Theory]

    // panelEnabled, hasWindow  =>  expected
    [InlineData(true,  true,  true)] // the one and only case that re-asserts the Z-order
    [InlineData(false, true,  false)] // panel off — the window is gone anyway
    [InlineData(true,  false, false)] // no window to push back
    [InlineData(false, false, false)]
    public void ShouldReassertZOrder_TruthTable(bool panelEnabled, bool hasWindow, bool expected)
    {
        var actual = SystemIconsPanelManager.ShouldReassertZOrder(panelEnabled, hasWindow);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The Z-order timer and the virtual desktop poll timer are orthogonal: "stay behind other
    /// windows" is a property of the panel itself and must keep working while "show on all virtual
    /// desktops" is OFF (where the poll timer is deliberately not running) and on systems with no
    /// virtual desktop manager at all. Expressed as a policy comparison because
    /// <see cref="SystemIconsPanelManager.ShouldReassertZOrder"/> does not take either input — that
    /// absence IS the invariant, and this test fails the moment someone adds them.
    /// </summary>
    /// <param name="showOnAllVirtualDesktops">The "show on all virtual desktops" checkbox state under test.</param>
    /// <param name="virtualDesktopsSupported">Whether the virtual desktop COM object is available.</param>
    [Theory]
    [InlineData(false, true)] // checkbox OFF, virtual desktops supported
    [InlineData(false, false)] // checkbox OFF, no virtual desktop manager
    [InlineData(true,  false)] // checkbox ON, but the shell cannot honour it
    public void ShouldReassertZOrder_IsIndependentOfTheVirtualDesktopFeature(
        bool showOnAllVirtualDesktops, bool virtualDesktopsSupported)
    {
        // Precondition: in each of these combinations the virtual desktop timer stays stopped …
        Assert.False(SystemIconsPanelManager.ShouldPollVirtualDesktop(
            panelEnabled: true,
            showOnAllVirtualDesktops,
            hasWindow: true,
            virtualDesktopsSupported));

        // … while the Z-order timer must keep running regardless.
        Assert.True(SystemIconsPanelManager.ShouldReassertZOrder(panelEnabled: true, hasWindow: true));
    }

    // ── Poll step: PollVirtualDesktopOnce (the tick body) ────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.PollVirtualDesktopOnce"/> is a true no-op for a
    /// zero window handle.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_WithZeroHandle_TouchesNothing()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();

        sut.PollVirtualDesktopOnce(IntPtr.Zero);

        _virtualDesktopMock.Verify(v => v.IsWindowOnCurrentDesktop(It.IsAny<IntPtr>()), Times.Never);
        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that when the panel is already reported on the current desktop, the poll step neither
    /// tries to move nor embeds the window.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_WhenAlreadyOnCurrentDesktop_DoesNotMoveOrEmbed()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();
        ScriptOnCurrentDesktop(true);

        sut.PollVirtualDesktopOnce(PanelHwnd);

        _virtualDesktopMock.Verify(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>()), Times.Never);
        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that a rejected move-to-current-desktop attempt never embeds the window and does not
    /// increment the ineffective-move counter.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_WhenMoveIsRejected_DoesNotEmbed()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();
        ScriptOnCurrentDesktop(false);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>())).Returns(false);

        sut.PollVirtualDesktopOnce(PanelHwnd);

        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
        Assert.Equal(0, sut.IneffectiveMoveCount);
    }

    /// <summary>
    /// Verifies the ADR-019 V-R11 fix: an effective move resets the ineffective-move counter and never
    /// re-embeds the window into WorkerW — that step no longer exists.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_AfterEffectiveMove_ResetsCounterAndNeverEmbeds()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();

        // Guard says "left behind", the post-move check says "arrived" => the move worked.
        ScriptOnCurrentDesktop(false, true);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>())).Returns(true);

        sut.PollVirtualDesktopOnce(PanelHwnd);

        // ADR-019: the panel is never re-parented into WorkerW, so there is no "re-embed after a
        // successful move" step left at all (that step used to exist and re-triggered the original
        // bug — see V-R11 in the plan). An effective move just resets the counter.
        Assert.Equal(0, sut.IneffectiveMoveCount);
        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies the ADR-016 §E0.2 case: a move that reports success (<c>S_OK</c>) but has no visible
    /// effect (the window is never subsequently reported on the current desktop) still counts as
    /// ineffective and never embeds the window.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_WhenMoveIsSilentNoOp_NeverEmbeds()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();

        // MoveWindowToDesktop returns S_OK but does nothing (ADR-016 §E0.2): the window is never
        // reported as being on the current desktop.
        ScriptOnCurrentDesktop(false);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>())).Returns(true);

        sut.PollVirtualDesktopOnce(PanelHwnd);

        Assert.Equal(1, sut.IneffectiveMoveCount);
        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that three consecutive ineffective moves trip the circuit breaker: the manager stops
    /// polling and sets <c>Model.VirtualDesktopFeatureIneffective</c>, rather than retrying forever.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_AfterThreeIneffectiveMoves_TripsCircuitBreaker()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();

        ScriptOnCurrentDesktop(false);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>())).Returns(true);

        sut.PollVirtualDesktopOnce(PanelHwnd);
        sut.PollVirtualDesktopOnce(PanelHwnd);
        Assert.False(sut.Model.VirtualDesktopFeatureIneffective); // still trying

        sut.PollVirtualDesktopOnce(PanelHwnd);

        // Third strike: stop asking the shell to do something it demonstrably will not do.
        Assert.True(sut.Model.VirtualDesktopFeatureIneffective);
        Assert.False(sut.IsVirtualDesktopTimerRunning);
        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that the ineffective-move counter resets once the panel is found on the current desktop
    /// again — the circuit breaker must not latch on non-consecutive failures.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_ResetsCounterWhenPanelIsBackOnCurrentDesktop()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();

        // Two ineffective rounds (2 answers each: guard + verify), then the panel is found on the
        // active desktop again — the breaker must not latch on non-consecutive failures.
        ScriptOnCurrentDesktop(false, false, false, false, true);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>())).Returns(true);

        sut.PollVirtualDesktopOnce(PanelHwnd);
        sut.PollVirtualDesktopOnce(PanelHwnd);
        Assert.Equal(2, sut.IneffectiveMoveCount);

        sut.PollVirtualDesktopOnce(PanelHwnd);

        Assert.Equal(0, sut.IneffectiveMoveCount);
        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
    }

    /// <summary>
    /// Verifies that an exception thrown by <see cref="IVirtualDesktopService"/> during a poll step is
    /// caught and never propagates, since this runs on the UI thread from a <c>DispatcherTimer</c>.
    /// </summary>
    [Fact]
    public void PollVirtualDesktopOnce_WhenServiceThrows_DoesNotPropagate()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: true));
        sut.Load();
        _virtualDesktopMock
            .Setup(v => v.IsWindowOnCurrentDesktop(It.IsAny<IntPtr>()))
            .Throws(new InvalidOperationException("COM went away"));

        // This runs on the UI thread from a DispatcherTimer — an escaping exception would kill the app.
        var ex = Record.Exception(() => sut.PollVirtualDesktopOnce(PanelHwnd));

        Assert.Null(ex);
    }

    // ── Z-order step: SendPanelToBackOnce (the tick body) ────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.SendPanelToBackOnce"/> is a true no-op for a
    /// zero window handle.
    /// </summary>
    [Fact]
    public void SendPanelToBackOnce_WithZeroHandle_TouchesNothing()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();

        sut.SendPanelToBackOnce(IntPtr.Zero);

        _desktopMock.Verify(d => d.SendToBackOfZOrder(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// The panel must be pushed behind other windows in BOTH states of the "show on all virtual
    /// desktops" checkbox — the two features are unrelated (see
    /// <see cref="ShouldReassertZOrder_IsIndependentOfTheVirtualDesktopFeature"/>).
    /// </summary>
    /// <param name="showOnAll">The "show on all virtual desktops" checkbox state under test.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SendPanelToBackOnce_PushesPanelBack_RegardlessOfVirtualDesktopSetting(bool showOnAll)
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: showOnAll));
        sut.Load();

        sut.SendPanelToBackOnce(PanelHwnd);

        _desktopMock.Verify(d => d.SendToBackOfZOrder(PanelHwnd), Times.Once);
    }

    /// <summary>
    /// Regression guard on ADR-019 / V-R11, matching the ones on the two virtual desktop seams: the
    /// Z-order path must never fall back to the old WorkerW re-parenting, which would make the panel
    /// unconditionally visible on every virtual desktop again.
    /// </summary>
    [Fact]
    public void SendPanelToBackOnce_NeverCallsEmbedInDesktop()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();

        sut.SendPanelToBackOnce(PanelHwnd);

        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that an exception thrown by <see cref="IDesktopIntegrationService.SendToBackOfZOrder"/>
    /// during a Z-order tick is caught and never propagates.
    /// </summary>
    [Fact]
    public void SendPanelToBackOnce_WhenServiceThrows_DoesNotPropagate()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();
        _desktopMock
            .Setup(d => d.SendToBackOfZOrder(It.IsAny<IntPtr>()))
            .Throws(new InvalidOperationException("USER32 went away"));

        // Runs on the UI thread from a DispatcherTimer — an escaping exception would kill the app.
        var ex = Record.Exception(() => sut.SendPanelToBackOnce(PanelHwnd));

        Assert.Null(ex);
    }

    // ── Z-order timer lifecycle (no leak past the window it drives) ──────────

    /// <summary>
    /// The real start/stop truth table, driven through
    /// <c>SystemIconsPanelManager.UpdateZOrderTimerCore</c>. The <c>Apply</c>-based tests below can
    /// only ever reach the <c>hasWindow == false</c> row — a unit test has no WPF
    /// <c>Application</c> to build a panel window against, so the factory always fails and
    /// <c>_window</c> stays null — which means without this test the timer would never be created in
    /// any test at all and every "…LeavesZOrderTimerStopped" assertion below would be vacuous (M1).
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is enabled.</param>
    /// <param name="hasWindow">Whether the panel currently has a live window.</param>
    /// <param name="expectedRunning">Whether the Z-order timer is expected to be running afterwards.</param>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void UpdateZOrderTimerCore_RunsOnlyWhenPanelEnabledAndWindowExists(
        bool panelEnabled, bool hasWindow, bool expectedRunning)
    {
        var sut = BuildSut(BuildState(isEnabled: panelEnabled));
        sut.Load();

        sut.UpdateZOrderTimerCore(panelEnabled, hasWindow);

        Assert.Equal(expectedRunning, sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// The transition the vacuous tests could not cover: a timer that really is running must be
    /// stopped when either input goes away, not merely "never started" (risk E-R4 — a timer left
    /// ticking against a closed panel calls <c>SendToBackOfZOrder</c> on a dead handle forever).
    /// </summary>
    /// <param name="panelEnabled">Whether the system icons panel itself is enabled.</param>
    /// <param name="hasWindow">Whether the panel currently has a live window.</param>
    [Theory]
    [InlineData(false, true)] // panel switched off
    [InlineData(true, false)] // window closed
    [InlineData(false, false)] // both
    public void UpdateZOrderTimerCore_StopsARunningTimer_WhenEitherInputGoesAway(
        bool panelEnabled, bool hasWindow)
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();
        sut.UpdateZOrderTimerCore(true, true);
        Assert.True(sut.IsZOrderTimerRunning); // precondition, not the assertion under test

        sut.UpdateZOrderTimerCore(panelEnabled, hasWindow);

        Assert.False(sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// <c>Dispose</c> against a timer that is genuinely running — the non-vacuous counterpart of
    /// <see cref="Dispose_LeavesZOrderTimerStopped"/>.
    /// </summary>
    [Fact]
    public void Dispose_StopsARunningZOrderTimer()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();
        sut.UpdateZOrderTimerCore(true, true);
        Assert.True(sut.IsZOrderTimerRunning); // precondition, not the assertion under test

        sut.Dispose();

        Assert.False(sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// Switching the panel off routes through <c>ClosePanel</c>, which must stop a genuinely running
    /// Z-order timer — the non-vacuous counterpart of
    /// <see cref="Apply_AfterDisablingPanel_LeavesZOrderTimerStopped"/>.
    /// </summary>
    [Fact]
    public void Apply_AfterDisablingPanel_StopsARunningZOrderTimer()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();
        sut.UpdateZOrderTimerCore(true, true);
        Assert.True(sut.IsZOrderTimerRunning); // precondition, not the assertion under test

        sut.Model.IsEnabled = false;
        sut.Apply();

        Assert.False(sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// Re-applying the same state must reuse the existing <c>DispatcherTimer</c> rather than leak a
    /// new one on every call (<c>Apply</c> runs on every settings change).
    /// </summary>
    [Fact]
    public void UpdateZOrderTimerCore_IsIdempotent_AcrossRepeatedCalls()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();

        sut.UpdateZOrderTimerCore(true, true);
        sut.UpdateZOrderTimerCore(true, true);
        sut.UpdateZOrderTimerCore(true, true);

        Assert.True(sut.IsZOrderTimerRunning);

        sut.Dispose();
        Assert.False(sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// Verifies that the Z-order timer never starts when the panel is disabled.
    /// </summary>
    [Fact]
    public void Apply_WhenPanelDisabled_DoesNotStartZOrderTimer()
    {
        var sut = BuildSut(BuildState(isEnabled: false));
        sut.Load();

        sut.Apply();

        Assert.False(sut.IsZOrderTimerRunning);
        _desktopMock.Verify(d => d.SendToBackOfZOrder(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that the Z-order timer never starts when window creation failed, since there is nothing
    /// to push back.
    /// </summary>
    [Fact]
    public void Apply_WhenWindowMissing_DoesNotStartZOrderTimer()
    {
        // Window creation fails in unit tests (_window stays null) — nothing to push back.
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();

        sut.Apply();

        Assert.True(_factoryInvoked);
        Assert.False(sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Dispose"/> leaves the Z-order timer stopped,
    /// regardless of the "show on all virtual desktops" setting.
    /// </summary>
    /// <param name="showOnAll">The "show on all virtual desktops" checkbox state under test.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_LeavesZOrderTimerStopped(bool showOnAll)
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: showOnAll));
        sut.Load();
        sut.Apply();

        sut.Dispose();

        Assert.False(sut.IsZOrderTimerRunning);
    }

    /// <summary>
    /// Switching the panel off goes through <c>ClosePanel</c>, which must stop the Z-order timer
    /// before dropping the window reference — a timer left ticking against a closed panel is the
    /// same leak the virtual desktop timer guards against (risk E-R4).
    /// </summary>
    [Fact]
    public void Apply_AfterDisablingPanel_LeavesZOrderTimerStopped()
    {
        var sut = BuildSut(BuildState(isEnabled: true));
        sut.Load();
        sut.Apply();

        sut.Model.IsEnabled = false;
        sut.Apply();

        Assert.False(sut.IsZOrderTimerRunning);
    }

    // ── ApplyVirtualDesktopSetting: toggling must not recreate the window ────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.ApplyVirtualDesktopSetting"/> never invokes the
    /// window factory or starts the poll timer while the panel is disabled.
    /// </summary>
    [Fact]
    public void ApplyVirtualDesktopSetting_WhenPanelDisabled_DoesNotCreateWindow()
    {
        var sut = BuildSut(BuildState(isEnabled: false, showOnAllVirtualDesktops: true));
        sut.Load();

        sut.ApplyVirtualDesktopSetting();

        Assert.False(_factoryInvoked);
        Assert.False(sut.IsVirtualDesktopTimerRunning);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.ApplyVirtualDesktopSetting"/> clears
    /// <c>Model.VirtualDesktopFeatureIneffective</c>, since re-applying the setting is an explicit user
    /// action and therefore a retry point.
    /// </summary>
    [Fact]
    public void ApplyVirtualDesktopSetting_ClearsTheIneffectiveLatch()
    {
        var sut = BuildSut(BuildState(isEnabled: false, showOnAllVirtualDesktops: true));
        sut.Load();
        sut.Model.VirtualDesktopFeatureIneffective = true;

        // Re-applying the setting is an explicit user action and therefore a retry point.
        sut.ApplyVirtualDesktopSetting();

        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Apply"/> also clears
    /// <c>Model.VirtualDesktopFeatureIneffective</c>.
    /// </summary>
    [Fact]
    public void Apply_ClearsTheIneffectiveLatch()
    {
        var sut = BuildSut(BuildState(isEnabled: false, showOnAllVirtualDesktops: true));
        sut.Load();
        sut.Model.VirtualDesktopFeatureIneffective = true;

        sut.Apply();

        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
    }

    // ── ResolveAnchorDesktopId: pure policy truth table (ADR-019 §3.2) ───────

    /// <summary>
    /// Verifies every persisted/systemDefault/panelCurrent combination of
    /// <see cref="SystemIconsPanelManager.ResolveAnchorDesktopId"/>'s pure priority policy.
    /// </summary>
    /// <param name="persisted">The persisted anchor id (R1).</param>
    /// <param name="systemDefault">The shell's current default desktop id (R2).</param>
    /// <param name="panelCurrent">The desktop the panel window is currently on (R3).</param>
    /// <param name="expected">The expected resolved anchor id for this combination.</param>
    [Theory]
    [MemberData(nameof(ResolveAnchorDesktopIdCases))]
    public void ResolveAnchorDesktopId_TruthTable(
        Guid? persisted, Guid? systemDefault, Guid? panelCurrent, Guid? expected)
    {
        var actual = SystemIconsPanelManager.ResolveAnchorDesktopId(persisted, systemDefault, panelCurrent);

        Assert.Equal(expected, actual);
    }

    // ── EnforceAnchorDesktopOnce: OFF-mode anchor placement (ADR-019 §3.2/§3.3) ──

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.EnforceAnchorDesktopOnce"/> is a true no-op for
    /// a zero window handle.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_WithZeroHandle_TouchesNothing()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();

        sut.EnforceAnchorDesktopOnce(IntPtr.Zero);

        _virtualDesktopMock.VerifyGet(v => v.IsSupported, Times.Never);
        _virtualDesktopMock.Verify(v => v.TryGetDefaultDesktopId(), Times.Never);
        _virtualDesktopMock.Verify(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>()), Times.Never);
        _virtualDesktopMock.Verify(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// M5: on a system where the virtual desktop manager is unavailable, the method must return
    /// immediately after reading <see cref="IVirtualDesktopService.IsSupported"/> — it must not touch
    /// the registry or make any other COM call.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_WhenUnsupported_ReturnsImmediately()
    {
        var sut = BuildSut(
            BuildState(isEnabled: true, showOnAllVirtualDesktops: false),
            virtualDesktopSupported: false);
        sut.Load();

        sut.EnforceAnchorDesktopOnce(PanelHwnd);

        _virtualDesktopMock.Verify(v => v.TryGetDefaultDesktopId(), Times.Never);
        _virtualDesktopMock.Verify(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>()), Times.Never);
        _virtualDesktopMock.Verify(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>()), Times.Never);
        _virtualDesktopMock.Verify(v => v.IsWindowOnCurrentDesktop(It.IsAny<IntPtr>()), Times.Never);
        _virtualDesktopMock.Verify(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// M2 regression: the "already on the anchor desktop" branch must reset the shared
    /// <see cref="SystemIconsPanelManager.IneffectiveMoveCount"/> streak, not merely skip
    /// incrementing it — otherwise a prior OFF-mode failure streak would still trip once the panel
    /// later drifts again, even though it was demonstrably fine in between.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_AlreadyOnAnchor_DoesNotMove_AndResetsIneffectiveCounter()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        sut.Model.AnchorVirtualDesktopId = PersistedGuid;

        // Build up a non-zero streak first, so the reset below is actually observable.
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns(PanelCurrentGuid);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToDesktop(PanelHwnd, PersistedGuid)).Returns(false);
        sut.EnforceAnchorDesktopOnce(PanelHwnd);
        Assert.Equal(1, sut.IneffectiveMoveCount); // precondition: streak is non-zero

        // Now the panel reports being on its anchor desktop already.
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns(PersistedGuid);

        sut.EnforceAnchorDesktopOnce(PanelHwnd);

        _virtualDesktopMock.Verify(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>()), Times.Once); // only the earlier attempt
        Assert.Equal(0, sut.IneffectiveMoveCount);
        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
    }

    /// <summary>
    /// Verifies that when the panel is not on its resolved anchor desktop, a successful move happens
    /// exactly once and resets the ineffective-move counter.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_NotOnAnchor_SuccessfulMove_MovesOnceAndResetsCounter()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        sut.Model.AnchorVirtualDesktopId = PersistedGuid;

        _virtualDesktopMock.SetupSequence(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>()))
            .Returns(PanelCurrentGuid) // panelCurrent, read before the move
            .Returns(PersistedGuid);     // afterMove, once the move succeeded
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToDesktop(PanelHwnd, PersistedGuid)).Returns(true);

        sut.EnforceAnchorDesktopOnce(PanelHwnd);

        _virtualDesktopMock.Verify(v => v.TryMoveWindowToDesktop(PanelHwnd, PersistedGuid), Times.Once);
        Assert.Equal(0, sut.IneffectiveMoveCount);
        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
    }

    /// <summary>
    /// "Never guess" invariant (ADR-019 §3.2): when persisted, R2 and R1 all come back
    /// <see langword="null"/>, the method must not call <see cref="IVirtualDesktopService.TryMoveWindowToDesktop"/>
    /// with an arbitrary desktop, and it must not write an anchor to settings.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_WhenAllSourcesNull_NeverMoves()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load(); // Model.AnchorVirtualDesktopId stays null: no anchor was ever persisted

        sut.EnforceAnchorDesktopOnce(PanelHwnd);

        _virtualDesktopMock.Verify(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>()), Times.Never);
        _stateServiceMock.Verify(s => s.SaveNow(), Times.Never);
        Assert.Null(sut.Model.AnchorVirtualDesktopId);
    }

    /// <summary>
    /// M3 self-heal (§3.3): a persisted/R1 anchor that fails to take for
    /// <see cref="SystemIconsPanelManager"/>'s <c>SelfHealMinConsecutiveFailures</c> (2) consecutive
    /// rounds — including an explicit refusal, not just a silent no-op — is recaptured from the
    /// window's actual (tracked) desktop and persisted exactly once. Never applies while R2
    /// (<c>systemDefault</c>) is available.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_SelfHeal_AfterTwoConsecutiveFailures_RecapturesAnchorAndSavesOnce()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        sut.Model.AnchorVirtualDesktopId = PersistedGuid;

        // systemDefault (R2) stays null throughout: self-heal only applies to a persisted/R1 anchor.
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns(PanelCurrentGuid);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToDesktop(PanelHwnd, PersistedGuid)).Returns(false);

        sut.EnforceAnchorDesktopOnce(PanelHwnd); // failure #1 — below the self-heal threshold
        Assert.Equal(PersistedGuid, sut.Model.AnchorVirtualDesktopId);
        _stateServiceMock.Verify(s => s.SaveNow(), Times.Never);

        sut.EnforceAnchorDesktopOnce(PanelHwnd); // failure #2 — self-heal threshold reached

        Assert.Equal(PanelCurrentGuid, sut.Model.AnchorVirtualDesktopId);
        _stateServiceMock.Verify(s => s.SaveNow(), Times.Once);
        Assert.Equal(0, sut.IneffectiveMoveCount);
        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
    }

    /// <summary>
    /// Verifies that repeated calls resolving to the same, already-applied anchor never call
    /// <see cref="IApplicationStateService.SaveNow"/>.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_WhenAnchorUnchanged_DoesNotCallSave()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        sut.Model.AnchorVirtualDesktopId = SystemDefaultGuid;
        _virtualDesktopMock.Setup(v => v.TryGetDefaultDesktopId()).Returns(SystemDefaultGuid);
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns(SystemDefaultGuid);

        sut.EnforceAnchorDesktopOnce(PanelHwnd);
        sut.EnforceAnchorDesktopOnce(PanelHwnd);

        _stateServiceMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>
    /// M1 regression: unlike <see cref="SystemIconsPanelManager.PollVirtualDesktopOnce"/>'s ON-mode
    /// circuit breaker, this OFF-mode seam must never raise
    /// <see cref="DesktopOrganizer.UI.Models.SystemIconsPanelModel.VirtualDesktopFeatureIneffective"/>
    /// — that banner is worded for the poll timer and would otherwise appear for a checkbox the user
    /// may have just switched off. Keeping <c>systemDefault</c> non-null on every call disables
    /// self-heal too, so the failure streak keeps growing well past
    /// <see cref="SystemIconsPanelManager.PollVirtualDesktopOnce"/>'s own threshold (3) without ever
    /// resetting.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_NeverRaisesVirtualDesktopFeatureIneffective_EvenAfterManyFailures()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        sut.Model.AnchorVirtualDesktopId = SystemDefaultGuid;

        _virtualDesktopMock.Setup(v => v.TryGetDefaultDesktopId()).Returns(SystemDefaultGuid);
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns(PanelCurrentGuid);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>())).Returns(false);

        for (var i = 0; i < 10; i++)
        {
            sut.EnforceAnchorDesktopOnce(PanelHwnd);
        }

        Assert.False(sut.Model.VirtualDesktopFeatureIneffective);
        Assert.Equal(10, sut.IneffectiveMoveCount);
    }

    /// <summary>
    /// Regression guard on the first-cause fix (V-R11): this OFF-mode seam must never fall back to
    /// the old WorkerW re-parenting path.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_NeverCallsEmbedInDesktop()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        sut.Model.AnchorVirtualDesktopId = PersistedGuid;
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns(PanelCurrentGuid);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>())).Returns(true);

        sut.EnforceAnchorDesktopOnce(PanelHwnd);

        _desktopMock.Verify(d => d.EmbedInDesktop(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that an exception thrown by <see cref="IVirtualDesktopService"/> is caught and never
    /// propagates out of <see cref="SystemIconsPanelManager.EnforceAnchorDesktopOnce"/>.
    /// </summary>
    [Fact]
    public void EnforceAnchorDesktopOnce_WhenServiceThrows_DoesNotPropagate()
    {
        var sut = BuildSut(BuildState(isEnabled: true, showOnAllVirtualDesktops: false));
        sut.Load();
        _virtualDesktopMock
            .Setup(v => v.TryGetDefaultDesktopId())
            .Throws(new InvalidOperationException("COM went away"));

        var ex = Record.Exception(() => sut.EnforceAnchorDesktopOnce(PanelHwnd));

        Assert.Null(ex);
    }

    // ── Persistence: AnchorVirtualDesktopId (ADR-019 §7) ──────────────────────
    //
    // Save()'s round trip is a pull-model concern (see the "Save: delegates to
    // IApplicationStateService.SaveNow()" region above) — covered by SystemIconsPanelStateMapperTests
    // instead. The Load() half stays covered here.

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelManager.Load"/> leaves the anchor id
    /// <see langword="null"/> when the persisted state omits it.
    /// </summary>
    [Fact]
    public void Load_WithoutAnchorKey_LeavesAnchorNull()
    {
        var state = BuildState(isEnabled: true); // anchor argument omitted -> AnchorVirtualDesktopId stays null
        Assert.Null(state.AnchorVirtualDesktopId);
        var sut = BuildSut(initialState: state);

        sut.Load();

        Assert.Null(sut.Model.AnchorVirtualDesktopId);
    }

    /// <summary>
    /// Verifies that a garbage (non-GUID) persisted anchor value loads as a <see langword="null"/>
    /// anchor while every other setting still loads normally.
    /// </summary>
    [Fact]
    public void Load_WithGarbageAnchorValue_AnchorIsNull_ButOtherSettingsLoadNormally()
    {
        var items = new[]
        {
            (SystemIconKind.ThisPc,       true,  0),
            (SystemIconKind.ControlPanel, false, 1),
            (SystemIconKind.RecycleBin,   true,  2),
            (SystemIconKind.Network,      true,  3),
            (SystemIconKind.UserFolder,   true,  4),
        };
        var json = BuildState(
            isEnabled: true, iconSize: "Large", items: items, anchorVirtualDesktopId: "not-a-guid");
        var sut = BuildSut(initialState: json);

        sut.Load();

        Assert.Null(sut.Model.AnchorVirtualDesktopId);
        Assert.True(sut.Model.IsEnabled);
        Assert.Equal(SystemIconSize.Large, sut.Model.IconSize);
        Assert.False(sut.Model.Items.Single(i => i.Kind == SystemIconKind.ControlPanel).IsVisible);
        Assert.Equal(Enum.GetValues<SystemIconKind>().Length, sut.Model.Items.Count);
    }

    // Whether Save() ends up omitting a null anchorVirtualDesktopId key on disk is StateJson's
    // DefaultIgnoreCondition.WhenWritingNull concern — see SchemaContractTests.

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="SystemIconsPanelStateDto"/> as <see cref="IApplicationStateService.SystemIconsPanel"/>
    /// would already hold it after a real <c>Load()</c> (ADR-024 C6: the manager no longer parses raw
    /// JSON itself — that happens once, in the store/service layer).
    /// <para>
    /// <paramref name="showOnAllVirtualDesktops"/> defaulting to <see langword="false"/> when omitted
    /// mirrors a document written by a build from before the virtual desktop setting existed (the DTO
    /// field itself always exists; only its JSON presence used to vary).
    /// </para>
    /// </summary>
    private static SystemIconsPanelStateDto BuildState(
        bool isEnabled,
        string iconSize = "Medium",
        IEnumerable<(SystemIconKind Kind, bool Visible, int Order)>? items = null,
        bool? showOnAllVirtualDesktops = null,
        string? anchorVirtualDesktopId = null)
    {
        return new SystemIconsPanelStateDto
        {
            IsEnabled = isEnabled,
            ShowOnAllVirtualDesktops = showOnAllVirtualDesktops ?? false,
            IconSize = iconSize,
            AnchorVirtualDesktopId = anchorVirtualDesktopId,
            Items = (items ?? DefaultItemTuples())
                .Select(t => new SystemIconsPanelItemStateDto { Kind = t.Kind, IsVisible = t.Visible, Order = t.Order })
                .ToList(),
        };
    }

    private static IEnumerable<(SystemIconKind Kind, bool Visible, int Order)> DefaultItemTuples() =>
    [
        (SystemIconKind.ThisPc,       true, 0),
        (SystemIconKind.ControlPanel, true, 1),
        (SystemIconKind.RecycleBin,   true, 2),
        (SystemIconKind.Network,      true, 3),
        (SystemIconKind.UserFolder,   true, 4),
    ];

    /// <summary>
    /// Feeds <see cref="IVirtualDesktopService.IsWindowOnCurrentDesktop"/> a scripted sequence of
    /// answers; the last one repeats once the script runs out. One poll step asks twice: once as
    /// the guard, once to verify the move actually had an effect.
    /// </summary>
    private void ScriptOnCurrentDesktop(params bool[] answers)
    {
        var queue = new Queue<bool>(answers);
        _virtualDesktopMock
            .Setup(v => v.IsWindowOnCurrentDesktop(It.IsAny<IntPtr>()))
            .Returns(() => queue.Count > 0 ? queue.Dequeue() : answers[^1]);
    }

    private SystemIconsPanelManager BuildSut(
        SystemIconsPanelStateDto? initialState = null,
        bool virtualDesktopSupported = true)
    {
        _virtualDesktopMock.SetupGet(v => v.IsSupported).Returns(virtualDesktopSupported);
        _virtualDesktopMock.Setup(v => v.IsWindowOnCurrentDesktop(It.IsAny<IntPtr>())).Returns(true);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToCurrentDesktop(It.IsAny<IntPtr>())).Returns(false);

        // Anchor-resolution members (ADR-019 §3.2): default to "nothing available" so tests that
        // don't care about EnforceAnchorDesktopOnce see it degrade to a complete no-op.
        _virtualDesktopMock.Setup(v => v.TryGetWindowDesktopId(It.IsAny<IntPtr>())).Returns((Guid?)null);
        _virtualDesktopMock.Setup(v => v.TryGetDefaultDesktopId()).Returns((Guid?)null);
        _virtualDesktopMock.Setup(v => v.TryMoveWindowToDesktop(It.IsAny<IntPtr>(), It.IsAny<Guid>())).Returns(false);

        // SystemIconsPanel is read-only on IApplicationStateService — populated once by Load() and
        // never written back by SystemIconsPanelManager.Save() (ADR-024 D-11/D-12: SaveNow() pulls the
        // live Model straight from ISystemIconsPanelManager inside ApplicationStateService itself, so
        // App.OnExit's single SaveNow() call always captures the current in-memory model, not a
        // possibly-stale value pushed earlier in the session).
        _stateServiceMock.SetupGet(s => s.SystemIconsPanel).Returns(initialState);
        _stateServiceMock.Setup(s => s.SaveNow());

        _monitorMock.SetupAdd(m => m.MonitorsChanged += It.IsAny<Action>());
        _monitorMock.SetupRemove(m => m.MonitorsChanged -= It.IsAny<Action>());
        _monitorMock.Setup(m => m.GetPrimaryMonitor())
            .Returns(new MonitorInfo(
                DeviceName: "\\\\.\\DISPLAY1",
                Bounds: new System.Windows.Rect(0, 0, 1920, 1080),
                WorkArea: new System.Windows.Rect(0, 0, 1920, 1040),
                IsPrimary: true));

        _factoryInvoked = false;

        // The window factory is a Func<SystemIconsPanelWindow>; we never call it successfully
        // (a real WPF window needs an STA thread + dispatcher, which this test project does not
        // set up), so the manager's Apply() swallows the exception and _window stays null. We just
        // track that the factory was invoked.
        //
        // Because of that, everything that depends on a live window is tested through the two
        // extracted seams instead of through the timer: ShouldPollVirtualDesktop (pure policy,
        // takes hasWindow as a parameter) and PollVirtualDesktopOnce (tick body, takes the HWND as
        // a parameter). See the "poll policy" and "poll step" regions below.
#pragma warning disable CA1416 // Platform check not needed in unit tests
        SystemIconsPanelWindow FactoryDelegate()
        {
            _factoryInvoked = true;

            // Throw immediately — no WPF dispatcher available in unit tests.
            // The manager swallows window-creation exceptions gracefully.
            throw new InvalidOperationException("Window creation not supported in unit tests.");
        }
#pragma warning restore CA1416

        return new SystemIconsPanelManager(
            _stateServiceMock.Object,
            _desktopMock.Object,
            _monitorMock.Object,
            _virtualDesktopMock.Object,
            FactoryDelegate);
    }
}
