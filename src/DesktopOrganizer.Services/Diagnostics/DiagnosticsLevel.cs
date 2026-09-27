namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// The two diagnostics granularities a running process can be at (ADR-033 part 1). Orthogonal to
/// whether <see cref="Abstractions.IAppLog"/> writes at all — that is controlled separately by
/// <see cref="DiagnosticsDefaults.FileLoggingEnabled"/> and stays on in every build regardless of this
/// level; only gesture-lifecycle tracing (<see cref="Abstractions.IDiagnosticsLog"/>) depends on it.
/// </summary>
public enum DiagnosticsLevel
{
    /// <summary>
    /// Default in Release without <c>--diag</c>/the environment variable: gesture-lifecycle tracing
    /// (<see cref="Abstractions.IDiagnosticsLog"/>) resolves to a no-op sink; <see cref="Abstractions.IAppLog"/>
    /// still writes.
    /// </summary>
    Standard,

    /// <summary>
    /// Always on in Debug builds, or in Release with <c>--diag</c>/<c>DESKTOPORGANIZER_DIAG=1</c>:
    /// gesture-lifecycle tracing is also written, to the very same sink <see cref="Abstractions.IAppLog"/>
    /// uses.
    /// </summary>
    Verbose,
}
