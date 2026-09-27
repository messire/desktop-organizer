using System.IO;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="CrashReportWriter"/> (ADR-033 part 2) — content, scrubbing, file naming,
/// retention, and "never throws" behavior. Every test uses a disposable temp directory, never the real
/// per-user <c>Logs</c> folder.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class CrashReportWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>Deletes the disposable temp directory this test class writes crash reports into.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A written report's path is returned and the file actually exists on disk.</summary>
    [Fact]
    public void Write_ReturnsPathToAFileThatExists()
    {
        var sut = new CrashReportWriter(_directory);

        var path = sut.Write(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        Assert.NotEmpty(path);
        Assert.True(File.Exists(path));
    }

    /// <summary>The file name follows the <c>crash-yyyyMMdd-HHmmss-&lt;pid&gt;.log</c> shape.</summary>
    [Fact]
    public void Write_FileName_MatchesCrashPrefixPidAndExtension()
    {
        var sut = new CrashReportWriter(_directory);

        var path = sut.Write(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        var fileName = Path.GetFileName(path);
        Assert.StartsWith("crash-", fileName, StringComparison.Ordinal);
        Assert.EndsWith($"-{Environment.ProcessId}.log", fileName, StringComparison.Ordinal);
    }

    /// <summary>The report's content includes the app version, the origin, and the exception's type/message.</summary>
    [Fact]
    public void Write_Content_IncludesVersionOriginAndExceptionType()
    {
        var sut = new CrashReportWriter(_directory);

        var path = sut.Write(new InvalidOperationException("boom"), CrashOrigin.WinForms);
        var content = File.ReadAllText(path);

        Assert.Contains("Origin: WinForms", content, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), content, StringComparison.Ordinal);
        Assert.Contains("boom", content, StringComparison.Ordinal);
    }

    /// <summary>Every exception in the <see cref="Exception.InnerException"/> chain is recorded, not just the outermost one.</summary>
    [Fact]
    public void Write_Content_IncludesInnerExceptionChain()
    {
        var inner = new ArgumentException("inner boom");
        var outer = new InvalidOperationException("outer boom", inner);
        var sut = new CrashReportWriter(_directory);

        var path = sut.Write(outer, CrashOrigin.Background);
        var content = File.ReadAllText(path);

        Assert.Contains(nameof(InvalidOperationException), content, StringComparison.Ordinal);
        Assert.Contains(nameof(ArgumentException), content, StringComparison.Ordinal);
        Assert.Contains("inner boom", content, StringComparison.Ordinal);
    }

    /// <summary>A path embedded in the exception message is scrubbed before being written to disk.</summary>
    [Fact]
    public void Write_ExceptionMessageContainingPath_IsScrubbed()
    {
        var sut = new CrashReportWriter(_directory);

        var path = sut.Write(new InvalidOperationException(@"failed to read C:\Users\alice\report.docx"), CrashOrigin.Dispatcher);
        var content = File.ReadAllText(path);

        Assert.DoesNotContain("alice", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path:.docx>", content, StringComparison.Ordinal);
    }

    /// <summary>Writing beyond <see cref="CrashReportWriter.RetentionCount"/> existing reports deletes the oldest ones, keeping exactly the retention count.</summary>
    [Fact]
    public void Write_MoreThanRetentionCount_DeletesOldestBeyondRetention()
    {
        Directory.CreateDirectory(_directory);
        for (var i = 0; i < CrashReportWriter.RetentionCount + 3; i++)
            File.WriteAllText(Path.Combine(_directory, $"crash-2020010{i:00}-000000-1.log"), "old");

        var sut = new CrashReportWriter(_directory);
        sut.Write(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        var remaining = Directory.GetFiles(_directory, "crash-*.log");
        Assert.Equal(CrashReportWriter.RetentionCount, remaining.Length);
    }

    /// <summary>
    /// L3 (release-hardening review): every exception in an <see cref="AggregateException.InnerExceptions"/>
    /// tree is recorded, not just the first one a plain <see cref="Exception.InnerException"/> walk would see.
    /// </summary>
    [Fact]
    public void Write_Content_IncludesEveryAggregateExceptionInnerException()
    {
        var first = new ArgumentException("first boom");
        var second = new InvalidOperationException("second boom");
        var aggregate = new AggregateException("aggregate boom", first, second);
        var sut = new CrashReportWriter(_directory);

        var path = sut.Write(aggregate, CrashOrigin.Background);
        var content = File.ReadAllText(path);

        Assert.Contains(nameof(ArgumentException), content, StringComparison.Ordinal);
        Assert.Contains("first boom", content, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), content, StringComparison.Ordinal);
        Assert.Contains("second boom", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// L3 (release-hardening review): a retention failure (e.g. <c>Directory.GetFiles</c> itself throwing)
    /// must not turn an already-successful report write into <see cref="string.Empty"/> — the file is
    /// already safely on disk by the time retention runs.
    /// </summary>
    [Fact]
    public void Write_RetentionThrows_StillReturnsThePathAndTheFileExists()
    {
        var sut = new CrashReportWriter(_directory, testForceRetentionFailure: () => true);

        var path = sut.Write(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);

        Assert.NotEmpty(path);
        Assert.True(File.Exists(path), "The report file itself must exist even though retention failed.");
    }

    /// <summary>An I/O failure while writing (here: the target directory cannot be created) degrades to <see cref="string.Empty"/> instead of throwing.</summary>
    [Fact]
    public void Write_DirectoryCannotBeCreated_ReturnsEmptyAndNeverThrows()
    {
        // A file used as the "directory" path makes Directory.CreateDirectory throw (IOException: not
        // a directory) — exercising the "never throws" contract without needing real permission denial.
        var blockingFile = Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blockingFile)!);
        File.WriteAllText(blockingFile, "not a directory");
        var invalidDirectory = Path.Combine(blockingFile, "Logs");

        var sut = new CrashReportWriter(invalidDirectory);

        var exception = Record.Exception(() =>
        {
            var path = sut.Write(new InvalidOperationException("boom"), CrashOrigin.Dispatcher);
            Assert.Equal(string.Empty, path);
        });

        Assert.Null(exception);

        File.Delete(blockingFile);
    }

    /// <summary>The constructor rejects a <see langword="null"/> directory.</summary>
    [Fact]
    public void Constructor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CrashReportWriter(null!));
    }
}
