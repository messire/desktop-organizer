using System.IO;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools.IconLoading;
using DesktopOrganizer.Tests.Helpers;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>Unit tests for <see cref="PathProbe"/> (ADR-046 D-11).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class PathProbeTests
{
    private readonly Mock<IRemotePathClassifier> _classifier = new();
    private readonly Mock<IRemoteRootCircuitBreaker> _breaker = new();
    private readonly PathProbe _probe;

    /// <summary>Initializes a new instance of the <see cref="PathProbeTests"/> class with every path classified as local by default.</summary>
    public PathProbeTests()
    {
        _classifier.Setup(c => c.GetOwnRemoteRoot(It.IsAny<string>())).Returns((string?)null);
        _breaker.Setup(b => b.BeginCall(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        _probe = new PathProbe(_classifier.Object, _breaker.Object);
    }

    /// <summary>An existing file probes as existing, not a directory, known.</summary>
    [Fact]
    public void Probe_ExistingFile_ReturnsExistsTrueIsDirectoryFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            var result = _probe.Probe(path);

            Assert.True(result.Exists);
            Assert.False(result.IsDirectory);
            Assert.True(result.IsKnown);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An existing directory probes as existing and a directory.</summary>
    [Fact]
    public void Probe_ExistingDirectory_ReturnsExistsTrueIsDirectoryTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var result = _probe.Probe(path);

            Assert.True(result.Exists);
            Assert.True(result.IsDirectory);
            Assert.True(result.IsKnown);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    /// <summary>A missing path degrades to Exists = false, matching legacy <c>File.Exists</c> semantics — the observation is still known.</summary>
    [Fact]
    public void Probe_MissingPath_ReturnsExistsFalseButKnown()
    {
        var result = _probe.Probe(Path.Combine(Path.GetTempPath(), $"path-probe-missing-{Guid.NewGuid():N}.txt"));

        Assert.False(result.Exists);
        Assert.False(result.IsDirectory);
        Assert.True(result.IsKnown);
    }

    /// <summary>An open remote root short-circuits the probe: no file-system I/O, and the result is explicitly NOT known.</summary>
    [Fact]
    public void Probe_OwnRootOpenInBreaker_ReturnsUnknownWithoutIo()
    {
        const string path = @"Z:\some\path\file.txt";
        _classifier.Setup(c => c.GetOwnRemoteRoot(path)).Returns("Z:");
        _breaker.Setup(b => b.IsOpen("Z:")).Returns(true);

        var result = _probe.Probe(path);

        Assert.False(result.Exists);
        Assert.False(result.IsDirectory);
        Assert.False(result.IsKnown);
    }

    /// <summary>An open remote root never even registers an in-flight call — <see cref="IRemoteRootCircuitBreaker.BeginCall"/> is skipped entirely once <see cref="IRemoteRootCircuitBreaker.IsOpen"/> already says no.</summary>
    [Fact]
    public void Probe_OwnRootOpenInBreaker_NeverCallsBeginCall()
    {
        const string path = @"Z:\some\path\file.txt";
        _classifier.Setup(c => c.GetOwnRemoteRoot(path)).Returns("Z:");
        _breaker.Setup(b => b.IsOpen("Z:")).Returns(true);

        _probe.Probe(path);

        _breaker.Verify(b => b.BeginCall(It.IsAny<string>()), Times.Never);
    }

    /// <summary>A remote path whose root is NOT open in the breaker still probes normally (falls through to the real metadata call).</summary>
    [Fact]
    public void Probe_OwnRootNotOpen_StillProbesNormally()
    {
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-remote-not-open-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            _classifier.Setup(c => c.GetOwnRemoteRoot(path)).Returns("Z:");
            _breaker.Setup(b => b.IsOpen("Z:")).Returns(false);

            var result = _probe.Probe(path);

            Assert.True(result.Exists);
            Assert.True(result.IsKnown);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Review fix (7c): a probe against a network root that isn't open must still register itself as an
    /// in-flight call with the breaker (<see cref="IRemoteRootCircuitBreaker.BeginCall"/>) — not just
    /// consult <see cref="IRemoteRootCircuitBreaker.IsOpen"/> — so a slow standalone liveness probe (this
    /// is exactly what <c>BackgroundItemProbeQueue</c> calls, entirely independent of
    /// <c>StaIconLoadQueue</c>'s own Remote lane) actually counts toward the breaker's slow-call tracking.
    /// </summary>
    [Fact]
    public void Probe_OwnRootNotOpen_RegistersInFlightCallWithBreaker()
    {
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-remote-registers-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            _classifier.Setup(c => c.GetOwnRemoteRoot(path)).Returns("Z:");
            _breaker.Setup(b => b.IsOpen("Z:")).Returns(false);
            var scope = new Mock<IDisposable>();
            _breaker.Setup(b => b.BeginCall("Z:")).Returns(scope.Object);

            _probe.Probe(path);

            _breaker.Verify(b => b.BeginCall("Z:"), Times.Once);
            scope.Verify(s => s.Dispose(), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A local (non-network) path never touches the breaker's <see cref="IRemoteRootCircuitBreaker.BeginCall"/> at all.</summary>
    [Fact]
    public void Probe_LocalPath_NeverCallsBeginCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-local-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            _probe.Probe(path);

            _breaker.Verify(b => b.BeginCall(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── ProbeWithinCall (M-4) ────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="PathProbe.ProbeWithinCall"/> never consults the breaker at all — no classifier lookup, no <c>IsOpen</c>, no <c>BeginCall</c>.</summary>
    [Fact]
    public void ProbeWithinCall_ExistingFile_NeverTouchesBreakerOrClassifier()
    {
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-within-call-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            var result = _probe.ProbeWithinCall(path);

            Assert.True(result.Exists);
            Assert.False(result.IsDirectory);
            Assert.True(result.IsKnown);
            _classifier.Verify(c => c.GetOwnRemoteRoot(It.IsAny<string>()), Times.Never);
            _breaker.Verify(b => b.IsOpen(It.IsAny<string>()), Times.Never);
            _breaker.Verify(b => b.BeginCall(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// M-4 regression (review it.2): with the REAL breaker, a half-open trial's own <c>BeginCall</c> scope
    /// occupies <c>Trial</c>, which makes <see cref="IRemoteRootCircuitBreaker.IsOpen"/> report the root
    /// open to anyone consulting it — including the trial call itself, if it (wrongly) called
    /// <see cref="IPathProbe.Probe"/> again from inside its own scope. <see cref="IPathProbe.ProbeWithinCall"/>
    /// must see the share as back (Exists = true), not short-circuit to false.
    /// </summary>
    [Fact]
    public void ProbeWithinCall_DuringOwnHalfOpenTrial_SeesRealResultNotBreakerShortCircuit()
    {
        var time = new ManualTimeProvider();
        var breaker = new RemoteRootCircuitBreaker(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), time);
        var path = Path.Combine(Path.GetTempPath(), $"path-probe-half-open-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Empty);
        try
        {
            const string root = @"\\srv\share";
            breaker.ReportStuck(root); // opens the root for a full quarantine
            time.Advance(TimeSpan.FromSeconds(60)); // quarantine elapses -> half-open

            var probe = new PathProbe(_classifier.Object, breaker);

            using (breaker.BeginCall(root)) // this IS the half-open trial call
            {
                // Sanity: IsOpen now reports true for anyone ELSE consulting it (Trial != null) — this is
                // exactly the state that used to make a naive re-entrant Probe() call short-circuit itself.
                Assert.True(breaker.IsOpen(root));

                var result = probe.ProbeWithinCall(path);

                Assert.True(result.Exists);
                Assert.False(result.IsDirectory);
                Assert.True(result.IsKnown);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
