using System.Runtime.CompilerServices;

[assembly: CLSCompliant(false)] // This assembly uses native P/Invoke calls and is not CLS-compliant.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
[assembly: InternalsVisibleTo("DesktopOrganizer.Tests")]

// Lets DesktopOrganizer.UI's DiConfig forward its own test-only diagnosticsLogDirectory overload
// (see DesktopOrganizer.UI.Configuration.DiConfig.ConfigureServices) to this assembly's internal
// RegisterServices(IServiceCollection, string?) overload, so UI-layer DI integration tests can avoid
// touching the real per-user diagnostics log directory without duplicating its registration logic.
[assembly: InternalsVisibleTo("DesktopOrganizer.UI")]
