using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Writes a single, synchronous, human-readable crash report (ADR-033 part 2, release-hardening plan
/// §2) to <c>crash-yyyyMMdd-HHmmss-&lt;pid&gt;.log</c> — deliberately NOT through
/// <see cref="JsonLinesDiagnosticsLog"/>'s queued background-thread pipeline: a process that is fatally
/// crashing cannot rely on that writer thread ever getting a chance to drain before the process exits,
/// which is exactly why this uses a direct, synchronous <see cref="File.WriteAllText(string, string)"/>
/// instead.
/// </summary>
/// <remarks>
/// Every exception message/stack trace is scrubbed via <see cref="LogScrubber"/> before being written —
/// same privacy invariant as <see cref="DiagnosticsAppLog"/>. <see cref="Write"/> never throws: any
/// failure (a locked/unwritable directory, a full disk, …) degrades to returning
/// <see cref="string.Empty"/> so a crash report failure can never itself become a second crash.
/// </remarks>
public sealed class CrashReportWriter
{
    /// <summary>Maximum number of crash reports retained on disk (oldest deleted first).</summary>
    internal const int RetentionCount = 10;

    private const string Prefix = "crash-";
    private const string Extension = ".log";

    private readonly string _directory;
    private readonly Func<bool>? _testForceRetentionFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashReportWriter"/> class.
    /// </summary>
    /// <param name="directory">The folder crash reports are written to — the same <c>Logs</c> folder <see cref="JsonLinesDiagnosticsLog"/> uses.</param>
    public CrashReportWriter(string directory)
        : this(directory, testForceRetentionFailure: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashReportWriter"/> class, with a test-only hook
    /// (same pattern as <see cref="JsonLinesDiagnosticsLog"/>'s <c>testForceReopenFailure</c>): lets
    /// <c>CrashReportWriterTests</c> force <see cref="ApplyRetention"/> to fail deterministically (e.g.
    /// simulating <see cref="Directory.GetFiles(string, string)"/> itself throwing) without needing a
    /// real, flaky filesystem/ACL setup — L3, release-hardening review.
    /// </summary>
    /// <param name="directory">See the public constructor.</param>
    /// <param name="testForceRetentionFailure">When it returns <see langword="true"/>, <see cref="ApplyRetention"/> throws immediately instead of touching disk.</param>
    internal CrashReportWriter(string directory, Func<bool>? testForceRetentionFailure)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _testForceRetentionFailure = testForceRetentionFailure;
    }

    /// <summary>
    /// Writes the report and applies retention. Never throws.
    /// </summary>
    /// <param name="exception">The fatal exception (its full <see cref="Exception.InnerException"/> chain is recorded).</param>
    /// <param name="origin">Which kind of thread it was caught on.</param>
    /// <returns>The full path written, or <see cref="string.Empty"/> if writing failed.</returns>
    public string Write(Exception exception, CrashOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string path;
        try
        {
            Directory.CreateDirectory(_directory);

            path = Path.Combine(_directory, BuildFileName(DateTimeOffset.UtcNow, Environment.ProcessId));
            File.WriteAllText(path, BuildContent(exception, origin));
        }
        catch
        {
            // Never throws back into the crash-handling sequence — see type remarks.
            return string.Empty;
        }

        try
        {
            // L3 (release-hardening review): retention is best-effort bookkeeping performed AFTER the
            // report itself is already safely on disk — a failure here (e.g. Directory.GetFiles itself
            // throwing) must never turn an already-successful write into a reported failure by falling
            // through to the shared catch above.
            ApplyRetention();
        }
        catch
        {
            // Best effort — see ApplyRetention's own remarks.
        }

        return path;
    }

    /// <summary>
    /// Builds the file name for a report written at <paramref name="utcNow"/> by process
    /// <paramref name="processId"/>, e.g. <c>crash-20260927-101500-12345.log</c>. Pure — exposed so
    /// <c>CrashReportWriterTests</c> can pin the exact shape without racing the real clock.
    /// </summary>
    /// <param name="utcNow">The report's UTC timestamp.</param>
    /// <param name="processId">The current process id, disambiguating concurrent instances.</param>
    internal static string BuildFileName(DateTimeOffset utcNow, int processId) =>
        $"{Prefix}{utcNow:yyyyMMdd-HHmmss}-{processId}{Extension}";

    /// <summary>
    /// Builds the report's full text content: UTC timestamp, app version, OS/runtime info, UI culture,
    /// <paramref name="origin"/>, then every exception reachable from <paramref name="exception"/> (type
    /// name, <see cref="Exception.HResult"/>, scrubbed message, scrubbed stack trace) — walking not just
    /// the single-exception <see cref="Exception.InnerException"/> chain, but every branch of an
    /// <see cref="AggregateException.InnerExceptions"/> tree too (L3, release-hardening review): a plain
    /// <c>InnerException</c> walk only ever sees an <see cref="AggregateException"/>'s FIRST inner
    /// exception, silently dropping every other concurrent failure it actually wraps. Pure — exposed for
    /// <c>CrashReportWriterTests</c>.
    /// </summary>
    /// <param name="exception">The fatal exception.</param>
    /// <param name="origin">Which kind of thread it was caught on.</param>
    internal static string BuildContent(Exception exception, CrashOrigin origin)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Time (UTC): {DateTimeOffset.UtcNow:O}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Version: {AppVersionInfo.Full}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion.VersionString}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"UI culture: {CultureInfo.CurrentUICulture}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Origin: {origin}"));
        text.AppendLine();

        var pending = new Queue<(Exception Exception, int Depth)>();
        pending.Enqueue((exception, 0));

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Dequeue();

            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[{depth}] {current.GetType().FullName} (HResult 0x{current.HResult:X8})"));
            text.AppendLine(LogScrubber.Scrub(current.Message));
            text.AppendLine(LogScrubber.Scrub(current.StackTrace ?? string.Empty));
            text.AppendLine();

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Enqueue((inner, depth + 1));
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue((current.InnerException, depth + 1));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Deletes the oldest reports beyond <see cref="RetentionCount"/>. Best-effort: a single file that
    /// fails to delete (e.g. held open by an antivirus scanner) is skipped rather than aborting the rest
    /// of the pass.
    /// </summary>
    private void ApplyRetention()
    {
        if (_testForceRetentionFailure?.Invoke() == true)
            throw new IOException("Simulated retention failure (test-only).");

        var existing = Directory.GetFiles(_directory, $"{Prefix}*{Extension}");
        if (existing.Length <= RetentionCount) return;

        var toDelete = existing
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Take(existing.Length - RetentionCount);

        foreach (var path in toDelete)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort — see method remarks.
            }
        }
    }
}
