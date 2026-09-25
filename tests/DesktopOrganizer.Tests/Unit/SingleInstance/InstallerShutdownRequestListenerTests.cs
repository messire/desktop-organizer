using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;
using Moq;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>
/// Unit tests for <see cref="InstallerShutdownRequestListener"/> against real, uniquely-named
/// <see cref="EventWaitHandle"/> kernel objects — no GUI involved, so these run under <c>dotnet test</c>
/// like any other unit test. Each test uses a unique <see cref="Guid"/>-suffixed name so parallel test
/// runs never collide, mirroring <c>MutexGateTests</c>' own convention.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class InstallerShutdownRequestListenerTests
{
    [Fact]
    public void Start_SignaledFromAnotherHandle_RaisesShutdownRequested()
    {
        var name = UniqueName();
        var diagnostics = new Mock<IDiagnosticsLog>().Object;
        using var raised = new ManualResetEventSlim(false);

        using var listener = new InstallerShutdownRequestListener(name, diagnostics);
        listener.ShutdownRequested += (_, _) => raised.Set();
        listener.Start();

        // Simulates the installer's side: a separate handle opened by the same well-known name, exactly
        // as installer/DesktopOrganizer.iss's OpenEventW + SetEvent does.
        using var signalHandle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        signalHandle.Set();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "ShutdownRequested was not raised within the timeout.");
    }

    /// <summary>
    /// R-6: <see cref="EventResetMode.AutoReset"/> releases exactly one waiter per signal regardless of
    /// how many registrations exist, so asserting "raised exactly once" (the previous version of this
    /// test) would pass even if the second <see cref="InstallerShutdownRequestListener.Start"/> call
    /// incorrectly created a SECOND, independent registration — proving nothing about a leak. This
    /// version instead proves the absence of a leak directly: <see cref="InstallerShutdownRequestListener.Dispose"/>
    /// only ever unregisters the single wait its own <c>_registeredWait</c> field tracks, so if the
    /// second <c>Start()</c> call had (incorrectly) replaced that field with a second registration, the
    /// FIRST one would leak past <c>Dispose()</c> and still be listening on this exact kernel object —
    /// and would raise <see cref="InstallerShutdownRequestListener.ShutdownRequested"/> right here.
    /// </summary>
    [Fact]
    public void Start_CalledTwice_DoesNotLeakADuplicateRegistrationPastDispose()
    {
        var name = UniqueName();
        var diagnostics = new Mock<IDiagnosticsLog>().Object;
        using var raised = new ManualResetEventSlim(false);

        // Opened before Start()/Dispose() so the underlying kernel object outlives both — see
        // Dispose_ThenSignaled_DoesNotRaiseShutdownRequested's remarks for why this ordering matters.
        using var signalHandle = new EventWaitHandle(false, EventResetMode.AutoReset, name);

        var listener = new InstallerShutdownRequestListener(name, diagnostics);
        listener.ShutdownRequested += (_, _) => raised.Set();

        listener.Start();
        listener.Start(); // Second call must be a no-op — never throws, never double-registers.
        listener.Dispose();

        signalHandle.Set();

        Assert.False(
            raised.Wait(TimeSpan.FromSeconds(1)),
            "ShutdownRequested fired after Dispose() — a registration leaked from the duplicate Start() call.");
    }

    /// <summary>
    /// R-5: <paramref name="signalHandle"/>'s underlying kernel object must be opened BEFORE
    /// <see cref="InstallerShutdownRequestListener.Start"/>/<see cref="InstallerShutdownRequestListener.Dispose"/>
    /// run, not after. The listener's own <see cref="EventWaitHandle"/> is the only handle keeping the
    /// named kernel object alive until this test opens its own — opening this test's handle only AFTER
    /// <c>Dispose()</c> has already closed the listener's last handle would let Windows destroy that
    /// kernel object first, so the later <c>new EventWaitHandle(name)</c> would silently create a
    /// brand-new, never-listened-to object instead of reopening the real one — the assertion below would
    /// then trivially (and meaninglessly) pass.
    /// </summary>
    [Fact]
    public void Dispose_ThenSignaled_DoesNotRaiseShutdownRequested()
    {
        var name = UniqueName();
        var diagnostics = new Mock<IDiagnosticsLog>().Object;
        using var raised = new ManualResetEventSlim(false);

        using var signalHandle = new EventWaitHandle(false, EventResetMode.AutoReset, name);

        var listener = new InstallerShutdownRequestListener(name, diagnostics);
        listener.ShutdownRequested += (_, _) => raised.Set();
        listener.Start();
        listener.Dispose();

        signalHandle.Set();

        Assert.False(raised.Wait(TimeSpan.FromSeconds(1)), "ShutdownRequested fired after Dispose().");
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var listener = new InstallerShutdownRequestListener(UniqueName(), new Mock<IDiagnosticsLog>().Object);
        listener.Start();

        listener.Dispose();
        listener.Dispose();
    }

    /// <summary>O-7: <see cref="IShutdownRequestListener.Start"/> after Dispose() must stay a no-op.</summary>
    [Fact]
    public void Start_AfterDispose_IsNoOp()
    {
        var name = UniqueName();
        var diagnostics = new Mock<IDiagnosticsLog>().Object;
        using var raised = new ManualResetEventSlim(false);
        using var signalHandle = new EventWaitHandle(false, EventResetMode.AutoReset, name);

        var listener = new InstallerShutdownRequestListener(name, diagnostics);
        listener.ShutdownRequested += (_, _) => raised.Set();
        listener.Dispose(); // Disposed WITHOUT ever calling Start() first.

        var exception = Record.Exception(listener.Start);
        signalHandle.Set();

        Assert.Null(exception);
        Assert.False(raised.Wait(TimeSpan.FromSeconds(1)), "Start() after Dispose() must not begin listening.");
    }

    [Fact]
    public void Start_NeverThrows_EvenWhenTheNamedEventCannotBeCreated()
    {
        // A backslash-qualified name whose prefix is neither "Global\" nor "Local\" tells the Windows
        // object manager to look for an existing NAMESPACE DIRECTORY object with that name — which does
        // not exist — so CreateEvent reliably fails with ERROR_PATH_NOT_FOUND, surfacing as an exception
        // from `new EventWaitHandle(...)` inside Start(). This test asserts that failure never escapes
        // Start() itself (the interface's "never throws" contract) and is instead logged.
        var invalidName = $@"DesktopOrganizer.Tests.NoSuchNamespace\{Guid.NewGuid():N}";
        var diagnosticsMock = new Mock<IDiagnosticsLog>();
        using var listener = new InstallerShutdownRequestListener(invalidName, diagnosticsMock.Object);

        var exception = Record.Exception(listener.Start);

        Assert.Null(exception);
        diagnosticsMock.Verify(
            l => l.Write(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// O-7: a subscriber that throws must be swallowed inside the thread-pool callback (never crash the
    /// process) and logged as <c>InstallerShutdownListenerCallbackFailed</c>. Since <c>Start()</c>
    /// registers with <c>executeOnlyOnce: true</c> (R-4), there is no second signal to wait for here —
    /// this callback only ever runs once per listener lifetime.
    /// </summary>
    [Fact]
    public void OnSignaled_SubscriberThrows_IsSwallowedAndLogged()
    {
        var name = UniqueName();
        var diagnosticsMock = new Mock<IDiagnosticsLog>();
        using var loggedOnce = new ManualResetEventSlim(false);
        diagnosticsMock
            .Setup(l => l.Write(
                "session",
                "InstallerShutdownListenerCallbackFailed",
                null,
                It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback(() => loggedOnce.Set());

        using var listener = new InstallerShutdownRequestListener(name, diagnosticsMock.Object);
        listener.ShutdownRequested += (_, _) =>
            throw new InvalidOperationException("Simulated subscriber failure — must never escape the thread-pool callback.");
        listener.Start();

        using var signalHandle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        signalHandle.Set();

        var message =
            "InstallerShutdownListenerCallbackFailed was not logged within the timeout — either the callback " +
            "didn't run, or the exception escaped uncaught (which would have crashed the test process).";
        Assert.True(loggedOnce.Wait(TimeSpan.FromSeconds(5)), message);
    }

    /// <summary>
    /// O-7: when the named event already exists (<c>out createdNew</c> comes back <see langword="false"/>
    /// from the <see cref="EventWaitHandle"/> constructor inside <c>Start()</c>), R-4 requires a
    /// diagnostics line naming that condition, even though it is not itself fatal.
    /// </summary>
    [Fact]
    public void Start_EventAlreadyExists_LogsEventPreexisted()
    {
        var name = UniqueName();

        // Pre-creates the named kernel object before the listener ever calls `new EventWaitHandle(...)`
        // itself — this is exactly what makes that constructor's own `out createdNew` come back False.
        using var preexisting = new EventWaitHandle(false, EventResetMode.AutoReset, name);

        var diagnosticsMock = new Mock<IDiagnosticsLog>();
        using var listener = new InstallerShutdownRequestListener(name, diagnosticsMock.Object);

        listener.Start();

        diagnosticsMock.Verify(
            l => l.Write(
                "session",
                "InstallerShutdownListenerEventPreexisted",
                null,
                It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    private static string UniqueName() => $"DesktopOrganizer.Tests.Shutdown.{Guid.NewGuid():N}";
}
