using System.Windows.Controls;
using DesktopOrganizer.UI.Views.Settings;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Regression tests for the settings-window section <see cref="UserControl"/>s (ADR-031): each one must
/// construct — i.e. its <c>InitializeComponent()</c> must resolve every <c>StaticResource</c> it uses —
/// with only the APPLICATION-level resource chain available.
/// </summary>
/// <remarks>
/// The sections are instantiated by <c>MainWindow.xaml</c>'s <c>ContentControl</c> through
/// <c>DataTemplate</c>s, so their XAML is parsed BEFORE they are attached to the window's visual tree.
/// <c>StaticResource</c> lookups made during that parse therefore never see <c>Window.Resources</c> — only
/// the control's own resources and <c>Application.Current.Resources</c>. The first release of the redesign
/// merged <c>Resources/Styles/Settings.xaml</c> into <c>MainWindow.xaml</c>'s <c>Window.Resources</c>
/// only, which built and passed every ViewModel test yet threw
/// <c>XamlParseException: Cannot find resource named 'Settings.PageHeader'</c> the moment the window
/// opened. <c>App.xaml.cs</c> now merges <c>Settings.xaml</c> app-wide, and
/// <see cref="WpfApplicationHost"/> mirrors that list, so these tests fail again if either side drifts.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SettingsSectionsInitializeTests
{
    /// <summary>Every section type, so a newly added section cannot be forgotten here.</summary>
    public static TheoryData<Type> SectionTypes =>
    [
        typeof(GeneralSection),
        typeof(LayoutSnappingSection),
        typeof(PanelsSection),
        typeof(NewPanelDefaultsSection),
        typeof(PagesSection),
        typeof(SystemIconsSection),
        typeof(BackupSection),
        typeof(AboutSection),
    ];

    /// <summary>
    /// Constructs the section with only application-level resources in scope (no hosting window), which
    /// is exactly the context <c>InitializeComponent()</c> runs in at runtime.
    /// </summary>
    /// <param name="sectionType">The <see cref="UserControl"/> subclass to construct.</param>
    [Theory]
    [MemberData(nameof(SectionTypes))]
    public void Section_ConstructsWithApplicationResourcesOnly(Type sectionType)
    {
        var control = StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            return (UserControl)Activator.CreateInstance(sectionType)!;
        });

        Assert.NotNull(control);
        Assert.IsType(sectionType, control);
    }
}
