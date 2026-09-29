using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// T-8 (docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1, ADR-047 D-2): <see cref="AppThemeService"/>
/// against a fake <see cref="ISystemThemeProvider"/> and a synchronous <c>defer</c> — the gate
/// (<see cref="AppThemeService.DefaultDarkThemeEnabled"/> in production, always <see langword="false"/>) is
/// exercised through the internal test constructor's explicit <c>darkThemeEnabled</c> parameter, never by
/// touching the real production constant. All tests run on the dedicated <see cref="StaTestRunner"/> thread
/// and isolate <see cref="Application.Current"/>'s <see cref="Application.Resources"/> for the duration of
/// each test (a live <c>Border</c>'s <c>{DynamicResource}</c> re-resolution needs a real
/// <see cref="Application"/>) — see <see cref="IsolatedAppResources"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class AppThemeServiceTests
{
    /// <summary>While the gate is closed, <see cref="AppThemeService.Current"/> is always <see cref="AppTheme.Light"/>, regardless of Windows' own app mode.</summary>
    [Fact]
    public void Start_GateClosed_ResolvesLightRegardlessOfWindowsAppMode()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: false, SystemUsesLightTheme: false));
            using var sut = CreateSut(provider, darkThemeEnabled: false);

            sut.Start();

            Assert.Equal(AppTheme.Light, sut.Current);
        });
    }

    /// <summary>With the gate open, <see cref="AppThemeService.Current"/> follows <see cref="SystemThemeSnapshot.AppsUseLightTheme"/>.</summary>
    [Fact]
    public void Start_GateOpen_FollowsAppsUseLightTheme()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: false, SystemUsesLightTheme: true));
            using var sut = CreateSut(provider, darkThemeEnabled: true);

            sut.Start();

            Assert.Equal(AppTheme.Dark, sut.Current);
        });
    }

    /// <summary>High contrast always wins, whether or not the dark-theme gate is open.</summary>
    [Fact]
    public void Start_HighContrast_WinsRegardlessOfGate()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            using var sut = CreateSut(provider, darkThemeEnabled: true, highContrast: true);

            sut.Start();

            Assert.Equal(AppTheme.HighContrast, sut.Current);
        });
    }

    /// <summary>
    /// The primary T-8 scenario: a live <see cref="Border"/> bound via <c>{DynamicResource
    /// Theme.Surface.Card}</c> actually repaints after a Light→Dark switch (the whole point of the
    /// dictionary-swap design, ADR-047 D-2) — with the gate open.
    /// </summary>
    [Fact]
    public void ThemeChanged_LightToDark_RepaintsALiveDynamicResourceConsumer()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            using var sut = CreateSut(provider, darkThemeEnabled: true);
            sut.Start();

            // Hosted in a real (off-screen, never activated) Window — DynamicResource re-resolution on a
            // MergedDictionaries change is driven off the logical/visual tree, which a detached element has
            // none of.
            var border = new Border();
            border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceCard);
            var window = new Window { Content = border, ShowActivated = false, Left = -5000, Top = -5000, Width = 50, Height = 50 };
            try
            {
                window.Show();

                Assert.Equal(ThemePalette.Light.SurfaceCard, ((SolidColorBrush)border.Background).Color);

                provider.RaiseChanged(new SystemThemeSnapshot(AppsUseLightTheme: false, SystemUsesLightTheme: false));

                Assert.Equal(AppTheme.Dark, sut.Current);
                Assert.Equal(ThemePalette.Dark.SurfaceCard, ((SolidColorBrush)border.Background).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A Windows theme change is only applied once the supplied <c>defer</c> callback actually runs, and <see cref="AppThemeService.ThemeChanged"/> carries the newly resolved theme.</summary>
    [Fact]
    public void ThemeChanged_Event_MarshalsThroughDefer_AndCarriesTheResolvedTheme()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            var deferred = new List<Action>();
            using var sut = new AppThemeService(
                provider,
                defer: AddAndReport(deferred),
                highContrastProvider: () => false,
                messageFontSizeProvider: () => 12,
                darkThemeEnabled: true);
            sut.Start();

            AppTheme? raised = null;
            sut.ThemeChanged += (_, theme) => raised = theme;

            provider.RaiseChanged(new SystemThemeSnapshot(AppsUseLightTheme: false, SystemUsesLightTheme: false));

            // Not yet applied — the recompute is queued in `deferred`, not run synchronously.
            Assert.Equal(AppTheme.Light, sut.Current);
            Assert.Null(raised);
            Assert.Single(deferred);

            deferred[0]();

            Assert.Equal(AppTheme.Dark, sut.Current);
            Assert.Equal(AppTheme.Dark, raised);
        });
    }

    /// <summary>A raised <see cref="ISystemThemeProvider.Changed"/> that resolves to the SAME theme is a no-op — idempotent recompute.</summary>
    [Fact]
    public void Recompute_NoActualChange_DoesNotRaiseThemeChanged()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            using var sut = CreateSut(provider, darkThemeEnabled: true);
            sut.Start();

            var raiseCount = 0;
            sut.ThemeChanged += (_, _) => raiseCount++;

            // Same snapshot, same resolved theme (Light) — Recompute must be a no-op.
            provider.RaiseChanged(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));

            Assert.Equal(0, raiseCount);
        });
    }

    // ── M-2/M-1-UI fixes (6.1 review round) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// M-2 fix: a Windows-theme change that resolves to the SAME <see cref="AppTheme.HighContrast"/> value
    /// both times (e.g. switching between two HC schemes, Aquatic → Desert) still rebuilds/re-applies and
    /// raises <see cref="AppThemeService.ThemeChanged"/> when <see cref="SystemThemeSnapshot.SystemColorsFingerprint"/>
    /// differs — closing the pre-fix gap where the old two-dimension check (theme + font size) treated it
    /// as a no-op and left the stale <see cref="SystemColors"/>-built dictionary applied.
    /// </summary>
    [Fact]
    public void Recompute_HighContrastFingerprintChanges_StillRaisesThemeChanged()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true, HighContrast: true, SystemColorsFingerprint: 1));
            using var sut = CreateSut(provider, darkThemeEnabled: true, highContrast: true);
            sut.Start();

            var raiseCount = 0;
            sut.ThemeChanged += (_, _) => raiseCount++;

            // Same resolved AppTheme (HighContrast) both times — only the fingerprint (i.e. the underlying
            // Win32 system colours) changed.
            provider.RaiseChanged(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true, HighContrast: true, SystemColorsFingerprint: 2));

            Assert.Equal(1, raiseCount);
            Assert.Equal(AppTheme.HighContrast, sut.Current);
        });
    }

    /// <summary>
    /// M-1-UI fix: a live <see cref="System.Windows.SystemFonts.MessageFontSize"/> change (e.g. the
    /// Windows "Text size" accessibility setting) rebuilds the dictionary and raises
    /// <see cref="AppThemeService.ThemeChanged"/> even though the resolved <see cref="AppTheme"/> itself is
    /// unchanged — and the rebuilt dictionary actually reflects the new size.
    /// </summary>
    [Fact]
    public void Recompute_MessageFontSizeChangesButThemeDoesNot_StillRebuildsAndRaisesThemeChanged()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            var messageFontSize = 12.0;
            using var sut = new AppThemeService(
                provider,
                defer: action =>
                {
                    action();
                    return true;
                },
                highContrastProvider: () => false,
                messageFontSizeProvider: () => messageFontSize,
                darkThemeEnabled: true);
            sut.Start();

            var raiseCount = 0;
            sut.ThemeChanged += (_, _) => raiseCount++;

            messageFontSize = 16.0;
            InvokeOnStaticPropertyChanged(sut, "MessageFontSize");

            Assert.Equal(1, raiseCount);
            Assert.Equal(AppTheme.Light, sut.Current);

            var app = Application.Current!;
            Assert.Equal(TypeRamp.FromMessageFontSize(16.0).Body, (double)app.Resources[ThemeKeys.FontSizeBody]);
        });
    }

    /// <summary>
    /// M-1-UI fix: <see cref="SystemParameters.StaticPropertyChanged"/> is handled regardless of WHICH
    /// property name it reports (not only <see cref="SystemParameters.HighContrast"/>) — WPF does not
    /// guarantee a stable name for every settings-change category — and a burst of near-simultaneous
    /// notifications (Windows' own behaviour for a single user action) coalesces into exactly one deferred
    /// recompute rather than one per notification.
    /// </summary>
    [Fact]
    public void OnStaticPropertyChanged_AnyPropertyName_DefersOnce_EvenForRapidRepeatedNotifications()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            var deferred = new List<Action>();
            using var sut = new AppThemeService(
                provider,
                defer: AddAndReport(deferred),
                highContrastProvider: () => false,
                messageFontSizeProvider: () => 12,
                darkThemeEnabled: true);
            sut.Start();
            deferred.Clear(); // Start()'s own baseline apply runs synchronously, never through `defer`.

            InvokeOnStaticPropertyChanged(sut, "SomeUnrelatedProperty");
            InvokeOnStaticPropertyChanged(sut, "AnotherUnrelatedProperty");

            Assert.Single(deferred);

            deferred[0]();

            // The queued recompute is allowed to run again after the previous one completed — this adds a
            // SECOND entry to the same cumulative `deferred` list (not a replacement of the first).
            InvokeOnStaticPropertyChanged(sut, "SomeUnrelatedProperty");
            Assert.Equal(2, deferred.Count);
        });
    }

    /// <summary>
    /// L-3 fix (6.5 review round): when <c>defer</c> reports it could not queue the callback at all (e.g. no
    /// live <see cref="Application.Current"/>/dispatcher already shutting down — see
    /// <see cref="AppThemeService"/>'s own <c>DeferToUiThread</c>), the "recompute pending" flag must NOT
    /// stay stuck <see langword="true"/> forever — otherwise every later
    /// <see cref="SystemParameters.StaticPropertyChanged"/> notification for the remainder of the process
    /// would be silently swallowed by the very first "return early because a recompute is already pending"
    /// guard.
    /// </summary>
    [Fact]
    public void OnStaticPropertyChanged_DeferFailsToQueue_ResetsPendingFlag_SoALaterNotificationStillDefers()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            var deferred = new List<Action>();
            var queueSucceeds = false;
            using var sut = new AppThemeService(
                provider,
                defer: action =>
                {
                    if (!queueSucceeds) return false;
                    deferred.Add(action);
                    return true;
                },
                highContrastProvider: () => false,
                messageFontSizeProvider: () => 12,
                darkThemeEnabled: true);
            sut.Start();

            InvokeOnStaticPropertyChanged(sut, "SomeUnrelatedProperty");
            Assert.Empty(deferred); // The queue attempt failed — nothing was ever recorded.

            queueSucceeds = true;
            InvokeOnStaticPropertyChanged(sut, "SomeUnrelatedProperty");

            // Had the flag stayed stuck, this second call would have returned early (already pending) and
            // `deferred` would still be empty.
            Assert.Single(deferred);
        });
    }

    /// <summary>A second <see cref="AppThemeService.Start"/> call is a no-op — the underlying provider is only started once.</summary>
    [Fact]
    public void Start_CalledTwice_IsIdempotent()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            using var sut = CreateSut(provider, darkThemeEnabled: false);

            sut.Start();
            sut.Start();

            Assert.Equal(1, provider.StartCallCount);
        });
    }

    /// <summary><see cref="AppThemeService.Dispose"/> unsubscribes — a <see cref="ISystemThemeProvider.Changed"/> raised afterwards must not throw or mutate <see cref="AppThemeService.Current"/>.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromSystemThemeProvider()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources();
            var provider = new FakeSystemThemeProvider(new SystemThemeSnapshot(AppsUseLightTheme: true, SystemUsesLightTheme: true));
            using var sut = CreateSut(provider, darkThemeEnabled: true);
            sut.Start();

            sut.Dispose();

            // A Changed event arriving after Dispose must not throw and must not still mutate Current.
            provider.RaiseChanged(new SystemThemeSnapshot(AppsUseLightTheme: false, SystemUsesLightTheme: false));
            Assert.Equal(AppTheme.Light, sut.Current);
        });
    }

    /// <summary>Invokes <see cref="AppThemeService"/>'s private <c>OnStaticPropertyChanged</c> handler directly — the real <see cref="SystemParameters.StaticPropertyChanged"/> event has no public raise mechanism a test could use instead.</summary>
    private static void InvokeOnStaticPropertyChanged(AppThemeService sut, string propertyName) =>
        typeof(AppThemeService).GetMethod("OnStaticPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sut, [null, new PropertyChangedEventArgs(propertyName)]);

    private static AppThemeService CreateSut(FakeSystemThemeProvider provider, bool darkThemeEnabled, bool highContrast = false)
        => new(
            provider,
            defer: action =>
            {
                action();
                return true;
            },
            highContrastProvider: () => highContrast,
            messageFontSizeProvider: () => 12,
            darkThemeEnabled: darkThemeEnabled);

    /// <summary>
    /// A <c>defer</c> that always successfully "queues" (records) the callback into <paramref name="deferred"/>
    /// and reports success — the <c>Func&lt;Action, bool&gt;</c> shape L-3 (6.5 review round) added to the
    /// real <c>AppThemeService.DeferToUiThread</c> so a caller can tell a queue failure apart from a queue
    /// success.
    /// </summary>
    private static Func<Action, bool> AddAndReport(List<Action> deferred) =>
        action =>
        {
            deferred.Add(action);
            return true;
        };

    /// <summary>
    /// Swaps <see cref="Application.Current"/>'s <see cref="Application.Resources"/> for an empty
    /// dictionary for the duration of a test, then restores whatever <see cref="WpfApplicationHost"/>'s
    /// shared instance had — the same isolation pattern <c>PanelCustomizeWindowClosingTests</c>' own
    /// harness uses, needed here because <see cref="AppThemeService"/> mutates
    /// <see cref="ResourceDictionary.MergedDictionaries"/> directly on whatever <see cref="Application.Resources"/>
    /// is current, and this test class must not leak an extra "Theme.*" dictionary into every later test
    /// sharing the one process-wide <see cref="Application"/>.
    /// </summary>
    private sealed class IsolatedAppResources : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary _original;

        public IsolatedAppResources()
        {
            _app = WpfApplicationHost.EnsureApplication();
            _original = _app.Resources;
            _app.Resources = new ResourceDictionary();
        }

        public void Dispose() => _app.Resources = _original;
    }

    /// <summary>Minimal, fully controllable <see cref="ISystemThemeProvider"/> fake — no native window.</summary>
    private sealed class FakeSystemThemeProvider(SystemThemeSnapshot initial) : ISystemThemeProvider
    {
        public event EventHandler<SystemThemeSnapshot>? Changed;

        public SystemThemeSnapshot Current { get; private set; } = initial;

        public bool IsListening { get; private set; }

        public int StartCallCount { get; private set; }

        public void Start()
        {
            StartCallCount++;
            IsListening = true;
        }

        public void RaiseChanged(SystemThemeSnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, snapshot);
        }

        public void Dispose()
        {
            IsListening = false;
            Changed = null;
        }
    }
}
