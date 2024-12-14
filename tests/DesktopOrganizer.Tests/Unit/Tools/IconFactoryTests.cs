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
}
