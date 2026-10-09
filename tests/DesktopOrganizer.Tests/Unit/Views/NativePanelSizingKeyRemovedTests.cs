using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.UI.Properties;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Views.Settings;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// ADR-022 phase 1 (docs/PLANS/native-resize-single-path.md section 4, explicit user permission 2026-10-10):
/// the <c>NativePanelSizing</c> user setting is gone together with the WPF Thumb resize path. A stale
/// <c>&lt;setting name="NativePanelSizing"&gt;</c> element in an existing <c>user.config</c> is simply
/// ignored by <c>LocalFileSettingsProvider</c> (only declared properties are read; the orphan stays in the
/// file untouched), so these tests pin the declared surface: the key is not declared, not exposed by
/// <see cref="IAppSettings"/>, and no settings control binds to it.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativePanelSizingKeyRemovedTests
{
    /// <summary>
    /// <c>Settings.settings</c> no longer declares <c>NativePanelSizing</c> (metadata only - never the
    /// user's actual <c>user.config</c>), while its neighbours are untouched.
    /// </summary>
    [Fact]
    public void SettingsMetadata_DoesNotDeclareNativePanelSizing_ButKeepsNeighbouringKeys()
    {
        Assert.Null(Settings.Default.Properties["NativePanelSizing"]);
        Assert.NotNull(Settings.Default.Properties["SnapResizeToIconGrid"]);
        Assert.NotNull(Settings.Default.Properties["GridSnapDistance"]);
    }

    /// <summary><see cref="IAppSettings"/> exposes no <c>NativePanelSizing</c> member any more.</summary>
    [Fact]
    public void IAppSettings_HasNoNativePanelSizingMember()
    {
        Assert.Null(typeof(IAppSettings).GetProperty("NativePanelSizing", BindingFlags.Instance | BindingFlags.Public));
    }

    /// <summary>The General section no longer hosts a toggle bound to <c>NativePanelSizing</c>.</summary>
    [Fact]
    public void GeneralSection_HasNoNativePanelSizingToggle()
    {
        var found = StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            return FindNativeResizeToggle(new GeneralSection()) is not null;
        });

        Assert.False(found);
    }

    private static CheckBox? FindNativeResizeToggle(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is CheckBox checkBox
                && System.Windows.Data.BindingOperations.GetBinding(checkBox, ToggleButton.IsCheckedProperty)?.Path.Path
                    == "NativePanelSizing")
            {
                return checkBox;
            }

            var nested = FindNativeResizeToggle(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
