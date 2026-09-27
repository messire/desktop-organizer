namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Handles native icon operations, including retrieving and destroying icon handles.
/// </summary>
internal interface IIconHandler
{
    /// <summary>
    /// Retrieves the icon handle for the specified file path using Shell API.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="fileInfo">The structure to receive file information.</param>
    /// <param name="fileAttributes">File attributes.</param>
    /// <param name="iconFlags">The icon flags.</param>
    /// <returns>A handle to the icon, or <see cref="IntPtr.Zero"/> if retrieval fails.</returns>
    IntPtr GetIconHandle(string filePath, ref ShellFileInfo fileInfo, FileAttribute fileAttributes, FileFlag iconFlags);

    /// <summary>
    /// Safely destroys the icon handle to release resources.
    /// </summary>
    /// <param name="iconHandle">The handle to the icon to be destroyed.</param>
    void DestroyIconHandle(IntPtr iconHandle);

    // ── High-resolution panel item icons ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fallback stage 1: renders the shell's own icon of an EXISTING item through
    /// <c>IShellItemImageFactory</c> (see <see cref="INativeApi.GetShellItemImage"/>). A failure is
    /// written to the diagnostics log (extension + <c>HRESULT</c> only).
    /// </summary>
    /// <param name="filePath">Full path of an existing file or folder.</param>
    /// <param name="pixelSize">Requested square size in physical pixels.</param>
    /// <returns>
    /// An <c>HBITMAP</c> the caller must release with <see cref="DestroyBitmapHandle"/>, or
    /// <see cref="IntPtr.Zero"/> on failure.
    /// </returns>
    IntPtr GetShellItemBitmap(string filePath, int pixelSize);

    /// <summary>
    /// Copies a bitmap's pixels into managed memory (see <see cref="INativeApi.TryReadBitmapPixels"/>).
    /// Does not take ownership of the handle. A failure is written to the diagnostics log.
    /// </summary>
    /// <param name="bitmapHandle">The <c>HBITMAP</c> to read.</param>
    /// <param name="pixels">Receives the pixels on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    bool TryReadBitmapPixels(IntPtr bitmapHandle, out NativeBitmapPixels pixels);

    /// <summary>
    /// Safely releases a bitmap handle obtained from <see cref="GetShellItemBitmap"/>. No-op for
    /// <see cref="IntPtr.Zero"/>.
    /// </summary>
    /// <param name="bitmapHandle">The <c>HBITMAP</c> to release.</param>
    void DestroyBitmapHandle(IntPtr bitmapHandle);

    /// <summary>
    /// Fallback stage 2: resolves an EXISTING item's system icon index (<c>SHGetFileInfo</c> with
    /// <c>SHGFI_SYSICONINDEX</c> and WITHOUT <c>SHGFI_USEFILEATTRIBUTES</c>, so the shell reads the item
    /// itself) and extracts it from the system image list best matching <paramref name="pixelSize"/>.
    /// Failures are written to the diagnostics log.
    /// </summary>
    /// <param name="filePath">Full path of an existing file or folder.</param>
    /// <param name="pixelSize">Requested square size in physical pixels.</param>
    /// <returns>
    /// An <c>HICON</c> the caller must release with <see cref="DestroyIconHandle"/>, or
    /// <see cref="IntPtr.Zero"/> on failure.
    /// </returns>
    IntPtr GetSystemImageListIconHandle(string filePath, int pixelSize);
}
