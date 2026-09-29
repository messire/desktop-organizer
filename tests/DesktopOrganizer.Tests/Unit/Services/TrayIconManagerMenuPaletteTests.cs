using System.Globalization;
using System.Windows.Forms;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Localization;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="TrayIconManager.SetMenuPalette"/> (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.9 variant A, phase 6.4, T-11): the renderer is assigned to the root
/// strip AND every submenu drop-down (including "Pages" after a <see cref="TrayIconManager.SetPages"/>
/// rebuild), <see langword="null"/> selects the system renderer, a repeated equal palette is a no-op, a
/// palette arriving while the menu is open waits for the next Opening, MAJOR-1 (the strip is handed to the
/// tray exactly once) holds, the DWM popup style reaches every popup window, and <see cref="TrayIconManager.Dispose"/>
/// stops everything. <see cref="INotifyIconAdapter"/>/<see cref="ITrayMenuWindowStyler"/> are mocked.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class TrayIconManagerMenuPaletteTests : IDisposable
{
    private static readonly TrayMenuPalette Light = new(0xFFF9F9F9, 0xFFE5E5E5, 0xFF1B1B1B, 0xFF8A8A8A, 0xFFEFEFEF, 0xFFE5E5E5, 0xFF1B1B1B, 4);
    private static readonly TrayMenuPalette Dark = new(0xFF2C2C2C, 0xFF3A3A3A, 0xFFFFFFFF, 0xFF7A7A7A, 0xFF3B3B3B, 0xFF404040, 0xFFFFFFFF, 4);

    private readonly Mock<INotifyIconAdapter> _adapter = new();
    private readonly Mock<ITrayMenuWindowStyler> _styler = new();
    private readonly LocalizationService _localization = new(CultureInfo.GetCultureInfo("en"));
    private readonly TrayIconManager _sut;
    private ContextMenuStrip? _menu;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManagerMenuPaletteTests"/> class.
    /// </summary>
    public TrayIconManagerMenuPaletteTests()
    {
        _adapter
            .Setup(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()))
            .Callback<ContextMenuStrip>(menu => _menu = menu);

        _sut = new TrayIconManager(_adapter.Object, _localization, _styler.Object);
    }

    /// <inheritdoc />
    public void Dispose() => _sut.Dispose();

    /// <summary>Before any palette the menu keeps its legacy renderer and no metrics are touched.</summary>
    [Fact]
    public void BeforeSetMenuPalette_NoRendererAssigned()
    {
        _sut.Initialize();

        Assert.Null(_sut.MenuRenderer);
        Assert.IsNotType<TrayMenuRenderer>(_menu!.Renderer);
    }

    /// <summary>A palette assigns ONE <see cref="TrayMenuRenderer"/> to the root strip and to the "Pages" drop-down.</summary>
    [Fact]
    public void SetMenuPalette_AssignsRendererToRootAndPagesDropDown()
    {
        _sut.Initialize();
        _sut.SetPages([new TrayPageEntry("a", "A", true), new TrayPageEntry("b", "B", false)]);

        _sut.SetMenuPalette(Light);

        var renderer = Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer);
        Assert.Equal(Light, renderer.Palette);
        Assert.Same(renderer, _menu!.Renderer);
        Assert.Same(renderer, PagesItem().DropDown.Renderer);
        Assert.Equal(2, _sut.MenuStrips().Count());
    }

    /// <summary>
    /// After a composition-changing <see cref="TrayIconManager.SetPages"/> rebuild, the "Pages" drop-down still
    /// uses the same renderer and the freshly built page items carry the §2.6 row metrics.
    /// </summary>
    [Fact]
    public void SetPages_RebuildAfterPalette_KeepsRenderer_AndAppliesRowMetricsToNewItems()
    {
        _sut.Initialize();
        _sut.SetPages([new TrayPageEntry("a", "A", true)]);
        _sut.SetMenuPalette(Dark);
        var renderer = _sut.MenuRenderer;

        _sut.SetPages([new TrayPageEntry("a", "A", true), new TrayPageEntry("c", "C", false)]);

        var dropDown = PagesItem().DropDown;
        Assert.Same(renderer, dropDown.Renderer);
        var newItem = Assert.IsType<ToolStripMenuItem>(Assert.Single(dropDown.Items.Cast<ToolStripItem>(), i => i.Name == TrayIconManager.PageItemNamePrefix + "c"));
        var textHeight = TextRenderer.MeasureText("Ag", dropDown.Font).Height;
        Assert.Equal(TrayMenuRenderer.ItemPadding(dropDown.DeviceDpi, textHeight, newItem.Padding), newItem.Padding);
        Assert.True(newItem.Padding.Vertical > 2, "row metrics not applied to the rebuilt page item");
    }

    /// <summary>High contrast (null) switches every strip to the stock system renderer.</summary>
    [Fact]
    public void SetMenuPalette_Null_UsesSystemRendererOnEveryStrip()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);

        _sut.SetMenuPalette(null);

        var renderer = Assert.IsType<ToolStripSystemRenderer>(_sut.MenuRenderer);
        Assert.All(_sut.MenuStrips(), strip => Assert.Same(renderer, strip.Renderer));
    }

    /// <summary>An equal palette (record value equality, even a different instance) is a no-op; a different one replaces the renderer.</summary>
    [Fact]
    public void SetMenuPalette_SamePaletteIsNoOp_DifferentPaletteReplacesRenderer()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);
        var first = _sut.MenuRenderer;

        _sut.SetMenuPalette(Light with { });
        Assert.Same(first, _sut.MenuRenderer);

        _sut.SetMenuPalette(Dark);
        Assert.NotSame(first, _sut.MenuRenderer);
        Assert.Equal(Dark, Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer).Palette);
        Assert.Same(_sut.MenuRenderer, _menu!.Renderer);
    }

    /// <summary>A repeated null is a no-op too.</summary>
    [Fact]
    public void SetMenuPalette_NullTwice_IsNoOp()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(null);
        var first = _sut.MenuRenderer;

        _sut.SetMenuPalette(null);

        Assert.Same(first, _sut.MenuRenderer);
    }

    /// <summary>
    /// MAJOR-1: palette changes, page rebuilds and menu Opening never build or hand over a second strip —
    /// <see cref="INotifyIconAdapter.SetContextMenu"/> is called exactly once.
    /// </summary>
    [Fact]
    public void SetMenuPalette_NeverRebuildsOrReHandsTheStrip()
    {
        _sut.Initialize();
        var menu = _menu;

        _sut.SetMenuPalette(Light);
        _sut.SetMenuPalette(Dark);
        _sut.SetMenuPalette(null);
        _sut.SetPages([new TrayPageEntry("a", "A", true), new TrayPageEntry("b", "B", false)]);
        _sut.HandleMenuOpening();
        _sut.SetMenuPalette(Light);

        _adapter.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);
        Assert.Same(menu, _menu);
    }

    /// <summary>A palette requested while the menu is open is not applied under it — only on the next Opening.</summary>
    [Fact]
    public void SetMenuPalette_WhileMenuOpen_AppliedOnNextOpening()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);
        var lightRenderer = _sut.MenuRenderer;
        _sut.IsMenuOpen = _ => true;

        _sut.SetMenuPalette(Dark);
        Assert.Same(lightRenderer, _sut.MenuRenderer);
        Assert.Same(lightRenderer, _menu!.Renderer);

        _sut.IsMenuOpen = _ => false;
        _sut.HandleMenuOpening();

        Assert.Equal(Dark, Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer).Palette);
        Assert.Same(_sut.MenuRenderer, _menu.Renderer);
        Assert.Same(_sut.MenuRenderer, PagesItem().DropDown.Renderer);
    }

    /// <summary>
    /// The DWM popup style is applied exactly once when a popup gets its window (after a palette), and
    /// re-applied to existing windows on every palette change — including null (system defaults) for high
    /// contrast.
    /// </summary>
    /// <remarks>
    /// WinForms may create the root popup's (hidden) window as early as the first renderer assignment to the
    /// "Pages" drop-down (its layout translates the owner item's position through the root window), so the
    /// test only pins "once per window per palette", not the exact moment.
    /// </remarks>
    [Fact]
    public void WindowStyler_AppliedOnHandleCreated_AndOnPaletteChange()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);

        var handle = _menu!.Handle; // creates the (hidden) popup window → HandleCreated, unless it already exists
        _styler.Verify(s => s.Apply(handle, Light), Times.Once);

        _sut.SetMenuPalette(Dark);
        _styler.Verify(s => s.Apply(handle, Dark), Times.Once);

        _sut.SetMenuPalette(null);
        _styler.Verify(s => s.Apply(handle, null), Times.Once);
    }

    /// <summary>No DWM styling happens before the first palette (legacy look stays untouched).</summary>
    [Fact]
    public void WindowStyler_NotCalledBeforeFirstPalette()
    {
        _sut.Initialize();

        _ = _menu!.Handle;

        _styler.Verify(s => s.Apply(It.IsAny<IntPtr>(), It.IsAny<TrayMenuPalette?>()), Times.Never);
    }

    /// <summary>The two-argument constructor (no styler) still styles the menu, just without DWM calls.</summary>
    [Fact]
    public void WithoutStyler_PaletteStillApplied()
    {
        using var sut = new TrayIconManager(_adapter.Object, _localization);
        sut.Initialize();

        sut.SetMenuPalette(Light);
        _ = _menu!.Handle;

        Assert.IsType<TrayMenuRenderer>(sut.MenuRenderer);
        Assert.Same(sut.MenuRenderer, _menu.Renderer);
    }

    /// <summary>After <see cref="TrayIconManager.Dispose"/> a palette call neither resurrects the strip nor throws.</summary>
    [Fact]
    public void SetMenuPalette_AfterDispose_IsNoOp()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);

        _sut.Dispose();
        var exception = Record.Exception(() => _sut.SetMenuPalette(Dark));

        Assert.Null(exception);
        Assert.Null(_sut.MenuRenderer);
        Assert.Empty(_sut.MenuStrips());
        _adapter.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);
    }

    /// <summary>SetMenuPalette before Initialize builds the (single) strip lazily, like every other entry point.</summary>
    [Fact]
    public void SetMenuPalette_BeforeInitialize_BuildsStripOnce()
    {
        _sut.SetMenuPalette(Light);
        _sut.Initialize();

        _adapter.Verify(a => a.SetContextMenu(It.IsAny<ContextMenuStrip>()), Times.Once);
        Assert.Same(_sut.MenuRenderer, _menu!.Renderer);
    }

    /// <summary>
    /// Review L-7: a palette change disposes the replaced <see cref="TrayMenuRenderer"/> (its cached glyph
    /// fonts) only after every strip was switched to the new one; the renderer in effect stays alive.
    /// </summary>
    [Fact]
    public void SetMenuPalette_DifferentPalette_DisposesReplacedRendererOnly()
    {
        _sut.Initialize();
        _sut.SetPages([new TrayPageEntry("a", "A", true), new TrayPageEntry("b", "B", false)]);
        _sut.SetMenuPalette(Light);
        var first = Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer);

        _sut.SetMenuPalette(Light with { });
        Assert.False(first.IsDisposed);

        _sut.SetMenuPalette(Dark);

        var second = Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.All(_sut.MenuStrips(), strip => Assert.Same(second, strip.Renderer));

        _sut.SetMenuPalette(null);
        Assert.True(second.IsDisposed);
    }

    /// <summary>A palette requested while the menu is open does not dispose the renderer painting it.</summary>
    [Fact]
    public void SetMenuPalette_WhileMenuOpen_KeepsCurrentRendererAliveUntilNextOpening()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);
        var lightRenderer = Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer);
        _sut.IsMenuOpen = _ => true;

        _sut.SetMenuPalette(Dark);
        Assert.False(lightRenderer.IsDisposed);

        _sut.IsMenuOpen = _ => false;
        _sut.HandleMenuOpening();
        Assert.True(lightRenderer.IsDisposed);
    }

    /// <summary>Review L-7: <see cref="TrayIconManager.Dispose"/> disposes the renderer in effect.</summary>
    [Fact]
    public void Dispose_DisposesRendererInEffect()
    {
        _sut.Initialize();
        _sut.SetMenuPalette(Light);
        var renderer = Assert.IsType<TrayMenuRenderer>(_sut.MenuRenderer);

        _sut.Dispose();

        Assert.True(renderer.IsDisposed);
    }

    /// <summary>
    /// Review 6.4 (per-monitor DPI): once a popup is on screen its row metrics are re-asserted at the strip's
    /// current DPI — restoring rows laid out for another DPI; a no-op before the first palette and after Dispose.
    /// </summary>
    [Fact]
    public void HandleMenuStripOpened_ReassertsRowMetrics_OnlyWhileStyled()
    {
        _sut.Initialize();
        var item = Assert.IsType<ToolStripMenuItem>(_menu!.Items.Cast<ToolStripItem>().First(i => i is ToolStripMenuItem));
        var stale = new Padding(1);
        item.Padding = stale;

        _sut.HandleMenuStripOpened(_menu);
        Assert.Equal(stale, item.Padding);

        _sut.SetMenuPalette(Light);
        item.Padding = stale;
        _sut.HandleMenuStripOpened(_menu);

        var textHeight = TextRenderer.MeasureText("Ag", _menu.Font).Height;
        Assert.Equal(TrayMenuRenderer.ItemPadding(_menu.DeviceDpi, textHeight, stale), item.Padding);

        _sut.Dispose();
        Assert.Null(Record.Exception(() => _sut.HandleMenuStripOpened(_menu)));
        Assert.Throws<ArgumentNullException>(() => _sut.HandleMenuStripOpened(null!));
    }

    private ToolStripMenuItem PagesItem() =>
        Assert.IsType<ToolStripMenuItem>(Assert.Single(_menu!.Items.Cast<ToolStripItem>(), i => i.Name == TrayIconManager.PagesItemName));
}
