using Xunit;

// Several tests (IconProviderTests, IconHandlerTests) redirect the process-wide Console.Error via
// Console.SetError to capture logged error messages, then restore it in a finally block. xUnit
// parallelizes test *classes* by default, so without this attribute a concurrently-running test class
// that also writes to Console.Error (e.g. PanelItemStorageServiceTests' failure-path logging) can race
// with the redirect/restore, intermittently throwing ObjectDisposedException ("Cannot write to a
// closed TextWriter"). Disabling test parallelization removes the race — the suite is small enough
// (well under a second) that serial execution has no meaningful cost.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
