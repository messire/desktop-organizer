using System.Runtime.InteropServices;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// Integration tests for the <c>WINDOWPOS</c> in-place accessors of <see cref="NativeApi"/> used by the
/// <c>WM_WINDOWPOSCHANGING</c> desktop-band filter (ADR-034 amendment 2026-10-09), against real unmanaged
/// memory laid out like the native structure.
/// </summary>
[Trait(IntegrationTests, nameof(Services))]
public class NativeApiWindowPosTests
{
    private readonly NativeApi _nativeApi = new ();

    /// <summary>Reads exactly <c>hwndInsertAfter</c> and <c>flags</c> from a native <c>WINDOWPOS</c>.</summary>
    [Fact]
    public void TryReadWindowPosZOrder_ReadsInsertAfterAndFlags()
    {
        var memory = Allocate(new NativeWindowPos { Hwnd = 11, HwndInsertAfter = 22, X = 1, Y = 2, Cx = 3, Cy = 4, Flags = 0x0247 });
        try
        {
            var ok = _nativeApi.TryReadWindowPosZOrder(memory, out var insertAfter, out var flags);

            Assert.True(ok);
            Assert.Equal(new IntPtr(22), insertAfter);
            Assert.Equal(0x0247u, flags);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Writes only <c>hwndInsertAfter</c> and <c>flags</c>; handle, position and size are untouched.</summary>
    [Fact]
    public void WriteWindowPosZOrder_ChangesOnlyZOrderFields()
    {
        var memory = Allocate(new NativeWindowPos { Hwnd = 11, HwndInsertAfter = 22, X = 1, Y = 2, Cx = 3, Cy = 4, Flags = 0x0004 });
        try
        {
            _nativeApi.WriteWindowPosZOrder(memory, new IntPtr(99), 0x0240);

            var result = Marshal.PtrToStructure<NativeWindowPos>(memory);
            Assert.Equal(11, result.Hwnd);
            Assert.Equal(99, result.HwndInsertAfter);
            Assert.Equal((1, 2, 3, 4), (result.X, result.Y, result.Cx, result.Cy));
            Assert.Equal(0x0240u, result.Flags);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Null pointers and handles are safe no-ops.</summary>
    [Fact]
    public void Accessors_NullInputs_AreSafe()
    {
        Assert.False(_nativeApi.TryReadWindowPosZOrder(IntPtr.Zero, out var insertAfter, out var flags));
        Assert.Equal(IntPtr.Zero, insertAfter);
        Assert.Equal(0u, flags);

        var exception = Record.Exception(() => _nativeApi.WriteWindowPosZOrder(IntPtr.Zero, new IntPtr(1), 1));
        Assert.Null(exception);

        Assert.Equal(0u, _nativeApi.GetWindowProcessId(IntPtr.Zero));
    }

    private static IntPtr Allocate(NativeWindowPos value)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeWindowPos>());
        Marshal.StructureToPtr(value, memory, false);
        return memory;
    }

    /// <summary>Independent copy of the native <c>WINDOWPOS</c> layout (not the product's own declaration).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPos
    {
        public nint Hwnd;
        public nint HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }
}
