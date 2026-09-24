using System.Windows.Media.Imaging;
using DesktopOrganizer.Tests.Resources;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="IconFactory"/>.
/// </summary>
[Trait(UnitTests, nameof(Services))]
public class IconFactoryTests
{
    private readonly IconFactory _iconFactory = new ();

    /// <summary>
    /// Ensures that placeholder icon is cached and reused.
    /// </summary>
    [Fact]
    public void CreateBitmap_ReturnsCachedPlaceholder()
    {
        // Act
        var firstPlaceholder = _iconFactory.CreateBitmap();
        var secondPlaceholder = _iconFactory.CreateBitmap();

        // Assert
        Assert.NotNull(firstPlaceholder);
        Assert.Same(firstPlaceholder, secondPlaceholder);
    }

    /// <summary>
    /// Ensures that CreateBitmap(IntPtr) creates a valid BitmapSource from an icon handle.
    /// </summary>
    [Fact]
    public void CreateBitmap_WithIconHandle_ReturnsValidBitmap()
    {
        // Arrange
        var iconHandle = User32.LoadIcon(IntPtr.Zero, User32.IDI_APPLICATION);
        Assert.NotEqual(IntPtr.Zero, iconHandle);

        // Act
        var result = _iconFactory.CreateBitmap(iconHandle);

        // Assert
        Assert.NotNull(result);
        Assert.IsAssignableFrom<BitmapSource>(result);

        // Cleanup
        User32.DestroyIcon(iconHandle);
    }

    /// <summary>
    /// Ensures that CreateBitmap(IntPtr) throws an exception for invalid handle.
    /// </summary>
    [Fact]
    public void CreateBitmap_WithInvalidIconHandle_ThrowsArgumentException()
    {
        // Arrange
        var invalidHandle = IntPtr.Zero;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => _iconFactory.CreateBitmap(invalidHandle));
        Assert.Equal("Value cannot be null. (Parameter 'icon')", exception.Message);
    }

    /// <summary>
    /// Regression for the P00 baseline defect (docs/DIAGNOSTICS/icon-loading-findings.md): the
    /// placeholder used to render <see cref="RenderTargetBitmap.Render"/> before its
    /// <see cref="DrawingContext"/> was closed, so nothing had actually been recorded onto the visual
    /// yet — the result was a fully transparent (0/1024 opaque pixels) bitmap. A broken/missing panel
    /// item therefore showed no icon at all, just a dim label floating over empty space, rather than a
    /// visible gray placeholder.
    /// </summary>
    [Fact]
    public void CreateBitmap_Placeholder_IsNotFullyTransparent()
    {
        // Act
        var placeholder = (BitmapSource)_iconFactory.CreateBitmap();

        // Assert
        var opaquePixelCount = CountOpaquePixels(placeholder);
        Assert.True(opaquePixelCount > 0, "Placeholder icon must not be fully transparent.");
    }

    /// <summary>
    /// The placeholder is drawn at 32x32 — matching <c>SHGFI_SHELLICONSIZE</c> (see
    /// <c>IconProvider.IconFlags</c>) — so it renders at native resolution in the Grid item template
    /// instead of being upscaled/blurred from a smaller source (P00 baseline finding H9).
    /// </summary>
    [Fact]
    public void CreateBitmap_Placeholder_MatchesNativeShellIconSize()
    {
        // Act
        var placeholder = (BitmapSource)_iconFactory.CreateBitmap();

        // Assert
        Assert.Equal(32, placeholder.PixelWidth);
        Assert.Equal(32, placeholder.PixelHeight);
    }

    /// <summary>
    /// Counts pixels with a non-zero alpha channel, converting to a known-layout format first so the
    /// alpha byte offset is deterministic regardless of the source bitmap's native pixel format.
    /// </summary>
    private static int CountOpaquePixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, destinationPalette: null, alphaThreshold: 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var opaqueCount = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) opaqueCount++;
        }

        return opaqueCount;
    }
}
