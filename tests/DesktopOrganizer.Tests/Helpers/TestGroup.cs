namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Names of xUnit <c>[Trait]</c> groups used to separate fast unit tests from slower integration tests.
/// </summary>
public static class TestGroup
{
    /// <summary>Group for tests that touch real infrastructure (filesystem, pipes, WPF resources, etc.).</summary>
    public const string IntegrationTests = nameof(IntegrationTests);

    /// <summary>Group for fast, isolated unit tests with no external dependencies.</summary>
    public const string UnitTests = nameof(UnitTests);
}
