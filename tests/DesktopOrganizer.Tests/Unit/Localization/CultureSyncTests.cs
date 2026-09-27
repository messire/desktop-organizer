using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <c>DesktopOrganizer.UI.Localization.CultureSync</c> (docs/PLANS/localization.md
/// §2.4/§4.1, internal — reached only via <c>InternalsVisibleTo</c>).
/// </summary>
/// <remarks>
/// Runs on a dedicated STA thread (<see cref="StaTestRunner"/>) — constructing/showing a real
/// <see cref="Window"/> requires it, same as every other WPF-window-constructing fixture in this
/// project. In the <c>GlobalCulture</c> collection (<c>DisableParallelization = true</c>) because this
/// mutates <see cref="CultureInfo.CurrentUICulture"/>/<see cref="CultureInfo.DefaultThreadCurrentUICulture"/>
/// — restored in a <see langword="finally"/> block, exactly like
/// <c>LocalizationServiceTests.ApplyLanguage_DoesNotTouchThreadCulture</c>'s own convention.
/// <para>
/// The entire test body runs inside <c>app.Dispatcher.Invoke(...)</c>, not just directly on
/// <see cref="StaTestRunner"/>'s own worker thread: <see cref="Application"/> is a process-wide
/// singleton (<c>WpfApplicationHost.EnsureApplication()</c>'s own remarks) that may already have been
/// constructed — and be permanently pumped — by an EARLIER test's OWN dedicated STA thread.
/// <c>Application.Windows</c> (which <c>CultureSync.ApplyCulture</c> reads) is dispatcher-affine, so
/// touching it from a foreign STA thread throws. Wrapping the whole body in
/// <see cref="System.Windows.Threading.Dispatcher.Invoke(Action)"/> against <c>app</c>'s OWN dispatcher
/// makes this correct either way: a same-thread call (this test happens to be the one constructing
/// <see cref="Application"/>) takes WPF's own synchronous same-thread fast path, while a foreign-thread
/// call genuinely marshals onto the real owner.
/// </para>
/// </remarks>
[Collection("GlobalCulture")]
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class CultureSyncTests
{
    /// <summary>
    /// Verifies the full contract: a shown <see cref="Window"/> is stamped with the current culture's
    /// <see cref="FrameworkElement.Language"/> once loaded, and a subsequent
    /// <c>ILocalizationService.CultureChanged</c> updates BOTH that already-open window's
    /// <see cref="FrameworkElement.Language"/> AND the UI thread's own culture — never
    /// <see cref="CultureInfo.CurrentCulture"/> (L-6, regional format stays untouched).
    /// </summary>
    [Fact]
    public void Attach_ShownWindow_GetsLanguageOnLoad_AndCultureChangedUpdatesWindowAndThreadCulture()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();

            app.Dispatcher.Invoke(() =>
            {
                // Application.Current is a shared, process-wide singleton for the rest of this
                // `dotnet test` run (WpfApplicationHost's own remarks) — ShutdownMode's default,
                // OnLastWindowClose, would tear the WHOLE Application down the moment this test's own
                // Window.Close() below happens to be the last one currently tracked in
                // Application.Windows, breaking every later test that assumes Application.Current stays
                // alive forever. OnExplicitShutdown is never undone afterward, deliberately: nothing in
                // this test suite ever calls Application.Shutdown() itself.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var originalCurrentUiCulture = CultureInfo.CurrentUICulture;
                var originalDefaultThreadCulture = CultureInfo.DefaultThreadCurrentUICulture;
                var originalCurrentCulture = CultureInfo.CurrentCulture;

                var service = TestLocalization.English;
                Window? window = null;

                try
                {
                    CultureSync.Attach(service, app);

                    window = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        Width = 10,
                        Height = 10,
                        Left = -32000,
                        Top = -32000,
                    };
                    window.Show();

                    Assert.Equal("en", window.Language.IetfLanguageTag);

                    service.ApplyLanguage("ru");

                    Assert.Equal("ru", window.Language.IetfLanguageTag);
                    Assert.Equal("ru", CultureInfo.CurrentUICulture.Name);
                    Assert.Equal("ru", CultureInfo.DefaultThreadCurrentUICulture?.Name);

                    // L-6: CultureSync never touches CultureInfo.CurrentCulture (regional format).
                    Assert.Equal(originalCurrentCulture, CultureInfo.CurrentCulture);
                }
                finally
                {
                    window?.Close();
                    CultureSync.ResetForTests();
                    service.ApplyLanguage("en");
                    CultureInfo.CurrentUICulture = originalCurrentUiCulture;
                    CultureInfo.DefaultThreadCurrentUICulture = originalDefaultThreadCulture;
                }
            });
        });
    }

    /// <summary>
    /// Localization fix-cycle 1 FIX-C item 6: pins <see cref="CultureSync.Attach"/>'s own documented
    /// idempotency contract — calling it again with a DIFFERENT <c>ILocalizationService</c> instance
    /// swaps which instance drives the sync (unsubscribing the old one first) rather than stacking a
    /// second subscription. After the second <see cref="CultureSync.Attach"/>, the FIRST service's own
    /// <c>CultureChanged</c> must no longer reach any open window/the UI thread culture at all, while the
    /// SECOND service's does.
    /// </summary>
    [Fact]
    public void Attach_CalledAgainWithDifferentService_SwitchesSubscriptionAndOldServiceNoLongerSyncs()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();

            app.Dispatcher.Invoke(() =>
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var originalCurrentUiCulture = CultureInfo.CurrentUICulture;
                var originalDefaultThreadCulture = CultureInfo.DefaultThreadCurrentUICulture;

                var firstService = TestLocalization.English;
                var secondService = TestLocalization.English;
                Window? window = null;

                try
                {
                    CultureSync.Attach(firstService, app);

                    window = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        Width = 10,
                        Height = 10,
                        Left = -32000,
                        Top = -32000,
                    };
                    window.Show();
                    Assert.Equal("en", window.Language.IetfLanguageTag);

                    CultureSync.Attach(secondService, app);

                    // The FIRST service is no longer subscribed: its own CultureChanged must not reach
                    // the already-open window or the UI thread culture at all.
                    firstService.ApplyLanguage("ru");

                    Assert.Equal("en", window.Language.IetfLanguageTag);
                    Assert.Equal(originalCurrentUiCulture, CultureInfo.CurrentUICulture);

                    // The SECOND service IS the one now driving the sync.
                    secondService.ApplyLanguage("ru");

                    Assert.Equal("ru", window.Language.IetfLanguageTag);
                    Assert.Equal("ru", CultureInfo.CurrentUICulture.Name);
                }
                finally
                {
                    window?.Close();
                    CultureSync.ResetForTests();
                    firstService.ApplyLanguage("en");
                    secondService.ApplyLanguage("en");
                    CultureInfo.CurrentUICulture = originalCurrentUiCulture;
                    CultureInfo.DefaultThreadCurrentUICulture = originalDefaultThreadCulture;
                }
            });
        });
    }
}
