using DesktopOrganizer.Services.SingleInstance;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>Unit tests for <see cref="SingleInstanceNaming"/> (ADR-023 D-1).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceNamingTests
{
    // T-NAM-01
    [Fact]
    public void InstallerDetectionMutexName_MatchesInstallerLiteral()
    {
        // Verbatim contract consumed by installer/DesktopOrganizer.iss:57 AppMutex=. See also
        // InstallerMutexContractTests, which parses the .iss file itself.
        Assert.Equal("DesktopOrganizerSingleInstanceMutex", SingleInstanceNaming.InstallerDetectionMutexName);
    }

    // T-NAM-02
    [Fact]
    public void GuardMutexName_StartsWithLocalNamespacePrefix()
    {
        var name = SingleInstanceNaming.GuardMutexName("abc123");

        Assert.StartsWith(@"Local\", name, StringComparison.Ordinal);
    }

    // T-NAM-03
    [Fact]
    public void UserKeyHash_IsDeterministic_AndDoesNotContainRawInput()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        var first = SingleInstanceNaming.UserKeyHash(sid);
        var second = SingleInstanceNaming.UserKeyHash(sid);

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
        Assert.DoesNotContain(sid, first, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21", first, StringComparison.Ordinal);
    }

    // T-NAM-04
    [Fact]
    public void UserKeyHash_DifferentSids_ProduceDifferentNames()
    {
        var first = SingleInstanceNaming.UserKeyHash("S-1-5-21-1111111111-2222222222-3333333333-1001");
        var second = SingleInstanceNaming.UserKeyHash("S-1-5-21-1111111111-2222222222-3333333333-1002");

        Assert.NotEqual(first, second);
        Assert.NotEqual(
            SingleInstanceNaming.GuardMutexName(first),
            SingleInstanceNaming.GuardMutexName(second));
        Assert.NotEqual(
            SingleInstanceNaming.ActivationPipeName(first),
            SingleInstanceNaming.ActivationPipeName(second));
    }

    [Fact]
    public void UserKeyHash_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SingleInstanceNaming.UserKeyHash(null!));
    }

    [Fact]
    public void ActivationPipeName_IncludesUserKey()
    {
        var name = SingleInstanceNaming.ActivationPipeName("deadbeefcafebabe");

        Assert.Contains("deadbeefcafebabe", name, StringComparison.Ordinal);
    }
}
