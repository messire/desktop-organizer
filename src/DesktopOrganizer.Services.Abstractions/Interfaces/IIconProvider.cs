using System.Diagnostics.CodeAnalysis;
using System.Windows.Media;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Provides functionality to cache file icons for improved performance.
/// </summary>
public interface IIconProvider
{
    /// <summary>
    /// Gets this provider's own generic placeholder (the uniform gray square <see cref="GetIcon(string, int)"/>
    /// falls back to as a last resort). Created once, thread-safe, frozen (ADR-046 C-4). The very first
    /// access must happen on an STA thread (it may render a <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/>).
    /// </summary>
    ImageSource PlaceholderIcon { get; }

    /// <summary>
    /// Get file icon or placeholder at the default size (<see cref="IconPixelSize.Default"/> pixels).
    /// Equivalent to <see cref="GetIcon(string, int)"/> with <see cref="IconPixelSize.Default"/>.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <returns>A <see cref="ImageSource"/> representing the icon.</returns>
    ImageSource GetIcon(string filePath);

    /// <summary>
    /// Get the file's own icon (Explorer-equivalent: <c>.lnk</c> target icon, embedded <c>.exe</c>/<c>.ico</c>
    /// icon, custom folder icon) rendered for a square of <paramref name="pixelSize"/> physical pixels, or
    /// a placeholder.
    /// </summary>
    /// <remarks>
    /// <paramref name="pixelSize"/> is snapped up to a shell-friendly size (<see cref="IconPixelSize.Snap"/>),
    /// and the snapped value is part of the cache key — two panels showing the same file at different sizes
    /// each get their own bitmap. The returned bitmap may be larger than requested (the shell may hand back a
    /// bigger frame); callers size the <c>Image</c> element themselves. A path that does not exist gets a
    /// type icon by extension, never cached (see the implementation's remarks). UI-thread (STA) call.
    /// </remarks>
    /// <param name="filePath">File path.</param>
    /// <param name="pixelSize">
    /// Physical pixel size the icon will be displayed at (DIP size × DPI scale — see
    /// <see cref="IconPixelSize.FromDip"/>).
    /// </param>
    /// <returns>A frozen <see cref="ImageSource"/> representing the icon.</returns>
    ImageSource GetIcon(string filePath, int pixelSize);

    /// <summary>
    /// True if <paramref name="icon"/> is this provider's own generic placeholder (the uniform gray
    /// square <see cref="GetIcon(string, int)"/> falls back to when neither the OS nor this provider could produce
    /// a real icon) rather than any icon actually resolved for a file/folder. Added for M3 (P08 fix
    /// cycle 1, 2026-09-22): lets a caller distinguish "this item's icon is the last-resort
    /// placeholder and might be worth retrying" from "this is a real, resolved icon that merely looks
    /// generic" — see <c>BaseViewModel.RefreshItemsLiveness</c>'s bounded placeholder-retry.
    /// </summary>
    /// <param name="icon">The icon previously returned by <see cref="GetIcon(string, int)"/>.</param>
    bool IsPlaceholder(ImageSource? icon);

    /// <summary>
    /// Cache-only lookup in the existing <c>(path, Snap(size))</c> icon cache: no file-system access, no
    /// shell call, and — unlike <see cref="GetIcon(string, int)"/> — no freshness check (the queued
    /// <see cref="GetIcon(string, int)"/> call re-validates once it actually runs). Safe from any thread.
    /// A hit moves the entry to the front of the LRU list, same as an ordinary <see cref="GetIcon(string, int)"/> hit.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="pixelSize">Requested physical pixel size (snapped the same way as <see cref="GetIcon(string, int)"/>).</param>
    /// <param name="icon">The cached icon, when found.</param>
    /// <returns><see langword="true"/> when the cache already held a fresh-enough entry for this key.</returns>
    bool TryPeekCachedIcon(string filePath, int pixelSize, [NotNullWhen(true)] out ImageSource? icon);

    /// <summary>
    /// Cache-only lookup of a type icon previously produced by <see cref="GetTypeIcon"/>. No file-system
    /// access, no shell call. Safe from any thread.
    /// </summary>
    /// <param name="filePath">File path (only its extension is used as the cache key).</param>
    /// <param name="isDirectory">Whether the item is a directory (directories share a single cache entry).</param>
    /// <returns>The cached type icon, or <see langword="null"/> when none has been resolved yet.</returns>
    ImageSource? TryPeekTypeIcon(string filePath, bool isDirectory);

    /// <summary>
    /// Resolves ONLY the generic type icon (<c>SHGFI_USEFILEATTRIBUTES | SHGFI_SHELLICONSIZE</c>, by
    /// extension/attribute) — the pre-1.0 stage 3 fallback of <see cref="GetIcon(string, int)"/> — cached
    /// per lower-case extension (or the closed <c>"&lt;dir&gt;"</c> token for a directory) in a cache
    /// separate from <see cref="GetIcon(string, int)"/>'s own <c>(path, size)</c> cache. Never touches the
    /// file system. The shell call itself must run on a worker thread in production (never the UI thread).
    /// </summary>
    /// <param name="filePath">File path (only its extension is used).</param>
    /// <param name="isDirectory">Whether the item is a directory.</param>
    /// <returns>A frozen type icon, or this provider's <see cref="PlaceholderIcon"/> on failure (not cached).</returns>
    ImageSource GetTypeIcon(string filePath, bool isDirectory);
}
