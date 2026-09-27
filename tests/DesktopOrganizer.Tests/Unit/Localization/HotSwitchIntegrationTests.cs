using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// End-to-end hot-switch integration test (docs/PLANS/localization.md §4.8, WP-6): a real
/// <see cref="LocalizationService"/>, a real <see cref="Window"/> parsed from XAML that exercises BOTH
/// markup extensions (<c>{loc:Loc}</c> on <c>Text</c>/<c>ToolTip</c>/<c>MenuItem.Header</c> and
/// <c>{loc:LocBinding}</c> driving a real value converter) plus <c>CultureSync</c>, all wired together the
/// way <c>App.StartApp</c> wires them for the real app — proving one <c>ILocalizationService.ApplyLanguage</c>
/// call updates every one of them, on the SAME already-constructed elements, in a single call, with no
/// window/control ever rebuilt (D-3/L-4). Deliberately does NOT re-test <c>TrayIconManager</c> (already
/// covered end-to-end in WP-1's own <c>TrayIconManagerTests</c>) or <c>CultureSync</c>'s window/thread-culture
/// contract in isolation (already covered by <c>CultureSyncTests</c>) — this test's own value is proving
/// the FULL chain (both markup extensions + a converter + <c>CultureSync</c>) together, end to end.
/// </summary>
/// <remarks>
/// In the <c>GlobalCulture</c> collection (<c>DisableParallelization = true</c>) for the same reasons as
/// <c>CultureSyncTests</c>: this mutates <see cref="CultureInfo.CurrentUICulture"/>/
/// <see cref="CultureInfo.DefaultThreadCurrentUICulture"/> (restored in <see langword="finally"/>) and
/// calls <see cref="LocalizationSource.Initialize"/> against a throwaway English-pinned service (reset
/// back to "never initialized" in <see langword="finally"/> via <see cref="LocalizationSource.ResetForTests"/>,
/// exactly like <c>LocalizationSourceTests</c>' own convention, so a later test reading
/// <see cref="LocalizationSource.Current"/> for the first time still gets a fresh default English
/// instance rather than this test's own, now-Russian, throwaway one). Runs the whole body inside
/// <c>app.Dispatcher.Invoke(...)</c> against <c>WpfApplicationHost.EnsureApplication()</c>'s OWN
/// dispatcher, same as <c>CultureSyncTests</c>, because <see cref="Application"/> is a process-wide
/// singleton that may already be owned by an earlier test's dedicated STA thread.
/// </remarks>
[Collection("GlobalCulture")]
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class HotSwitchIntegrationTests
{
    /// <summary>
    /// Verifies that a single <c>ILocalizationService.ApplyLanguage</c> call updates a <c>{loc:Loc}</c>-bound
    /// <see cref="TextBlock.Text"/>, a <c>{loc:Loc}</c>-bound <see cref="FrameworkElement.ToolTip"/>, a
    /// <c>{loc:Loc}</c>-bound <c>MenuItem.Header</c>, a <c>{loc:LocBinding}</c>+<c>EnumDisplayNameConverter</c>-bound
    /// <see cref="TextBlock.Text"/>, AND the <c>Window.Language</c>/UI-thread culture <c>CultureSync</c>
    /// maintains — all on the SAME already-constructed elements (never rebuilt).
    /// </summary>
    [Fact]
    public void ApplyLanguage_HotSwitchesTextTooltipHeaderConverterAndWindowLanguage_OnTheSameElements()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();

            app.Dispatcher.Invoke(() =>
            {
                // See CultureSyncTests' own remarks: Application.Current is a shared, process-wide
                // singleton for the rest of this `dotnet test` run — OnExplicitShutdown is never undone
                // afterward, deliberately, and is idempotent to set again here.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var originalCurrentUiCulture = CultureInfo.CurrentUICulture;
                var originalDefaultThreadCulture = CultureInfo.DefaultThreadCurrentUICulture;
                var originalCurrentCulture = CultureInfo.CurrentCulture;

                LocalizationSource.ResetForTests();
                var service = TestLocalization.English;
                Window? window = null;

                try
                {
                    LocalizationSource.Initialize(service);
                    CultureSync.Attach(service, app);

                    const string xaml = """
                        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:loc="clr-namespace:DesktopOrganizer.UI.Localization;assembly=DesktopOrganizer.UI"
                                xmlns:conv="clr-namespace:DesktopOrganizer.UI.Converters;assembly=DesktopOrganizer.UI"
                                xmlns:m="clr-namespace:DesktopOrganizer.UI.Models;assembly=DesktopOrganizer.UI"
                                WindowStyle="None" ShowInTaskbar="False" ShowActivated="False"
                                AllowsTransparency="True" Background="Transparent"
                                Width="10" Height="10" Left="-32000" Top="-32000">
                            <Window.Resources>
                                <conv:EnumDisplayNameConverter x:Key="EnumDisplayName"/>
                            </Window.Resources>
                            <StackPanel>
                                <TextBlock x:Name="Text1"
                                           Text="{loc:Loc Common_Ok_Button}"
                                           ToolTip="{loc:Loc Common_Cancel_Button}"/>
                                <TextBlock x:Name="Text2"
                                           Text="{loc:LocBinding Source={x:Static m:PanelSurfaceStyle.Solid}, Converter={StaticResource EnumDisplayName}}"/>
                                <Menu>
                                    <MenuItem x:Name="MenuItem1" Header="{loc:Loc Common_Ok_Button}"/>
                                </Menu>
                            </StackPanel>
                        </Window>
                        """;

                    window = (Window)XamlReader.Parse(xaml);
                    var text1 = (TextBlock)window.FindName("Text1")!;
                    var text2 = (TextBlock)window.FindName("Text2")!;
                    var menuItem1 = (MenuItem)window.FindName("MenuItem1")!;

                    window.Show();

                    Assert.Equal("OK", text1.Text);
                    Assert.Equal("Cancel", text1.ToolTip);
                    Assert.Equal("Solid", text2.Text);
                    Assert.Equal("OK", menuItem1.Header);
                    Assert.Equal("en", window.Language.IetfLanguageTag);

                    service.ApplyLanguage("ru");

                    // Same instances throughout — re-evaluated in place, never rebuilt (D-3/L-4).
                    Assert.Same(text1, window.FindName("Text1"));
                    Assert.Same(text2, window.FindName("Text2"));
                    Assert.Same(menuItem1, window.FindName("MenuItem1"));

                    Assert.Equal("ОК", text1.Text);
                    Assert.Equal("Отмена", text1.ToolTip);
                    Assert.Equal("Сплошной", text2.Text);
                    Assert.Equal("ОК", menuItem1.Header);
                    Assert.Equal("ru", window.Language.IetfLanguageTag);
                    Assert.Equal("ru", CultureInfo.CurrentUICulture.Name);

                    // L-6: CultureSync never touches the regional format culture.
                    Assert.Equal(originalCurrentCulture, CultureInfo.CurrentCulture);
                }
                finally
                {
                    window?.Close();
                    CultureSync.ResetForTests();
                    service.ApplyLanguage("en");
                    LocalizationSource.ResetForTests();
                    CultureInfo.CurrentUICulture = originalCurrentUiCulture;
                    CultureInfo.DefaultThreadCurrentUICulture = originalDefaultThreadCulture;
                }
            });
        });
    }
}
