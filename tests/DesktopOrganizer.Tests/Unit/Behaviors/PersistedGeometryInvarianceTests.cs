using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using Moq;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for release-hardening F4 (docs/PLANS/release-hardening-1.0-F-dpi.md §7
/// "PersistedGeometryInvarianceTests", ADR-037) — the plan's own persistence-invariance proof (§2
/// "Инвариант неизменности persistence"): on a UNIFORM <see cref="DesktopScaleModel"/> snapshot, the numbers
/// <see cref="PanelGeometryWriter"/> writes onto <see cref="ChildWindowModel.Position"/>/
/// <see cref="ChildWindowModel.Size"/> — and the numbers <c>ChildWindow.OnLocationChanged</c>'s own formula
/// reads back — are bit-identical to the pre-F 0.6.1 formula (a bare <c>Window.Left</c>/<c>Top</c>
/// read/write, no scale involved at all), for a fixture spanning every <see cref="PanelGeometryMode"/>, a
/// rolled-up panel, and negative (secondary-monitor placement) DIP coordinates.
/// </summary>
/// <remarks>
/// <b>Why this file has THREE shapes of test, not one <c>[Theory]</c> over every scale x mode combination.</b>
/// <see cref="PanelWindowCoordinates.GetWindowScale"/> — the ONLY allowed source of a window's <c>s_w</c>
/// (plan §4 point 3) — queries the REAL <see cref="System.Windows.Media.VisualTreeHelper.GetDpi(Visual)"/>
/// of whatever live <see cref="Window"/> the test constructs; it is not influenced by a mocked
/// <see cref="IDesktopScaleProvider"/> at all. In Embedded mode that query is provably irrelevant
/// (<see cref="DesktopScaleModel.WindowToDesktop"/>/<see cref="DesktopScaleModel.DesktopToWindow"/> both
/// short-circuit to identity before ever reading it) — so the Embedded tests below exercise the REAL
/// <see cref="PanelGeometryWriter"/>/<see cref="PanelWindowCoordinates"/> adapter at every representative
/// scale. In TopLevel mode the plan's invariant only holds when the window's real DPI equals the
/// snapshot's scale (<c>s_w == s</c>, plan §2/§4.1) — true in production (both come from the same real OS
/// DPI) but NOT reproducible for an arbitrary mocked scale in a CI box that only ever runs at its own,
/// single, real DPI (this file has no second monitor to actually change it — the same environment
/// constraint release-hardening F1.0 documents). So TopLevel is tested two ways: once live, at exactly
/// THIS environment's own real DPI (self-consistent, whatever machine runs the suite); and once as pure
/// math, calling <see cref="DesktopScaleModel.WindowToDesktop"/>/<see cref="DesktopScaleModel.DesktopToWindow"/>
/// directly with an EXPLICIT <c>windowScale</c> parameter equal to the snapshot's own scale (bypassing the
/// live query entirely) — proving the same formula holds at every representative scale
/// (<c>1, 1.25, 1.5, 2</c>) without needing a real per-monitor-DPI desktop to do it.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PersistedGeometryInvarianceTests
{
    /// <summary>Representative scales the plan calls out (§7).</summary>
    public static TheoryData<double> Scales => new() { 1d, 1.25d, 1.5d, 2d };

    /// <summary>One persisted panel's geometry fixture.</summary>
    /// <param name="Name">A short, human-readable label (assertion messages only).</param>
    /// <param name="GeometryMode">The panel's <see cref="ChildWindowModel.GeometryMode"/>.</param>
    /// <param name="Position">The persisted position, in desktop-DIP.</param>
    /// <param name="Size">The persisted size, in DIP.</param>
    /// <param name="IsRolledUp">Whether the panel is currently rolled up.</param>
    /// <param name="MonitorDeviceId">The monitor device id a grid-aligned write also supplies, or <see langword="null"/>.</param>
    /// <param name="WidthSpanUnits">The grid-aligned width span, or <see langword="null"/>.</param>
    /// <param name="HeightSpanUnits">The grid-aligned height span, or <see langword="null"/>.</param>
    private sealed record Fixture(
        string Name,
        PanelGeometryMode GeometryMode,
        DrawingPoint Position,
        DrawingSize Size,
        bool IsRolledUp,
        string? MonitorDeviceId,
        int? WidthSpanUnits,
        int? HeightSpanUnits);

    /// <summary>Freeform, grid-aligned (secondary monitor, negative DIP coordinates), and rolled-up.</summary>
    private static readonly Fixture[] Fixtures =
    [
        new("LegacyFreeform", PanelGeometryMode.LegacyFreeform, new DrawingPoint(64, 96), new DrawingSize(300, 200), false, null, null, null),
        new("GridAlignedOnSecondaryNegative", PanelGeometryMode.GridAligned, new DrawingPoint(-1200, -300), new DrawingSize(240, 160), false, @"\\.\DISPLAY2", 30, 20),
        new("RolledUp", PanelGeometryMode.LegacyFreeform, new DrawingPoint(101, 205), new DrawingSize(300, 400), true, null, null, null),
    ];

    // ── Embedded: the live PanelGeometryWriter/PanelWindowCoordinates adapter, every representative scale ──
    // (WindowToDesktop/DesktopToWindow short-circuit to identity for Embedded before ever reading the
    // window's real DPI, so the live query's environment-bound value cannot affect this case.)

    /// <summary>
    /// Verifies that <see cref="PanelGeometryWriter.Apply"/>, on an Embedded uniform snapshot, writes
    /// <c>Window.Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> AND <see cref="ChildWindowModel.Position"/>/
    /// <see cref="ChildWindowModel.Size"/>/<see cref="ChildWindowModel.MonitorDeviceId"/>/spans exactly as
    /// given — bit-identical to the pre-F 0.6.1 bare assignment — at every representative scale.
    /// </summary>
    /// <param name="scale">The uniform DPI scale under test.</param>
    [Theory]
    [MemberData(nameof(Scales))]
    public void Apply_EmbeddedUniformSnapshot_IsBitIdenticalToThePreFFormula(double scale)
    {
        foreach (var fixture in Fixtures)
        {
            StaTestRunner.Run(() =>
            {
                var window = new Window();
                var model = BuildModel(fixture);
                var scaleProvider = UniformScaleProvider(DesktopLayerMode.Embedded, scale);
                var writer = new PanelGeometryWriter(window, () => model, scaleProvider);

                Apply(writer, fixture);

                AssertWindowMatchesFixture(window, fixture);
                AssertModelMatchesFixture(model, fixture);
            });
        }
    }

    /// <summary>
    /// Verifies the read-back half — <c>ChildWindow.OnLocationChanged</c>'s own formula
    /// (<see cref="PanelWindowCoordinates.ToDesktopBounds"/> then an <c>(int)</c> truncation, deliberately
    /// never <see cref="Math.Round(double)"/> — see that handler's own remarks) — on an Embedded uniform
    /// snapshot, is bit-identical to a bare <c>Window.Left</c>/<c>Top</c> read, at every representative scale.
    /// </summary>
    /// <param name="scale">The uniform DPI scale under test.</param>
    [Theory]
    [MemberData(nameof(Scales))]
    public void OnLocationChangedFormula_EmbeddedUniformSnapshot_IsBitIdenticalToThePreFFormula(double scale)
    {
        foreach (var fixture in Fixtures)
        {
            StaTestRunner.Run(() =>
            {
                var window = new Window { Left = fixture.Position.X, Top = fixture.Position.Y };
                var scaleProvider = UniformScaleProvider(DesktopLayerMode.Embedded, scale);

                Assert.Equal(fixture.Position, ReadBack(window, scaleProvider));
            });
        }
    }

    // ── TopLevel, live: self-consistent at exactly THIS environment's own real window DPI ───────────

    /// <summary>
    /// Verifies <see cref="PanelGeometryWriter.Apply"/> on a TopLevel uniform snapshot built at exactly
    /// this test host's own real (live-queried) window DPI — the one self-consistent TopLevel scale a CI
    /// box without a real mixed-DPI desktop can actually exercise end to end (see this class's remarks) —
    /// is bit-identical to the pre-F formula.
    /// </summary>
    [Fact]
    public void Apply_TopLevelUniformSnapshot_AtThisEnvironmentsOwnRealScale_IsBitIdenticalToThePreFFormula()
    {
        foreach (var fixture in Fixtures)
        {
            StaTestRunner.Run(() =>
            {
                var window = new Window();
                var model = BuildModel(fixture);
                var scaleProvider = UniformScaleProvider(DesktopLayerMode.TopLevel, LiveEnvironmentScale(window));
                var writer = new PanelGeometryWriter(window, () => model, scaleProvider);

                Apply(writer, fixture);

                AssertWindowMatchesFixture(window, fixture);
                AssertModelMatchesFixture(model, fixture);
            });
        }
    }

    /// <summary>
    /// Verifies the read-back half on a TopLevel uniform snapshot built at exactly this test host's own
    /// real window DPI — see <see cref="Apply_TopLevelUniformSnapshot_AtThisEnvironmentsOwnRealScale_IsBitIdenticalToThePreFFormula"/>.
    /// </summary>
    [Fact]
    public void OnLocationChangedFormula_TopLevelUniformSnapshot_AtThisEnvironmentsOwnRealScale_IsBitIdenticalToThePreFFormula()
    {
        foreach (var fixture in Fixtures)
        {
            StaTestRunner.Run(() =>
            {
                var window = new Window { Left = fixture.Position.X, Top = fixture.Position.Y };
                var scaleProvider = UniformScaleProvider(DesktopLayerMode.TopLevel, LiveEnvironmentScale(window));

                Assert.Equal(fixture.Position, ReadBack(window, scaleProvider));
            });
        }
    }

    // ── TopLevel, pure math: s_w == s explicitly, every representative scale ────────────────────────
    // (Bypasses the live DPI query entirely — DesktopScaleModel.WindowToDesktop/DesktopToWindow are the
    // exact functions PanelWindowCoordinates delegates to; this is the formula-level completion of the
    // live TopLevel test above for scales this environment cannot actually put a window at.)

    /// <summary>
    /// Verifies that <see cref="DesktopScaleModel.DesktopToWindow"/>/<see cref="DesktopScaleModel.WindowToDesktop"/>
    /// — the exact functions <see cref="PanelWindowCoordinates"/> delegates to — are the identity, round
    /// trip, for a TopLevel uniform snapshot whenever the window's own DPI scale (<c>s_w</c>) equals the
    /// snapshot's scale, at every representative scale, for every fixture.
    /// </summary>
    /// <param name="scale">The uniform DPI scale under test (also <c>s_w</c>, matching it — plan §2/§4.1).</param>
    [Theory]
    [MemberData(nameof(Scales))]
    public void WindowAdapterFormulas_TopLevelUniformSnapshot_MatchingWindowScale_IsIdentity(double scale)
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, scale, scale, []);

        foreach (var fixture in Fixtures)
        {
            var desktopBounds = new Rect(fixture.Position.X, fixture.Position.Y, fixture.Size.Width, fixture.Size.Height);

            var windowBounds = model.DesktopToWindow(desktopBounds, scale);
            Assert.Equal(desktopBounds.TopLeft, windowBounds);

            var readBack = model.WindowToDesktop(new Rect(windowBounds, desktopBounds.Size), scale);
            Assert.Equal(desktopBounds, readBack);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static void Apply(PanelGeometryWriter writer, Fixture fixture)
    {
        var span = fixture.WidthSpanUnits is { } w && fixture.HeightSpanUnits is { } h ? new GridSpan(w, h) : (GridSpan?)null;
        var bounds = new Rect(fixture.Position.X, fixture.Position.Y, fixture.Size.Width, fixture.Size.Height);

        writer.Apply(bounds, span, fixture.MonitorDeviceId);
    }

    private static void AssertWindowMatchesFixture(Window window, Fixture fixture)
    {
        Assert.Equal((double)fixture.Position.X, window.Left);
        Assert.Equal((double)fixture.Position.Y, window.Top);
        Assert.Equal((double)fixture.Size.Width, window.Width);
        Assert.Equal((double)fixture.Size.Height, window.Height);
    }

    private static void AssertModelMatchesFixture(ChildWindowModel model, Fixture fixture)
    {
        Assert.Equal(fixture.Position, model.Position);
        Assert.Equal(fixture.Size, model.Size);
        Assert.Equal(fixture.MonitorDeviceId, model.MonitorDeviceId);
        Assert.Equal(fixture.WidthSpanUnits, model.WidthSpanUnits);
        Assert.Equal(fixture.HeightSpanUnits, model.HeightSpanUnits);
    }

    private static DrawingPoint ReadBack(Window window, IDesktopScaleProvider scaleProvider)
    {
        var desktopPosition = PanelWindowCoordinates.ToDesktopBounds(
            window, new Rect(window.Left, window.Top, 0, 0), scaleProvider.Current).TopLeft;
        return new DrawingPoint((int)desktopPosition.X, (int)desktopPosition.Y);
    }

    private static ChildWindowModel BuildModel(Fixture fixture) => new()
    {
        GeometryMode = fixture.GeometryMode,
        Position = fixture.Position,
        Size = fixture.Size,
        IsRolledUp = fixture.IsRolledUp,
        WidthSpanUnits = fixture.WidthSpanUnits,
        HeightSpanUnits = fixture.HeightSpanUnits,
    };

    private static IDesktopScaleProvider UniformScaleProvider(DesktopLayerMode mode, double scale)
    {
        var model = DesktopScaleModel.Create(mode, scale, scale, []);
        var mock = new Mock<IDesktopScaleProvider>();
        mock.SetupGet(p => p.Current).Returns(model);
        mock.SetupGet(p => p.Mode).Returns(model.Mode);
        return mock.Object;
    }

    /// <summary>
    /// This test host's own real, live-queried window DPI scale (see this class's remarks) — a fallback
    /// TopLevel/scale-1 snapshot supplies <see cref="PanelWindowCoordinates.GetWindowScale"/>'s fallback
    /// value in the (not expected here) case the query itself fails.
    /// </summary>
    private static double LiveEnvironmentScale(Window window)
        => PanelWindowCoordinates.GetWindowScale(window, DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, []));
}
