using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="GlobalDefaultsViewModel"/>'s ADR-011 Amendment (2026-09-24) hook: changing
/// <see cref="GlobalDefaultsViewModel.GridSize"/> must re-align every currently open panel onto the new
/// grid via <see cref="IWindowManager.NormalizePanelsToGrid"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class GlobalDefaultsViewModelTests
{
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IColorPickerService> _colorPickerMock = new();

    /// <summary>
    /// Verifies that changing <see cref="GlobalDefaultsViewModel.GridSize"/> to a different value
    /// normalizes every open panel onto the new grid and saves the setting.
    /// </summary>
    [Fact]
    public void GridSize_ChangedToADifferentValue_NormalizesOpenPanelsAndSaves()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 32);
        var sut = BuildSut();

        sut.GridSize = 48;

        Assert.Equal(48, _appSettingsMock.Object.GridSize);
        _appSettingsMock.Verify(s => s.Save(), Times.Once);
        _windowManagerMock.Verify(w => w.NormalizePanelsToGrid(), Times.Once);
    }

    /// <summary>
    /// Verifies that re-assigning <see cref="GlobalDefaultsViewModel.GridSize"/> to its current value
    /// never normalizes panels or saves settings.
    /// </summary>
    [Fact]
    public void GridSize_SetToTheSameValue_NeverNormalizesOrSaves()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 32);
        var sut = BuildSut();

        sut.GridSize = 32;

        _appSettingsMock.Verify(s => s.Save(), Times.Never);
        _windowManagerMock.Verify(w => w.NormalizePanelsToGrid(), Times.Never);
    }

    /// <summary>
    /// Verifies that setting <see cref="GlobalDefaultsViewModel.GridSize"/> multiple times, including a
    /// same-value no-op, normalizes panels exactly once per actual value change.
    /// </summary>
    [Fact]
    public void GridSize_ChangedMultipleTimes_NormalizesOncePerActualChange()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 1);
        var sut = BuildSut();

        sut.GridSize = 32;
        sut.GridSize = 32; // no-op, same value again.
        sut.GridSize = 48;

        _windowManagerMock.Verify(w => w.NormalizePanelsToGrid(), Times.Exactly(2));
    }

    // ── Settings-window redesign (docs/prompts/settings-window-redesign.md item 3): SnapToGridEnabled/
    // GridStep toggle semantics ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.SnapToGridEnabled"/> reflects whether
    /// <see cref="GlobalDefaultsViewModel.GridSize"/> is currently greater than <c>1</c>.
    /// </summary>
    /// <param name="gridSize">The <see cref="IAppSettings.GridSize"/> value under test.</param>
    /// <param name="expected">The expected <see cref="GlobalDefaultsViewModel.SnapToGridEnabled"/> value.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(16, true)]
    public void SnapToGridEnabled_Get_ReflectsGridSizeGreaterThanOne(int gridSize, bool expected)
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, gridSize);
        var sut = BuildSut();

        Assert.Equal(expected, sut.SnapToGridEnabled);
    }

    /// <summary>
    /// Verifies that turning <see cref="GlobalDefaultsViewModel.SnapToGridEnabled"/> off sets
    /// <see cref="GlobalDefaultsViewModel.GridSize"/> to <c>1</c>, and turning it back on restores the
    /// step that was in effect immediately before.
    /// </summary>
    [Fact]
    public void SnapToGridEnabled_ToggleOffThenOn_RestoresPreviousGridSize()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 48);
        var sut = BuildSut();

        sut.SnapToGridEnabled = false;
        Assert.Equal(1, sut.GridSize);

        sut.SnapToGridEnabled = true;
        Assert.Equal(48, sut.GridSize);
    }

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.GridStep"/> shows the remembered last step while
    /// snapping is off, rather than the misleading <c>1</c> <see cref="GlobalDefaultsViewModel.GridSize"/>
    /// itself holds in that state.
    /// </summary>
    [Fact]
    public void GridStep_WhileSnappingOff_ShowsRememberedStepNotOne()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 32);
        var sut = BuildSut();

        sut.SnapToGridEnabled = false;

        Assert.Equal(32, sut.GridStep);
    }

    /// <summary>
    /// M-6 fix-cycle regression: a <see cref="IAppSettings.GridSize"/> of <c>2</c> or <c>3</c> — legal
    /// values persisted before the Layout &amp; snapping section's slider existed — must be shown exactly
    /// as-is by <see cref="GlobalDefaultsViewModel.GridStep"/>, not silently coerced up. Merely reading
    /// <see cref="GlobalDefaultsViewModel.GridStep"/>/<see cref="GlobalDefaultsViewModel.SnapToGridEnabled"/>
    /// (as opening the settings window does) must never call <see cref="IAppSettings.Save"/> — the actual
    /// on-open coercion bug fixed alongside this test lived in the Slider's own <c>Minimum</c> in
    /// <c>LayoutSnappingSection.xaml</c> (WPF coerces an out-of-range bound value and writes the coerced
    /// value straight back through the two-way binding), which this VM-level test cannot exercise directly
    /// but documents the invariant the XAML fix restores.
    /// </summary>
    /// <param name="legacyGridSize">The legacy <see cref="IAppSettings.GridSize"/> value under test.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyGridSizeTwoOrThree_IsShownUnchanged(int legacyGridSize)
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, legacyGridSize);
        var sut = BuildSut();

        Assert.Equal(legacyGridSize, sut.GridStep);
        Assert.True(sut.SnapToGridEnabled);
        _appSettingsMock.Verify(s => s.Save(), Times.Never);
    }

    // ── DefaultSurfaceStyle (settings-window-redesign.md item 6): typed VM property over the
    // string-persisted IAppSettings.DefaultSurfaceStyle key — same shape as DefaultPanelGeometryMode/
    // DefaultIconLayoutPolicy, and the first control this setting has ever had. ─────────────────────

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.DefaultSurfaceStyle"/> parses whatever enum
    /// member name is currently persisted in <see cref="IAppSettings.DefaultSurfaceStyle"/>.
    /// </summary>
    [Fact]
    public void DefaultSurfaceStyle_Get_ParsesPersistedEnumName()
    {
        _appSettingsMock.SetupGet(s => s.DefaultSurfaceStyle).Returns(nameof(PanelSurfaceStyle.Glass));
        var sut = BuildSut();

        Assert.Equal(PanelSurfaceStyle.Glass, sut.DefaultSurfaceStyle);
    }

    /// <summary>
    /// Verifies that an unrecognised or empty persisted <see cref="IAppSettings.DefaultSurfaceStyle"/>
    /// string never throws and falls back to <see cref="PanelSurfaceStyle.Ambient"/>
    /// (<see cref="PanelSurfaceStyleParser.ParseOrAmbient"/>).
    /// </summary>
    /// <param name="persisted">The invalid/empty raw value <see cref="IAppSettings.DefaultSurfaceStyle"/> returns.</param>
    [Theory]
    [InlineData("")]
    [InlineData("NotARealStyle")]
    [InlineData(null)]
    public void DefaultSurfaceStyle_Get_InvalidOrEmptyPersistedValue_FallsBackToAmbient(string? persisted)
    {
        _appSettingsMock.SetupGet(s => s.DefaultSurfaceStyle).Returns(persisted!);
        var sut = BuildSut();

        Assert.Equal(PanelSurfaceStyle.Ambient, sut.DefaultSurfaceStyle);
    }

    /// <summary>
    /// Verifies that setting <see cref="GlobalDefaultsViewModel.DefaultSurfaceStyle"/> persists the
    /// enum member's name (not a numeric value) via <see cref="IAppSettings.DefaultSurfaceStyle"/> and
    /// saves — a full round trip through the same string-typed setting.
    /// </summary>
    [Fact]
    public void DefaultSurfaceStyle_Set_PersistsEnumNameAndRoundTrips()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultSurfaceStyle, nameof(PanelSurfaceStyle.Ambient));
        var sut = BuildSut();

        sut.DefaultSurfaceStyle = PanelSurfaceStyle.Glass;

        Assert.Equal(nameof(PanelSurfaceStyle.Glass), _appSettingsMock.Object.DefaultSurfaceStyle);
        Assert.Equal(PanelSurfaceStyle.Glass, sut.DefaultSurfaceStyle);
        _appSettingsMock.Verify(s => s.Save(), Times.Once);
    }

    /// <summary>
    /// Verifies that re-assigning <see cref="GlobalDefaultsViewModel.DefaultSurfaceStyle"/> to its
    /// current (parsed) value never saves again — mirrors every other <c>SetGlobal</c>-backed property.
    /// </summary>
    [Fact]
    public void DefaultSurfaceStyle_SetToSameValue_NeverSaves()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultSurfaceStyle, nameof(PanelSurfaceStyle.Glass));
        var sut = BuildSut();

        sut.DefaultSurfaceStyle = PanelSurfaceStyle.Glass;

        _appSettingsMock.Verify(s => s.Save(), Times.Never);
    }

    // ── SetDefaultSurfaceStyleCommand (release-polish-1.0 visual review item 5): backs the "New panel
    // defaults" section's Surface style segmented control, replacing its previous ComboBox — same
    // Command+CommandParameter shape as SelectedPanelSettingsViewModel.SetSurfaceStyleCommand. ──────────

    /// <summary>
    /// Verifies that executing <see cref="GlobalDefaultsViewModel.SetDefaultSurfaceStyleCommand"/> with a
    /// <see cref="PanelSurfaceStyle"/> parameter writes it through the SAME
    /// <see cref="GlobalDefaultsViewModel.DefaultSurfaceStyle"/> property (and therefore the same
    /// <see cref="IAppSettings.DefaultSurfaceStyle"/> setter) a direct property assignment would.
    /// </summary>
    [Fact]
    public void SetDefaultSurfaceStyleCommand_Execute_WritesDefaultSurfaceStyleAndSaves()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultSurfaceStyle, nameof(PanelSurfaceStyle.Ambient));
        var sut = BuildSut();

        sut.SetDefaultSurfaceStyleCommand.Execute(PanelSurfaceStyle.Glass);

        Assert.Equal(PanelSurfaceStyle.Glass, sut.DefaultSurfaceStyle);
        Assert.Equal(nameof(PanelSurfaceStyle.Glass), _appSettingsMock.Object.DefaultSurfaceStyle);
        _appSettingsMock.Verify(s => s.Save(), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.SetDefaultSurfaceStyleCommand"/> can always execute
    /// (no <c>CanExecute</c> gating — every <see cref="PanelSurfaceStyle"/> member is always a legal default).
    /// </summary>
    [Fact]
    public void SetDefaultSurfaceStyleCommand_CanExecute_IsAlwaysTrue()
    {
        var sut = BuildSut();

        Assert.True(sut.SetDefaultSurfaceStyleCommand.CanExecute(PanelSurfaceStyle.Solid));
    }

    // ── ApplyFromPanel (settings-window redesign item 6, "Take from selected panel") ────────────────

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.ApplyFromPanel"/> copies every relevant
    /// appearance/geometry property from the given panel into this class's own defaults.
    /// </summary>
    [Fact]
    public void ApplyFromPanel_CopiesAppearanceAndGeometryFromPanel()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor);
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor);
        _appSettingsMock.SetupProperty(s => s.DefaultOpacity);
        _appSettingsMock.SetupProperty(s => s.DefaultSurfaceStyle);
        _appSettingsMock.SetupProperty(s => s.DefaultIconSize);
        _appSettingsMock.SetupProperty(s => s.DefaultPanelGeometryMode);
        _appSettingsMock.SetupProperty(s => s.DefaultIconLayoutPolicy);
        var sut = BuildSut();

        var panel = new ChildWindowModel
        {
            AccentColor = "#123456",
            TextColor = "#ABCDEF",
            SurfaceOpacity = 0.6,
            SurfaceStyle = PanelSurfaceStyle.Glass,
            IconSize = 72,
            GeometryMode = PanelGeometryMode.GridAligned,
            IconLayoutPolicy = IconLayoutPolicy.FixedGap,
        };

        sut.ApplyFromPanel(panel);

        Assert.Equal("#123456", sut.DefaultBackgroundColor);
        Assert.Equal("#ABCDEF", sut.DefaultTextColor);
        Assert.Equal(0.6, sut.DefaultOpacity);
        Assert.Equal(PanelSurfaceStyle.Glass, sut.DefaultSurfaceStyle);
        Assert.Equal(72, sut.DefaultIconSize);
        Assert.Equal(PanelGeometryMode.GridAligned, sut.DefaultPanelGeometryMode);
        Assert.Equal(IconLayoutPolicy.FixedGap, sut.DefaultIconLayoutPolicy);
    }

    /// <summary>Verifies that <see cref="GlobalDefaultsViewModel.ApplyFromPanel"/> rejects a null panel.</summary>
    [Fact]
    public void ApplyFromPanel_NullPanel_Throws()
    {
        var sut = BuildSut();

        Assert.Throws<ArgumentNullException>(() => sut.ApplyFromPanel(null!));
    }

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.ApplyFromPanel"/> only ever reads from the given
    /// panel — it never mutates it (the panel keeps living with its own settings; only the future
    /// defaults for NEW panels change).
    /// </summary>
    [Fact]
    public void ApplyFromPanel_DoesNotMutateTheSourcePanel()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor);
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor);
        _appSettingsMock.SetupProperty(s => s.DefaultOpacity);
        _appSettingsMock.SetupProperty(s => s.DefaultSurfaceStyle);
        _appSettingsMock.SetupProperty(s => s.DefaultIconSize);
        _appSettingsMock.SetupProperty(s => s.DefaultPanelGeometryMode);
        _appSettingsMock.SetupProperty(s => s.DefaultIconLayoutPolicy);
        var sut = BuildSut();

        var panel = new ChildWindowModel
        {
            AccentColor = "#123456",
            TextColor = "#ABCDEF",
            SurfaceOpacity = 0.6,
            SurfaceStyle = PanelSurfaceStyle.Glass,
            IconSize = 72,
            GeometryMode = PanelGeometryMode.GridAligned,
            IconLayoutPolicy = IconLayoutPolicy.FixedGap,
        };

        sut.ApplyFromPanel(panel);

        Assert.Equal("#123456", panel.AccentColor);
        Assert.Equal("#ABCDEF", panel.TextColor);
        Assert.Equal(0.6, panel.SurfaceOpacity);
        Assert.Equal(PanelSurfaceStyle.Glass, panel.SurfaceStyle);
        Assert.Equal(72, panel.IconSize);
        Assert.Equal(PanelGeometryMode.GridAligned, panel.GeometryMode);
        Assert.Equal(IconLayoutPolicy.FixedGap, panel.IconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.ApplyFromPanel"/> saves once per field that
    /// actually changed, and not at all for fields whose value already matched the panel's
    /// (<c>SetGlobal</c>'s equality guard, exercised across all seven copied fields at once).
    /// </summary>
    [Fact]
    public void ApplyFromPanel_SavesOnlyForFieldsThatActuallyChanged()
    {
        // Every default already matches the panel's own values except IconSize — only that one field's
        // Apply should call Save().
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor, "#123456");
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor, "#ABCDEF");
        _appSettingsMock.SetupProperty(s => s.DefaultOpacity, 0.6);
        _appSettingsMock.SetupProperty(s => s.DefaultSurfaceStyle, nameof(PanelSurfaceStyle.Glass));
        _appSettingsMock.SetupProperty(s => s.DefaultIconSize, 48.0);
        _appSettingsMock.SetupProperty(s => s.DefaultPanelGeometryMode, nameof(PanelGeometryMode.GridAligned));
        _appSettingsMock.SetupProperty(s => s.DefaultIconLayoutPolicy, nameof(IconLayoutPolicy.FixedGap));
        var sut = BuildSut();

        var panel = new ChildWindowModel
        {
            AccentColor = "#123456",
            TextColor = "#ABCDEF",
            SurfaceOpacity = 0.6,
            SurfaceStyle = PanelSurfaceStyle.Glass,
            IconSize = 72, // the only field that differs from the current default
            GeometryMode = PanelGeometryMode.GridAligned,
            IconLayoutPolicy = IconLayoutPolicy.FixedGap,
        };

        sut.ApplyFromPanel(panel);

        Assert.Equal(72, sut.DefaultIconSize);
        _appSettingsMock.Verify(s => s.Save(), Times.Once);
    }

    private GlobalDefaultsViewModel BuildSut() => new(_appSettingsMock.Object, _windowManagerMock.Object, _colorPickerMock.Object);
}
