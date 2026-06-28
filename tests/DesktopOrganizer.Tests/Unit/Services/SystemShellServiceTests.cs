using System.Windows.Media;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for SystemShellService.
/// INativeApi is mocked — no Windows shell calls are made.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.Services")]
public class SystemShellServiceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal mock of INativeApi that returns safe defaults for
    /// methods not directly relevant to the test under scrutiny.
    /// </summary>
    private static Mock<INativeApi> BuildSafeNativeMock()
    {
        var m = new Mock<INativeApi>();

        // Default: QueryRecycleBin returns empty state so the ctor timer-poll is safe.
        m.Setup(n => n.QueryRecycleBin())
            .Returns(new RecycleBinNativeState(0, 0));

        return m;
    }

    // ── GetSystemIcon: graceful degrade when pidl is zero ────────────────────

    [Fact]
    public void GetSystemIcon_ReturnsNull_WhenPidlIsZero()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();

        // GetKnownFolderPidl returns IntPtr.Zero — item not available on this system
        nativeMock
            .Setup(n => n.GetKnownFolderPidl(It.IsAny<Guid>()))
            .Returns(IntPtr.Zero);

        using var sut = new SystemShellService(nativeMock.Object);

        // Act
        ImageSource? result = sut.GetSystemIcon(SystemIconKind.ThisPc, 32);

        // Assert — service must degrade gracefully, not throw
        Assert.Null(result);
    }

    // ── GetSystemIcon: caching ────────────────────────────────────────────────

    [Fact]
    public void GetSystemIcon_CachesResult_ForSameKindAndSize()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();
        var fakePidl = new IntPtr(0x1234);
        var fakeIcon = new System.Windows.Media.Imaging.BitmapImage();

        nativeMock
            .Setup(n => n.GetKnownFolderPidl(It.IsAny<Guid>()))
            .Returns(fakePidl);

        nativeMock
            .Setup(n => n.GetShellIconByPidl(fakePidl, It.IsAny<int>()))
            .Returns(fakeIcon);

        nativeMock
            .Setup(n => n.FreePidl(fakePidl));

        using var sut = new SystemShellService(nativeMock.Object);

        // Act — call twice with identical parameters
        sut.GetSystemIcon(SystemIconKind.Network, 32);
        sut.GetSystemIcon(SystemIconKind.Network, 32);

        // Assert — native layer called only once (first call populates cache)
        nativeMock.Verify(n => n.GetKnownFolderPidl(It.IsAny<Guid>()), Times.Once);
        nativeMock.Verify(n => n.GetShellIconByPidl(fakePidl, 32), Times.Once);
    }

    // ── GetSystemIcon: invalid / unknown kind must not throw ─────────────────

    [Fact]
    public void GetSystemIcon_InvalidKind_DoesNotThrow()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();
        using var sut = new SystemShellService(nativeMock.Object);
        var invalidKind = (SystemIconKind)999;

        // Act & Assert — must not propagate
        var ex = Record.Exception(() => sut.GetSystemIcon(invalidKind, 32));
        Assert.Null(ex);
    }

    // ── GetRecycleBinState: maps ItemCount=0 → IsEmpty=true ──────────────────

    [Fact]
    public void GetRecycleBinState_ReturnsIsEmptyTrue_WhenItemCountIsZero()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();

        nativeMock
            .Setup(n => n.QueryRecycleBin())
            .Returns(new RecycleBinNativeState(ItemCount: 0, TotalSize: 0));

        using var sut = new SystemShellService(nativeMock.Object);

        // Act
        RecycleBinState state = sut.GetRecycleBinState();

        // Assert
        Assert.True(state.IsEmpty);
        Assert.Equal(0L, state.ItemCount);
    }

    [Fact]
    public void GetRecycleBinState_ReturnsIsEmptyFalse_WhenItemCountIsNonZero()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();

        nativeMock
            .Setup(n => n.QueryRecycleBin())
            .Returns(new RecycleBinNativeState(ItemCount: 3, TotalSize: 1024));

        using var sut = new SystemShellService(nativeMock.Object);

        // Act
        RecycleBinState state = sut.GetRecycleBinState();

        // Assert
        Assert.False(state.IsEmpty);
        Assert.Equal(3L, state.ItemCount);
    }

    // ── ShowContextMenu (right-click): regression for AccessViolation crash ───

    [Fact]
    public void ShowContextMenu_PassesResolvedPidlAndCoordinates_ToNativeApi()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();
        var fakePidl = new IntPtr(0xBEEF);
        var ownerHwnd = new IntPtr(0x42);

        nativeMock
            .Setup(n => n.GetKnownFolderPidl(It.IsAny<Guid>()))
            .Returns(fakePidl);

        using var sut = new SystemShellService(nativeMock.Object);

        // Act — simulate a right-click on a panel icon
        sut.ShowContextMenu(SystemIconKind.RecycleBin, ownerHwnd, screenX: 120, screenY: 240);

        // Assert — the native shell menu received the exact pidl, owner and screen coords
        nativeMock.Verify(
            n => n.ShowShellContextMenu(fakePidl, ownerHwnd, 120, 240),
            Times.Once);

        // And the resolved pidl is always released afterwards (no leak on the menu path)
        nativeMock.Verify(n => n.FreePidl(fakePidl), Times.Once);
    }

    [Fact]
    public void ShowContextMenu_DoesNotThrow_WhenNativeMenuFaults()
    {
        // Arrange — the native layer raises (proxy for the former AccessViolation crash)
        var nativeMock = BuildSafeNativeMock();
        var fakePidl = new IntPtr(0xBEEF);

        nativeMock
            .Setup(n => n.GetKnownFolderPidl(It.IsAny<Guid>()))
            .Returns(fakePidl);

        nativeMock
            .Setup(n => n.ShowShellContextMenu(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws<Exception>();

        using var sut = new SystemShellService(nativeMock.Object);

        // Act & Assert — right-click must never propagate an exception to the UI thread
        var ex = Record.Exception(() => sut.ShowContextMenu(SystemIconKind.RecycleBin, IntPtr.Zero, 0, 0));
        Assert.Null(ex);

        // pidl still released even though the native call faulted
        nativeMock.Verify(n => n.FreePidl(fakePidl), Times.Once);
    }

    [Fact]
    public void ShowContextMenu_DoesNotInvokeNative_WhenPidlUnavailable()
    {
        // Arrange — item not present on this system → pidl is zero
        var nativeMock = BuildSafeNativeMock();
        nativeMock
            .Setup(n => n.GetKnownFolderPidl(It.IsAny<Guid>()))
            .Returns(IntPtr.Zero);

        using var sut = new SystemShellService(nativeMock.Object);

        // Act
        var ex = Record.Exception(() => sut.ShowContextMenu(SystemIconKind.ThisPc, IntPtr.Zero, 10, 10));

        // Assert — no native menu call with a null pidl, no throw
        Assert.Null(ex);
        nativeMock.Verify(
            n => n.ShowShellContextMenu(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    // ── SendToRecycleBin: returns false on native failure, never throws ───────

    [Fact]
    public void SendToRecycleBin_ReturnsFalse_OnNativeFailure()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();

        nativeMock
            .Setup(n => n.SendToRecycleBin(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Throws<Exception>();

        using var sut = new SystemShellService(nativeMock.Object);

        // Act
        bool result = sut.SendToRecycleBin(["C:\\some\\file.txt"], IntPtr.Zero);

        // Assert — false returned, exception not propagated
        Assert.False(result);
    }

    [Fact]
    public void SendToRecycleBin_ReturnsTrue_OnSuccess()
    {
        // Arrange
        var nativeMock = BuildSafeNativeMock();

        nativeMock
            .Setup(n => n.SendToRecycleBin(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Returns(true);

        using var sut = new SystemShellService(nativeMock.Object);

        // Act
        bool result = sut.SendToRecycleBin(["C:\\some\\file.txt"], IntPtr.Zero);

        // Assert
        Assert.True(result);
    }
}
