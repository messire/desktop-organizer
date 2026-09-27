using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
/// <remarks>
/// This class uses native methods that are not CLS-compliant.
/// </remarks>
internal class IconHandler(INativeApi nativeApi, IDiagnosticsLog diagnosticsLog) : IIconHandler
{
    /// <summary><c>SHIL_LARGE</c>: 32 px at 100 % system scale.</summary>
    internal const int ShilLarge = 0;

    /// <summary><c>SHIL_SMALL</c>: 16 px at 100 % system scale.</summary>
    internal const int ShilSmall = 1;

    /// <summary><c>SHIL_EXTRALARGE</c>: 48 px at 100 % system scale.</summary>
    internal const int ShilExtraLarge = 2;

    /// <summary><c>SHIL_JUMBO</c>: 256 px.</summary>
    internal const int ShilJumbo = 4;

    // SHGFI_SYSICONINDEX alone — deliberately WITHOUT SHGFI_USEFILEATTRIBUTES (the shell opens the item,
    // so an .exe/.lnk/custom-icon folder gets its own index) and without SHGFI_ICON (no HICON to leak).
    private const FileFlag SystemIndexFlags = FileFlag.SysIconIndex;

    /// <inheritdoc />
    public IntPtr GetIconHandle(string filePath, ref ShellFileInfo fileInfo, FileAttribute fileAttributes, FileFlag iconFlags)
    {
        var iconHandle = nativeApi.GetFileInfo(filePath, (uint)fileAttributes, ref fileInfo, (uint)Marshal.SizeOf(fileInfo), (uint)iconFlags);
        if (iconHandle == IntPtr.Zero && diagnosticsLog.IsEnabled)
        {
            // Privacy invariant (docs/Refactoring/Testing/diagnostics-log-events.md): never the
            // file path itself — only its extension (bounded, sanitized) and the closed-set
            // attribute flag actually passed to the shell.
            diagnosticsLog.Write(
                IconDiagnostics.Category,
                "IconHandleFailed",
                null,
                new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath)),
                new KeyValuePair<string, object?>("attributes", fileAttributes.ToString()));
        }

        return iconHandle;
    }

    /// <inheritdoc />
    public void DestroyIconHandle(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero || nativeApi.DestroyIcon(iconHandle)) return;

        if (diagnosticsLog.IsEnabled)
        {
            diagnosticsLog.Write(IconDiagnostics.Category, "IconDestroyFailed", null);
        }
    }

    // ── High-resolution panel item icons ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IntPtr GetShellItemBitmap(string filePath, int pixelSize)
    {
        var hr = nativeApi.GetShellItemImage(filePath, pixelSize, out var bitmapHandle);
        if (hr >= 0 && bitmapHandle != IntPtr.Zero) return bitmapHandle;

        // Contract of INativeApi.GetShellItemImage: no handle is owned by the caller unless it returned
        // success — but a "success with a null handle" is still a failure here, and a failure code with a
        // non-null handle is released rather than leaked.
        if (bitmapHandle != IntPtr.Zero) nativeApi.DeleteBitmap(bitmapHandle);

        if (diagnosticsLog.IsEnabled)
        {
            // Privacy invariant: extension + HRESULT (a closed, numeric failure code) only — never the path.
            diagnosticsLog.Write(
                IconDiagnostics.Category,
                "ShellItemImageFailed",
                null,
                new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath)),
                new KeyValuePair<string, object?>("hresult", FormatHResult(hr)));
        }

        return IntPtr.Zero;
    }

    /// <inheritdoc />
    public bool TryReadBitmapPixels(IntPtr bitmapHandle, out NativeBitmapPixels pixels)
    {
        if (bitmapHandle != IntPtr.Zero && nativeApi.TryReadBitmapPixels(bitmapHandle, out pixels)) return true;

        pixels = default;
        if (diagnosticsLog.IsEnabled)
        {
            diagnosticsLog.Write(IconDiagnostics.Category, "IconBitmapReadFailed", null);
        }

        return false;
    }

    /// <inheritdoc />
    public void DestroyBitmapHandle(IntPtr bitmapHandle)
    {
        if (bitmapHandle == IntPtr.Zero || nativeApi.DeleteBitmap(bitmapHandle)) return;

        if (diagnosticsLog.IsEnabled)
        {
            diagnosticsLog.Write(IconDiagnostics.Category, "IconBitmapDestroyFailed", null);
        }
    }

    /// <inheritdoc />
    public IntPtr GetSystemImageListIconHandle(string filePath, int pixelSize)
    {
        var fileInfo = default(ShellFileInfo);

        // The return value of SHGFI_SYSICONINDEX is the system image list handle (non-zero on success);
        // the index itself lands in fileInfo.IconIndex, where 0 is a perfectly valid index.
        var imageListHandle = nativeApi.GetFileInfo(filePath, 0, ref fileInfo, (uint)Marshal.SizeOf(fileInfo), (uint)SystemIndexFlags);
        if (imageListHandle == IntPtr.Zero)
        {
            WriteStageFailure("SystemIconIndexFailed", filePath);
            return IntPtr.Zero;
        }

        var imageList = SystemImageListFor(pixelSize);
        var iconHandle = nativeApi.GetSystemImageListIcon(imageList, fileInfo.IconIndex);
        if (iconHandle == IntPtr.Zero)
        {
            WriteStageFailure("ImageListIconFailed", filePath, new KeyValuePair<string, object?>("imageList", imageList));
        }

        return iconHandle;
    }

    /// <summary>
    /// Picks the system image list for a requested pixel size: the smallest list whose nominal size covers
    /// the request (16 → <see cref="ShilSmall"/>, 32 → <see cref="ShilLarge"/>), <see cref="ShilExtraLarge"/>
    /// (48) up to 96 px, and <see cref="ShilJumbo"/> (256) only above that.
    /// </summary>
    /// <remarks>
    /// Why not jumbo already from 49 px: for a file type that ships no 256 px frame, the jumbo list holds its
    /// 48 px icon centred in an otherwise empty 256 px canvas — shown in a 72 px slot that is a tiny icon in
    /// a big empty box, clearly worse than a mildly upscaled 48 px one. This is only the fallback stage; the
    /// primary <c>IShellItemImageFactory</c> stage scales properly at every size.
    /// </remarks>
    /// <param name="pixelSize">Requested square size in physical pixels.</param>
    /// <returns>One of the <c>SHIL_*</c> constants.</returns>
    internal static int SystemImageListFor(int pixelSize) => pixelSize switch
    {
        <= 16 => ShilSmall,
        <= 32 => ShilLarge,
        <= 96 => ShilExtraLarge,
        _ => ShilJumbo,
    };

    private static string FormatHResult(int hr) => $"0x{hr:X8}";

    private void WriteStageFailure(string name, string filePath, params KeyValuePair<string, object?>[] extra)
    {
        if (!diagnosticsLog.IsEnabled) return;

        // Privacy invariant: extension + closed-set/numeric values only — never the path.
        var payload = new KeyValuePair<string, object?>[extra.Length + 1];
        payload[0] = new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath));
        extra.CopyTo(payload, 1);
        diagnosticsLog.Write(IconDiagnostics.Category, name, null, payload);
    }
}
