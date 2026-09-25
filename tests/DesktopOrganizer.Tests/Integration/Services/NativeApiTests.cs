using System.IO;
using System.Runtime.InteropServices;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Models;
using DesktopOrganizer.Tests.Resources;
using static DesktopOrganizer.Services.Enums.FileFlag;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// Integration tests for <see cref="NativeApi"/>.
/// </summary>
[Trait(IntegrationTests, nameof(Services))]
public class NativeApiTests
{
    private const FileAttribute Attributes = FileAttribute.Normal;
    private readonly NativeApi _nativeApi = new ();

    /// <summary>
    /// Test cases for <see cref="GetFileInfo_ReturnsExpectedResult"/>: an existing file path (expected
    /// to yield a valid icon handle) and a non-existent one (expected to yield <see cref="IntPtr.Zero"/>).
    /// </summary>
    public static IEnumerable<object[]> FileInfoTestData => new List<object[]>
    {
        new object[] { Path.GetFullPath("Resources/TestFile.txt"), true, Icon | UseFileAttributes },
        new object[] { Path.GetFullPath("Resources/NonExistentFile.txt"), false, Icon },
    };

    /// <summary>
    /// Ensures that GetFileInfo returns the expected result for valid and invalid file paths.
    /// </summary>
    /// <param name="filePath">The path passed to <c>GetFileInfo</c>.</param>
    /// <param name="shouldReturnValidHandle">Whether a non-zero icon handle is expected for this path.</param>
    /// <param name="flags">The <see cref="FileFlag"/> combination passed to <c>GetFileInfo</c>.</param>
    [Theory]
    [MemberData(nameof(FileInfoTestData))]
    public void GetFileInfo_ReturnsExpectedResult(string filePath, bool shouldReturnValidHandle, FileFlag flags)
    {
        // Arrange
        var fileInfo = default(ShellFileInfo);

        // Act
        var result = _nativeApi.GetFileInfo(filePath, (uint)Attributes, ref fileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), (uint)flags);

        // Assert
        if (shouldReturnValidHandle)
        {
            Assert.NotEqual(IntPtr.Zero, result);
            _nativeApi.DestroyIcon(result); // Cleanup
        }
        else
        {
            Assert.Equal(IntPtr.Zero, result);
        }
    }

    /// <summary>
    /// Ensures that DestroyIcon behaves correctly for valid and invalid handles.
    /// </summary>
    [Fact]
    public void DestroyIcon_BehavesAsExpected()
    {
        // Arrange
        var validHandle = User32.LoadIcon(IntPtr.Zero, User32.IdiApplication);
        var invalidHandle = IntPtr.Zero;

        // Act & Assert for valid handle
        Assert.NotEqual(IntPtr.Zero, validHandle);
        Assert.True(_nativeApi.DestroyIcon(validHandle));

        // Act & Assert for invalid handle
        Assert.False(_nativeApi.DestroyIcon(invalidHandle));
    }

    // ── Native panel sizing primitives (ADR-022 / Phase 2, C1) ──────────────────────────────────

    /// <summary>
    /// <c>WriteRect</c> → <c>ReadRect</c> is a lossless round-trip over raw <c>lParam</c> memory.
    /// Negative coordinates are included on purpose: a monitor left of / above the primary one
    /// produces them, and a sign error here would corrupt every sizing message on such a setup.
    /// </summary>
    /// <param name="left">The left coordinate to round-trip.</param>
    /// <param name="top">The top coordinate to round-trip.</param>
    /// <param name="right">The right coordinate to round-trip.</param>
    /// <param name="bottom">The bottom coordinate to round-trip.</param>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(10, 20, 310, 420)]
    [InlineData(-1920, -100, -1620, 200)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue)]
    public void WriteRect_ThenReadRect_RoundTripsExactly(int left, int top, int right, int bottom)
    {
        // Arrange — four ints is exactly the native RECT layout the sizing loop hands us.
        var lParam = Marshal.AllocHGlobal(4 * sizeof(int));

        try
        {
            var rect = new NativeRectPx(left, top, right, bottom);

            // Act
            _nativeApi.WriteRect(lParam, rect);
            var read = _nativeApi.ReadRect(lParam);

            // Assert
            Assert.Equal(rect, read);
        }
        finally
        {
            Marshal.FreeHGlobal(lParam);
        }
    }

    /// <summary>
    /// <c>WM_SIZING</c> is answered by modifying the proposed rectangle in place, so a second
    /// <c>WriteRect</c> into the same buffer must fully replace the previous contents.
    /// </summary>
    [Fact]
    public void WriteRect_OverwritesPreviousContentInPlace()
    {
        // Arrange
        var lParam = Marshal.AllocHGlobal(4 * sizeof(int));

        try
        {
            _nativeApi.WriteRect(lParam, new NativeRectPx(1, 2, 3, 4));

            // Act
            _nativeApi.WriteRect(lParam, new NativeRectPx(100, 200, 500, 800));

            // Assert
            Assert.Equal(new NativeRectPx(100, 200, 500, 800), _nativeApi.ReadRect(lParam));
        }
        finally
        {
            Marshal.FreeHGlobal(lParam);
        }
    }

    /// <summary>
    /// The rectangle read back from <c>lParam</c> reports the extent the sizing math works with.
    /// An inverted rectangle is deliberately NOT normalized — it is surfaced as negative extent.
    /// </summary>
    /// <param name="left">The left coordinate to write.</param>
    /// <param name="top">The top coordinate to write.</param>
    /// <param name="right">The right coordinate to write.</param>
    /// <param name="bottom">The bottom coordinate to write.</param>
    /// <param name="width">The expected <c>Width</c> computed from the stored rectangle.</param>
    /// <param name="height">The expected <c>Height</c> computed from the stored rectangle.</param>
    [Theory]
    [InlineData(10, 20, 310, 420, 300, 400)]
    [InlineData(-50, -50, 50, 50, 100, 100)]
    [InlineData(100, 100, 40, 90, -60, -10)]
    public void ReadRect_ReportsWidthAndHeightOfStoredRect(int left, int top, int right, int bottom, int width, int height)
    {
        // Arrange
        var lParam = Marshal.AllocHGlobal(4 * sizeof(int));

        try
        {
            _nativeApi.WriteRect(lParam, new NativeRectPx(left, top, right, bottom));

            // Act
            var read = _nativeApi.ReadRect(lParam);

            // Assert
            Assert.Equal(width, read.Width);
            Assert.Equal(height, read.Height);
        }
        finally
        {
            Marshal.FreeHGlobal(lParam);
        }
    }

    /// <summary>
    /// An invalid handle must be reported, never thrown: <c>GetWindowRect</c> runs inside a window
    /// procedure where an exception would escape into the native sizing loop.
    /// </summary>
    [Fact]
    public void GetWindowRect_WithInvalidHandle_ReturnsFalseAndDefaultRect()
    {
        // Act
        var ok = _nativeApi.GetWindowRect(IntPtr.Zero, out var rect);

        // Assert
        Assert.False(ok);
        Assert.Equal(default, rect);
    }

    /// <summary>
    /// For a real window the call succeeds and reports the rectangle it was created with
    /// (screen coordinates, physical pixels).
    /// </summary>
    [Fact]
    public void GetWindowRect_ForRealWindow_ReturnsCreationRect()
    {
        // Arrange — a hidden top-level window is the cheapest real HWND available headless.
        var window = CreateHiddenWindow(out var keepAlive);

        try
        {
            Assert.True(window.IsValid, $"Window creation failed, Win32 error {window.LastError}.");

            // Act
            var ok = _nativeApi.GetWindowRect(window.Handle, out var rect);

            // Assert — created at (0,0) with zero extent (see NativeApi.CreateMessageWindow).
            Assert.True(ok);
            Assert.Equal(new NativeRectPx(0, 0, 0, 0), rect);
        }
        finally
        {
            _nativeApi.DestroyMessageWindow(window);
            GC.KeepAlive(keepAlive);
        }
    }

    /// <summary>
    /// An unusable handle falls back to the system DPI scale instead of returning 0 or throwing.
    /// </summary>
    [Fact]
    public void GetDpiScaleForWindow_WithInvalidHandle_FallsBackToSystemScale()
    {
        // Act
        var scale = _nativeApi.GetDpiScaleForWindow(IntPtr.Zero);

        // Assert
        Assert.Equal(_nativeApi.GetSystemDpiScale(), scale);
        Assert.True(scale > 0);
    }

    /// <summary>
    /// For a real window a plausible positive scale is returned (never 0, never an exception).
    /// </summary>
    [Fact]
    public void GetDpiScaleForWindow_ForRealWindow_ReturnsPositiveScale()
    {
        // Arrange
        var window = CreateHiddenWindow(out var keepAlive);

        try
        {
            Assert.True(window.IsValid, $"Window creation failed, Win32 error {window.LastError}.");

            // Act
            var scale = _nativeApi.GetDpiScaleForWindow(window.Handle);

            // Assert — 0.5..8.0 spans every scale Windows offers (48..768 DPI), so a value outside
            // it means the quotient or the fallback is wrong rather than the machine being unusual.
            Assert.InRange(scale, 0.5, 8.0);
        }
        finally
        {
            _nativeApi.DestroyMessageWindow(window);
            GC.KeepAlive(keepAlive);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a hidden top-level window with a unique class name. The window procedure must stay
    /// rooted for the window's lifetime — hence the <paramref name="windowProc"/> out parameter.
    /// </summary>
    private NativeMessageWindow CreateHiddenWindow(out NativeWindowProc windowProc)
    {
        var proc = new NativeWindowProc(_nativeApi.DefaultWindowProc);
        windowProc = proc;
        return _nativeApi.CreateMessageWindow($"DoTestSizingWnd_{Guid.NewGuid():N}", proc);
    }
}
