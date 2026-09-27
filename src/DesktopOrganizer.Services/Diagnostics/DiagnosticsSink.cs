using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Owns the single, real <see cref="JsonLinesDiagnosticsLog"/> instance shared by <see cref="IAppLog"/>
/// (always writes) and, at <see cref="DiagnosticsLevel.Verbose"/>, <see cref="IDiagnosticsLog"/> (gesture
/// tracing) — so both facades interleave into the very same JSONL file rather than each owning an
/// independent background writer thread and directory-retention pass.
/// </summary>
/// <remarks>
/// Deliberately lazy (see <see cref="Log"/>): a process that never actually logs anything (an ordinary
/// secondary launch, ADR-023 R-12) must never start the writer thread or touch the log directory just
/// because this singleton itself was constructed/resolved. Registered as an <see cref="IDisposable"/>
/// singleton so the root <c>ServiceProvider</c> disposes it exactly once at shutdown — see
/// <see cref="Dispose"/>, which itself only disposes the underlying sink if it was ever actually created.
/// </remarks>
internal sealed class DiagnosticsSink : IDisposable
{
    private readonly Lazy<JsonLinesDiagnosticsLog> _lazy;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsSink"/> class.
    /// </summary>
    /// <param name="directory">The folder the underlying sink writes its JSONL session files to.</param>
    public DiagnosticsSink(string directory) =>
        _lazy = new Lazy<JsonLinesDiagnosticsLog>(() => new JsonLinesDiagnosticsLog(directory), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The real sink, materialized on first access.</summary>
    public IDiagnosticsLog Log => _lazy.Value;

    /// <inheritdoc />
    public void Dispose()
    {
        // IsValueCreated guard: disposing must never itself force-materialize the sink (see type
        // remarks) — a session that never logged anything must exit having never touched the log
        // directory at all.
        if (_lazy.IsValueCreated) _lazy.Value.Dispose();
    }
}
