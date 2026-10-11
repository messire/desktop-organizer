namespace DesktopOrganizer.Tests.Unit.Native;

/// <summary>
/// Pins the package-identity probe (Microsoft Store channel): the interpretation of the
/// <c>GetCurrentPackageFullName</c> return code and the cached, never-throwing process-wide answer.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class PackageIdentityProbeTests
{
    /// <summary>
    /// Verifies the return-code table: <c>15700</c> (<c>APPMODEL_ERROR_NO_PACKAGE</c>) is not packaged,
    /// <c>122</c> (<c>ERROR_INSUFFICIENT_BUFFER</c>, the expected answer for a zero-length buffer) and
    /// <c>0</c> are packaged, and any other code is conservatively not packaged.
    /// </summary>
    /// <param name="returnCode">The probe's Win32 return code.</param>
    /// <param name="expected">Whether the process is considered packaged.</param>
    [Theory]
    [InlineData(15700, false)]
    [InlineData(122, true)]
    [InlineData(0, true)]
    [InlineData(5, false)]
    [InlineData(87, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    public void InterpretPackageProbe_MapsReturnCodes(int returnCode, bool expected)
    {
        Assert.Equal(expected, NativeApi.InterpretPackageProbe(returnCode));
    }

    /// <summary>
    /// Verifies that the (unpackaged) test host reports no package identity, with the documented
    /// <c>APPMODEL_ERROR_NO_PACKAGE</c> code, and that the answer is the same on every read (cached).
    /// </summary>
    [Fact]
    public void HasPackageIdentity_IsFalseInTestHost()
    {
        Assert.False(NativeApi.HasPackageIdentity);
        Assert.Equal(15700, NativeApi.PackageProbeResultCode);
        Assert.Equal(NativeApi.HasPackageIdentity, NativeApi.HasPackageIdentity);
        Assert.Equal(NativeApi.HasPackageIdentity, NativeApi.InterpretPackageProbe(NativeApi.PackageProbeResultCode));
    }

    /// <summary>
    /// Verifies that <see cref="DesktopOrganizer.Services.Interfaces.INativeApi.IsPackagedProcess"/> returns
    /// the static, cached probe result.
    /// </summary>
    [Fact]
    public void IsPackagedProcess_MatchesStatic()
    {
        using var api = new NativeApi();

        Assert.Equal(NativeApi.HasPackageIdentity, api.IsPackagedProcess);
    }
}
