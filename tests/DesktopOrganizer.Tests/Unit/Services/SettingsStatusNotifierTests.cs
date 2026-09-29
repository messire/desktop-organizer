using System.ComponentModel;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="SettingsStatusNotifier"/> (design-system-1x.md §2.8, ADR-047 D-9, T-10).
/// </summary>
/// <remarks>
/// Uses <see cref="FakeDebounceTimer"/> (same shape as <c>SaveSchedulerTests</c>' own fake) instead of a
/// real <see cref="System.Windows.Threading.DispatcherTimer"/>, so the auto-hide delay is deterministic.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SettingsStatusNotifierTests
{
    /// <summary><see cref="IStatusNotifier.Show"/> sets <see cref="IStatusNotifier.Current"/> and raises <see cref="INotifyPropertyChanged.PropertyChanged"/>.</summary>
    [Fact]
    public void Show_SetsCurrent_AndRaisesPropertyChanged()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.Show(StatusSeverity.Success, "Moved to page \"Work\".", "Panel moved");

        Assert.NotNull(sut.Current);
        Assert.Equal(StatusSeverity.Success, sut.Current!.Severity);
        Assert.Equal("Moved to page \"Work\".", sut.Current.Message);
        Assert.Equal("Panel moved", sut.Current.Title);
        Assert.Contains(nameof(IStatusNotifier.Current), raised);
    }

    /// <summary><see cref="StatusSeverity.Success"/> arms the auto-hide timer.</summary>
    [Fact]
    public void Show_Success_ArmsAutoHideTimer()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);

        sut.Show(StatusSeverity.Success, "Done");

        Assert.Equal(1, timer.StartCount);
    }

    /// <summary><see cref="StatusSeverity.Informational"/> also arms the auto-hide timer.</summary>
    [Fact]
    public void Show_Informational_ArmsAutoHideTimer()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);

        sut.Show(StatusSeverity.Informational, "FYI");

        Assert.Equal(1, timer.StartCount);
    }

    /// <summary><see cref="StatusSeverity.Caution"/>/<see cref="StatusSeverity.Critical"/> never auto-hide — they stay until explicitly dismissed.</summary>
    /// <param name="severity">The non-auto-hiding severity under test.</param>
    [Theory]
    [InlineData(StatusSeverity.Caution)]
    [InlineData(StatusSeverity.Critical)]
    public void Show_CautionOrCritical_DoesNotArmAutoHideTimer(StatusSeverity severity)
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);

        sut.Show(severity, "Something went wrong");

        Assert.Equal(0, timer.StartCount);
    }

    /// <summary>The auto-hide timer firing dismisses <see cref="IStatusNotifier.Current"/> back to <see langword="null"/>.</summary>
    [Fact]
    public void AutoHideTimerFires_ClearsCurrent()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);
        sut.Show(StatusSeverity.Success, "Done");

        timer.Fire();

        Assert.Null(sut.Current);
    }

    /// <summary>A second <see cref="IStatusNotifier.Show"/> call replaces <see cref="IStatusNotifier.Current"/> and restarts the auto-hide window.</summary>
    [Fact]
    public void Show_CalledAgain_ReplacesCurrent_AndRestartsAutoHideWindow()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);
        sut.Show(StatusSeverity.Success, "First");

        sut.Show(StatusSeverity.Success, "Second");

        Assert.Equal("Second", sut.Current!.Message);
        Assert.Equal(2, timer.StopCount); // every Show() first cancels whatever auto-hide was pending
        Assert.Equal(2, timer.StartCount);
    }

    /// <summary><see cref="IStatusNotifier.Dismiss"/> clears <see cref="IStatusNotifier.Current"/> and stops the timer.</summary>
    [Fact]
    public void Dismiss_ClearsCurrent_AndStopsTimer()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);
        sut.Show(StatusSeverity.Success, "Done");

        sut.Dismiss();

        Assert.Null(sut.Current);
        Assert.Equal(2, timer.StopCount); // once from Show()'s own unconditional Stop(), once from Dismiss()
    }

    /// <summary><see cref="IStatusNotifier.Dismiss"/> on an already-empty notifier is a safe no-op.</summary>
    [Fact]
    public void Dismiss_NothingShowing_DoesNotThrow_AndDoesNotRaisePropertyChanged()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);
        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        var exception = Record.Exception(sut.Dismiss);

        Assert.Null(exception);
        Assert.False(raised);
    }

    /// <summary><see cref="IStatusNotifier.DismissCommand"/> is bound to the same <see cref="IStatusNotifier.Dismiss"/> behavior — the InfoBar's own close ("×") button.</summary>
    [Fact]
    public void DismissCommand_Execute_ClearsCurrent()
    {
        var timer = new FakeDebounceTimer();
        var sut = new SettingsStatusNotifier(timer);
        sut.Show(StatusSeverity.Caution, "Careful");

        sut.DismissCommand.Execute(null);

        Assert.Null(sut.Current);
    }

    /// <summary>Minimal <see cref="IDebounceTimer"/> test double — same shape as <c>SaveSchedulerTests.FakeDebounceTimer</c>.</summary>
    private sealed class FakeDebounceTimer : IDebounceTimer
    {
        private Action? _callback;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public void Start(TimeSpan window, Action callback)
        {
            StartCount++;
            _callback = callback;
        }

        public void Stop()
        {
            StopCount++;
            _callback = null;
        }

        /// <summary>Simulates the debounce window elapsing: invokes and clears the armed callback, if any.</summary>
        public void Fire()
        {
            var callback = _callback;
            _callback = null;
            callback?.Invoke();
        }
    }
}
