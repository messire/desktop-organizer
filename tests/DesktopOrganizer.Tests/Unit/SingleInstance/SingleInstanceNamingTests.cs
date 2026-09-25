using DesktopOrganizer.Services.SingleInstance;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>Unit tests for <see cref="SingleInstanceNaming"/> (ADR-023 D-1).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceNamingTests
{
    /// <summary>
    /// Verifies that <see cref="SingleInstanceNaming.InstallerDetectionMutexName"/> matches the literal
    /// name consumed by the installer script (T-NAM-01).
    /// </summary>
    [Fact]
    public void InstallerDetectionMutexName_MatchesInstallerLiteral()
    {
        // Verbatim contract consumed by installer/DesktopOrganizer.iss:57 AppMutex=. See also
        // InstallerMutexContractTests, which parses the .iss file itself.
        Assert.Equal("DesktopOrganizerSingleInstanceMutex", SingleInstanceNaming.InstallerDetectionMutexName);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceNaming.GuardMutexName"/> always starts with the
    /// <c>Local\</c> namespace prefix (T-NAM-02).
    /// </summary>
    [Fact]
    public void GuardMutexName_StartsWithLocalNamespacePrefix()
    {
        var name = SingleInstanceNaming.GuardMutexName("abc123");

        Assert.StartsWith(@"Local\", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceNaming.UserKeyHash"/> is deterministic for the same input
    /// and never leaks the raw SID (or its well-known prefix) into the hash (T-NAM-03).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="SingleInstanceNaming.UserKeyHash"/> produces different hashes for
    /// different SIDs, and that the derived guard mutex/activation pipe names differ accordingly
    /// (T-NAM-04).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="SingleInstanceNaming.UserKeyHash"/> throws
    /// <see cref="ArgumentNullException"/> for a <see langword="null"/> input.
    /// </summary>
    [Fact]
    public void UserKeyHash_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SingleInstanceNaming.UserKeyHash(null!));
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceNaming.ActivationPipeName"/> includes the given user key
    /// hash verbatim.
    /// </summary>
    [Fact]
    public void ActivationPipeName_IncludesUserKey()
    {
        var name = SingleInstanceNaming.ActivationPipeName("deadbeefcafebabe");

        Assert.Contains("deadbeefcafebabe", name, StringComparison.Ordinal);
    }
}
