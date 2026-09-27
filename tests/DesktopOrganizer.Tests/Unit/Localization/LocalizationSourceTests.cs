using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="LocalizationSource"/> (docs/PLANS/localization.md §2.4, WP-0U DoD 2).
/// Every test resets the process-wide static in a <see langword="finally"/> block — this is the ONE
/// sanctioned static access point (L-5), so leaving it mutated would leak into every other test class in
/// this process.
/// </summary>
[Collection("GlobalCulture")]
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LocalizationSourceTests
{
    /// <summary>
    /// Before <see cref="LocalizationSource.Initialize"/> is ever called, <see cref="LocalizationSource.Current"/>
    /// lazily creates a fresh, English <see cref="LocalizationService"/> — deterministic regardless of
    /// the test runner machine's own culture (R-5).
    /// </summary>
    [Fact]
    public void Current_BeforeInitialize_LazilyCreatesEnglishInstance()
    {
        LocalizationSource.ResetForTests();
        try
        {
            var current = LocalizationSource.Current;

            Assert.Equal("en", current.CurrentCulture.Name);
            Assert.Same(current, LocalizationSource.Current);
        }
        finally
        {
            LocalizationSource.ResetForTests();
        }
    }

    /// <summary>
    /// <see cref="LocalizationSource.Initialize"/>, called BEFORE <see cref="LocalizationSource.Current"/>
    /// is ever read, installs the given instance — no exception, no lazy default ever created.
    /// </summary>
    [Fact]
    public void Initialize_BeforeCurrentIsRead_InstallsGivenInstance()
    {
        LocalizationSource.ResetForTests();
        try
        {
            var service = TestLocalization.Russian;

            LocalizationSource.Initialize(service);

            Assert.Same(service, LocalizationSource.Current);
        }
        finally
        {
            LocalizationSource.ResetForTests();
        }
    }

    /// <summary>
    /// Calling <see cref="LocalizationSource.Initialize"/> again with the SAME instance after
    /// <see cref="LocalizationSource.Current"/> has already been read is a harmless no-op (idempotent —
    /// nothing captured a stale reference, since it is the same object).
    /// </summary>
    [Fact]
    public void Initialize_AfterCurrentReadWithSameInstance_DoesNotThrow()
    {
        LocalizationSource.ResetForTests();
        try
        {
            var service = TestLocalization.English;
            LocalizationSource.Initialize(service);
            _ = LocalizationSource.Current;

            var exception = Record.Exception(() => LocalizationSource.Initialize(service));

            Assert.Null(exception);
            Assert.Same(service, LocalizationSource.Current);
        }
        finally
        {
            LocalizationSource.ResetForTests();
        }
    }

    /// <summary>
    /// The hazard §2.4 documents: once <see cref="LocalizationSource.Current"/> has been read — so some
    /// binding/element may already have captured it as a binding <c>Source</c> — calling
    /// <see cref="LocalizationSource.Initialize"/> again with a DIFFERENT instance throws, rather than
    /// silently leaving those earlier consumers pointed at the stale instance forever.
    /// </summary>
    [Fact]
    public void Initialize_AfterCurrentReadWithDifferentInstance_Throws()
    {
        LocalizationSource.ResetForTests();
        try
        {
            _ = LocalizationSource.Current; // lazily creates the default English instance, and reads it.
            var replacement = TestLocalization.Russian;

            Assert.Throws<InvalidOperationException>(() => LocalizationSource.Initialize(replacement));
        }
        finally
        {
            LocalizationSource.ResetForTests();
        }
    }

    /// <summary>Verifies <see cref="LocalizationSource.Initialize"/> rejects <see langword="null"/>.</summary>
    [Fact]
    public void Initialize_NullService_Throws()
    {
        LocalizationSource.ResetForTests();
        try
        {
            Assert.Throws<ArgumentNullException>(() => LocalizationSource.Initialize(null!));
        }
        finally
        {
            LocalizationSource.ResetForTests();
        }
    }
}
