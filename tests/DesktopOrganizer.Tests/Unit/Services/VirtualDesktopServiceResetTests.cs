using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="VirtualDesktopService.Reset"/> (ADR-034): after an Explorer restart the cached
/// <c>IVirtualDesktopManager</c> (or a cached "unsupported" verdict) must be dropped so the next call
/// activates a fresh one. The COM activation itself is replaced by a counting fake through the internal
/// test constructor; every call runs on an STA thread, matching the service's documented contract.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class VirtualDesktopServiceResetTests
{
    private readonly Mock<INativeApi> _nativeApi = new();
    private int _activations;

    /// <summary>Activation is cached — including the "unsupported" answer — until Reset.</summary>
    [Fact]
    public void Activation_IsCached_UntilReset() => StaTestRunner.Run(() =>
    {
        var sut = CreateSut();

        Assert.False(sut.IsSupported);
        Assert.False(sut.IsSupported);
        Assert.Equal(1, _activations);
    });

    /// <summary>Reset re-arms lazy activation: the next call activates again (against the new Explorer).</summary>
    [Fact]
    public void Reset_ReinitializesOnNextCall() => StaTestRunner.Run(() =>
    {
        var sut = CreateSut();
        _ = sut.IsSupported;

        sut.Reset();
        _ = sut.IsSupported;

        Assert.Equal(2, _activations);
    });

    /// <summary>Reset itself never activates anything and is idempotent.</summary>
    [Fact]
    public void Reset_DoesNotActivate_AndIsIdempotent() => StaTestRunner.Run(() =>
    {
        var sut = CreateSut();

        sut.Reset();
        sut.Reset();

        Assert.Equal(0, _activations);
    });

    /// <summary>After Dispose, Reset is a no-op: the service stays inert instead of resurrecting COM.</summary>
    [Fact]
    public void Reset_AfterDispose_DoesNotReactivate() => StaTestRunner.Run(() =>
    {
        var sut = CreateSut();
        _ = sut.IsSupported;
        sut.Dispose();

        sut.Reset();

        Assert.False(sut.IsSupported);
        Assert.Equal(1, _activations);
    });

    /// <summary>A throwing activation is still "unsupported", and Reset lets it be retried.</summary>
    [Fact]
    public void ThrowingActivation_IsRetriedAfterReset() => StaTestRunner.Run(() =>
    {
        var sut = new VirtualDesktopService(_nativeApi.Object, () =>
        {
            _activations++;
            throw new InvalidOperationException("explorer is gone");
        });

        Assert.False(sut.IsSupported);
        sut.Reset();
        Assert.False(sut.IsSupported);

        Assert.Equal(2, _activations);
    });

    /// <summary>
    /// MINOR 9 (release-polish 1.0 review): right after a native shell context menu the foreground window is this
    /// process's own never-shown helper window. It is never asked for its virtual desktop (it has no meaningful
    /// one — at best untracked, at worst stale): "current desktop" is unknown and moving a panel degrades to
    /// <see langword="false"/> without even touching the shell.
    /// </summary>
    [Fact]
    public void TryMoveWindowToCurrentDesktop_ForegroundIsOwnHelperWindow_DegradesWithoutConsultingShell() => StaTestRunner.Run(() =>
    {
        var helper = new IntPtr(0x5150);
        _nativeApi.Setup(n => n.GetForegroundWindow()).Returns(helper);
        _nativeApi.Setup(n => n.IsOwnHelperWindow(helper)).Returns(true);
        var sut = CreateSut();

        Assert.False(sut.TryMoveWindowToCurrentDesktop(new IntPtr(0x1234)));
        Assert.Equal(0, _activations);
    });

    /// <summary>
    /// Control for the test above: any OTHER foreground window is consulted as before.
    /// </summary>
    [Fact]
    public void TryMoveWindowToCurrentDesktop_ForegroundIsOtherWindow_ConsultsShell() => StaTestRunner.Run(() =>
    {
        var other = new IntPtr(0x6160);
        _nativeApi.Setup(n => n.GetForegroundWindow()).Returns(other);
        _nativeApi.Setup(n => n.IsOwnHelperWindow(other)).Returns(false);
        var sut = CreateSut();

        Assert.False(sut.TryMoveWindowToCurrentDesktop(new IntPtr(0x1234))); // unsupported fake
        Assert.Equal(1, _activations);
    });

    private VirtualDesktopService CreateSut() => new(_nativeApi.Object, () =>
    {
        _activations++;
        return null; // "CLSID not registered" — the unsupported answer
    });
}
