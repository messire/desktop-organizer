using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Tests for <see cref="AppPackageContext"/>: a thin, constant view over the package-identity probe.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class AppPackageContextTests
{
    /// <summary>
    /// Verifies that <see cref="AppPackageContext.IsPackaged"/> delegates to <see cref="INativeApi.IsPackagedProcess"/>.
    /// </summary>
    /// <param name="packaged">The value the native API reports.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsPackaged_DelegatesToNativeApi(bool packaged)
    {
        var nativeApi = new Mock<INativeApi>();
        nativeApi.SetupGet(n => n.IsPackagedProcess).Returns(packaged);

        var context = new AppPackageContext(nativeApi.Object);

        Assert.Equal(packaged, context.IsPackaged);
        nativeApi.VerifyGet(n => n.IsPackagedProcess, Times.Once);
    }

    /// <summary>
    /// Verifies that a null native API is rejected.
    /// </summary>
    [Fact]
    public void Constructor_NullNativeApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AppPackageContext(null!));
    }
}
