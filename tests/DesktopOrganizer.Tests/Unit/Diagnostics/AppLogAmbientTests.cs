using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for the ambient <see cref="AppLog"/> holder (ADR-033 part 1). <see cref="AppLog"/> is a
/// process-wide static, so every test here restores it to its pristine default
/// (<see cref="NullAppLog.Instance"/>, uninitialized) in a <c>finally</c> block via
/// <see cref="AppLog.ResetForTests"/> — the test project runs single-threaded (<c>DisableTestParallelization</c>,
/// <c>AssemblyInfo.cs</c>), so this is safe: no other test observes the mutated state in between.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class AppLogAmbientTests
{
    /// <summary>Before <see cref="AppLog.Initialize"/> is ever called, <see cref="AppLog.Current"/> is
    /// the shared <see cref="NullAppLog.Instance"/>.</summary>
    [Fact]
    public void Current_BeforeInitialize_IsNullAppLogInstance()
    {
        Assert.Same(NullAppLog.Instance, AppLog.Current);
    }

    /// <summary><see cref="AppLog.Initialize"/> sets <see cref="AppLog.Current"/> to the supplied log.</summary>
    [Fact]
    public void Initialize_SetsCurrentToTheSuppliedLog()
    {
        try
        {
            var log = Mock.Of<IAppLog>();

            AppLog.Initialize(log);

            Assert.Same(log, AppLog.Current);
        }
        finally
        {
            AppLog.ResetForTests();
        }
    }

    /// <summary>A second <see cref="AppLog.Initialize"/> call is a silent no-op: the first log installed
    /// keeps winning.</summary>
    [Fact]
    public void Initialize_CalledTwice_SecondCallIsNoOp()
    {
        try
        {
            var first = Mock.Of<IAppLog>();
            var second = Mock.Of<IAppLog>();

            AppLog.Initialize(first);
            AppLog.Initialize(second);

            Assert.Same(first, AppLog.Current);
        }
        finally
        {
            AppLog.ResetForTests();
        }
    }

    /// <summary><see cref="AppLog.Initialize"/> rejects a <see langword="null"/> log.</summary>
    [Fact]
    public void Initialize_NullLog_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AppLog.Initialize(null!));
    }

    /// <summary><see cref="AppLog.ResetForTests"/> itself restores the pristine default, allowing
    /// <see cref="AppLog.Initialize"/> to take effect again afterwards.</summary>
    [Fact]
    public void ResetForTests_RestoresDefault_AndAllowsInitializeAgain()
    {
        try
        {
            AppLog.Initialize(Mock.Of<IAppLog>());
            AppLog.ResetForTests();

            Assert.Same(NullAppLog.Instance, AppLog.Current);

            var log = Mock.Of<IAppLog>();
            AppLog.Initialize(log);

            Assert.Same(log, AppLog.Current);
        }
        finally
        {
            AppLog.ResetForTests();
        }
    }
}
