using System.Windows.Media.Imaging;
using DesktopOrganizer.Services.Interfaces;
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
        var iconHandle = User32.LoadIcon(IntPtr.Zero, User32.IdiApplication);
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

    // ── release-polish 1.0: bitmaps from IShellItemImageFactory pixels ──────────────────────────────

    /// <summary>
    /// Verifies that premultiplied data (no channel above its alpha) is kept as <see cref="PixelFormats.Pbgra32"/>
    /// and that the result is frozen and has the source dimensions.
    /// </summary>
    [Fact]
    public void CreateBitmap_FromPremultipliedPixels_IsFrozenPbgra32_WithSourceSize()
    {
        // 2x1: an opaque red pixel and a half-transparent premultiplied grey (64 <= 128).
        var pixels = new NativeBitmapPixels(2, 1, new byte[] { 0, 0, 255, 255, 64, 64, 64, 128 });

        var bitmap = (BitmapSource)_iconFactory.CreateBitmap(pixels, 32);

        Assert.True(bitmap.IsFrozen);
        Assert.Equal(2, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
    }

    /// <summary>
    /// Verifies the pixel-format decision: straight alpha (a channel above its alpha), premultiplied, and a
    /// buffer without any alpha at all (shown opaque rather than invisible).
    /// </summary>
    [Fact]
    public void DetectPixelFormat_DistinguishesStraightPremultipliedAndNoAlpha()
    {
        Assert.Equal(PixelFormats.Bgra32, IconFactory.DetectPixelFormat(new byte[] { 255, 255, 255, 128 }));
        Assert.Equal(PixelFormats.Pbgra32, IconFactory.DetectPixelFormat(new byte[] { 100, 100, 100, 128, 0, 0, 0, 0 }));
        Assert.Equal(PixelFormats.Bgr32, IconFactory.DetectPixelFormat(new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 }));
    }

    /// <summary>
    /// Verifies that a buffer whose length does not match the dimensions is rejected instead of producing a
    /// corrupt bitmap (the provider logs it and falls back to the next stage).
    /// </summary>
    [Fact]
    public void CreateBitmap_FromPixels_WithMismatchedBuffer_Throws()
        => Assert.Throws<ArgumentException>(() => _iconFactory.CreateBitmap(new NativeBitmapPixels(2, 2, new byte[4]), 32));

    /// <summary>
    /// M2 (release-polish 1.0 review): <c>SIIGBF_BIGGERSIZEOK</c> hands back the 256 px frame for most requests
    /// between 72 and 192 px. The cached bitmap must be EXACTLY the requested bucket size (so a count-bounded LRU
    /// cannot hold ~256 KB per entry), frozen, and self-contained.
    /// </summary>
    /// <param name="bucket">The snapped pixel size requested.</param>
    [Theory]
    [InlineData(72)]
    [InlineData(96)]
    [InlineData(128)]
    [InlineData(192)]
    public void CreateBitmap_FromLargerFrame_IsDownscaledToExactlyTheBucketSize(int bucket)
    {
        var pixels = SolidPixels(256, 256, b: 10, g: 20, r: 30, a: 255);

        var bitmap = (BitmapSource)_iconFactory.CreateBitmap(pixels, bucket);

        Assert.Equal(bucket, bitmap.PixelWidth);
        Assert.Equal(bucket, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
    }

    /// <summary>
    /// A frame at or below the bucket is never upscaled — WPF scales it at render time.
    /// </summary>
    [Fact]
    public void CreateBitmap_FromSmallerOrEqualFrame_KeepsItsSize()
    {
        var equal = (BitmapSource)_iconFactory.CreateBitmap(SolidPixels(48, 48, 1, 2, 3, 255), 48);
        var smaller = (BitmapSource)_iconFactory.CreateBitmap(SolidPixels(32, 32, 1, 2, 3, 255), 48);

        Assert.Equal(48, equal.PixelWidth);
        Assert.Equal(32, smaller.PixelWidth);
    }

    /// <summary>
    /// Alpha stays correct through the downscale: STRAIGHT-alpha input (a colour channel above its alpha) is
    /// resampled in premultiplied space, so a uniform 50 % white stays 50 % white (premultiplied 128/128), with no
    /// darkening halo from black transparent pixels bleeding in.
    /// </summary>
    [Fact]
    public void CreateBitmap_Downscale_KeepsStraightAlphaCorrect()
    {
        var pixels = SolidPixels(256, 256, b: 255, g: 255, r: 255, a: 128);

        var bitmap = (BitmapSource)_iconFactory.CreateBitmap(pixels, 64);

        var center = ReadPixel(bitmap, 32, 32);
        Assert.InRange(center.A, 126, 130);
        Assert.InRange(center.B, 126, 130); // premultiplied: 255 * 128 / 255
        Assert.InRange(center.R, 126, 130);
    }

    /// <summary>
    /// A frame without any alpha channel (all alpha bytes 0) is shown opaque after the downscale, not invisible.
    /// </summary>
    [Fact]
    public void CreateBitmap_Downscale_NoAlphaChannel_IsOpaque()
    {
        var bitmap = (BitmapSource)_iconFactory.CreateBitmap(SolidPixels(256, 256, 40, 50, 60, 0), 48);

        Assert.Equal(255, ReadPixel(bitmap, 10, 10).A);
    }

    /// <summary>
    /// A non-positive target size is a programming error.
    /// </summary>
    [Fact]
    public void CreateBitmap_FromPixels_NonPositiveTarget_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => _iconFactory.CreateBitmap(SolidPixels(2, 2, 0, 0, 0, 255), 0));

    private static NativeBitmapPixels SolidPixels(int width, int height, byte b, byte g, byte r, byte a)
    {
        var buffer = new byte[width * height * 4];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = a;
        }

        return new NativeBitmapPixels(width, height, buffer);
    }

    private static (byte B, byte G, byte R, byte A) ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
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
