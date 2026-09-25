using System.Reflection;
using System.Windows;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Structural tests for the two anti-conflict partials Stage S creates
/// (docs/PLANS/workspace-grid-03-04-integration.md §1.4/§4.2): <c>ChildWindow.WorkspaceGrid.cs</c>
/// (P03's placeholder "Align to workspace grid" handler) and <c>ChildWindow.Sizing.cs</c> (P04's
/// placeholder "Size" preset handler). Pure reflection — no <see cref="ChildWindow"/> instance is
/// constructed (it needs eight DI dependencies and a live <c>Application.Resources["LayoutEditMode"]</c>
/// entry to fully wire up its <c>ChildWindow.xaml</c> bindings, neither of which is worth standing up
/// just to prove two placeholder no-op handlers exist with the right shape); the corresponding
/// <c>Click="..."</c> wiring in <c>ChildWindow.xaml</c> is itself already verified by
/// <c>dotnet build</c>'s markup compiler, which fails the build if either handler did not exist or had
/// an incompatible signature. Gating correctness (<c>IsEnabled</c> bound to <c>CanEnterEditLayout</c> —
/// the same computed property the pre-existing "Edit layout" item already uses) is covered by
/// <c>LayoutEditModeServiceTests.CanEnterEditLayout_IsInverseOfIsLayoutLocked</c>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowWorkspaceGridPartialTests
{
    /// <summary>
    /// Verifies that <c>ChildWindow</c>'s private <c>AlignToGridMenu_Click</c> handler exists with the
    /// shape of a routed event handler.
    /// </summary>
    [Fact]
    public void AlignToGridMenu_Click_Exists_AsPrivateInstanceRoutedEventHandler()
    {
        var method = typeof(ChildWindow).GetMethod(
            "AlignToGridMenu_Click", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        AssertIsRoutedEventHandlerShape(method!);
    }

    /// <summary>
    /// Verifies that <c>ChildWindow</c>'s private <c>SizePresetMenu_Click</c> handler exists with the
    /// shape of a routed event handler.
    /// </summary>
    [Fact]
    public void SizePresetMenu_Click_Exists_AsPrivateInstanceRoutedEventHandler()
    {
        var method = typeof(ChildWindow).GetMethod(
            "SizePresetMenu_Click", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        AssertIsRoutedEventHandlerShape(method!);
    }

    /// <summary>
    /// P03: <c>WindowManager.OnMonitorsChanged</c> re-applies grid geometry to a live panel through this
    /// single entry point (rather than writing <c>window.Left</c>/<c>Top</c> directly), so that the write
    /// goes through the panel's own <c>PanelGeometryWriter</c> guard. A signature regression here would
    /// break that guarantee silently, so its shape is pinned.
    /// </summary>
    [Fact]
    public void ApplyWorkspaceGridGeometry_Exists_WithTheGeometryWriterShape()
    {
        var method = typeof(ChildWindow).GetMethod(
            "ApplyWorkspaceGridGeometry", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(Rect), parameters[0].ParameterType);
        Assert.Equal(typeof(DesktopOrganizer.UI.Models.GridSpan?), parameters[1].ParameterType);
        Assert.Equal(typeof(string), parameters[2].ParameterType);
    }

    /// <summary>
    /// P03: both "Align to workspace grid" call sites (the panel's own menu and
    /// <c>WindowManager.AlignPanelsToWorkspaceGrid</c>) read the panel's bounds through this one accessor,
    /// which substitutes the EXPANDED height while the panel is rolled up.
    /// </summary>
    [Fact]
    public void GetExpandedBoundsWindowSpace_Exists_ReturningARect()
    {
        var method = typeof(ChildWindow).GetMethod(
            "GetExpandedBoundsWindowSpace", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Rect), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    private static void AssertIsRoutedEventHandlerShape(MethodInfo method)
    {
        Assert.Equal(typeof(void), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(object), parameters[0].ParameterType);
        Assert.True(typeof(RoutedEventArgs).IsAssignableFrom(parameters[1].ParameterType));
    }
}
