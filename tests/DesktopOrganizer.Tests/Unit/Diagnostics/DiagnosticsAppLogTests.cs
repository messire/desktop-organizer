using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="DiagnosticsAppLog"/> — the production <see cref="IAppLog"/> implementation
/// (ADR-033 part 1). Uses a mocked <see cref="IDiagnosticsLog"/> sink so every assertion is about the
/// shape of the payload <see cref="DiagnosticsAppLog"/> builds, not about real file I/O (that belongs to
/// <c>JsonLinesDiagnosticsLogTests</c>).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DiagnosticsAppLogTests
{
    private readonly Mock<IDiagnosticsLog> _sinkMock = new();
    private readonly List<(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)> _writes = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsAppLogTests"/> class, wiring the mocked
    /// <see cref="IDiagnosticsLog"/> sink to record every <c>Write</c> call into <see cref="_writes"/>.
    /// </summary>
    public DiagnosticsAppLogTests()
    {
        _sinkMock
            .Setup(s => s.Write(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback<string, string, string?, KeyValuePair<string, object?>[]>(
                (category, name, scope, payload) => _writes.Add((category, name, scope, payload)));
    }

    // ── Category / scope ──────────────────────────────────────────────────────

    /// <summary>Every event is written under the fixed <c>"app"</c> category with a <see langword="null"/> scope.</summary>
    [Fact]
    public void Info_WritesUnderAppCategory_WithNullScope()
    {
        CreateSut().Info("MySource", "MyEvent");

        var write = Assert.Single(_writes);
        Assert.Equal("app", write.Category);
        Assert.Equal("MyEvent", write.Name);
        Assert.Null(write.Scope);
    }

    // ── level field ────────────────────────────────────────────────────────────

    /// <summary>Each of <see cref="DiagnosticsAppLog.Info"/>/<see cref="DiagnosticsAppLog.Warn"/>/
    /// <see cref="DiagnosticsAppLog.Error"/> writes a matching <c>level</c> field.</summary>
    /// <param name="expectedLevel">The <c>level</c> value expected for the method under test.</param>
    [Theory]
    [InlineData("info")]
    [InlineData("warn")]
    [InlineData("error")]
    public void Write_IncludesLevelField_MatchingTheCalledMethod(string expectedLevel)
    {
        var sut = CreateSut();

        switch (expectedLevel)
        {
            case "info": sut.Info("Source", "Evt"); break;
            case "warn": sut.Warn("Source", "Evt"); break;
            case "error": sut.Error("Source", "Evt", null); break;
        }

        var write = Assert.Single(_writes);
        Assert.Equal(expectedLevel, Value(write.Payload, "level"));
        Assert.Equal("Source", Value(write.Payload, "source"));
    }

    // ── Exception fields (Error only) ───────────────────────────────────────────

    /// <summary><see cref="DiagnosticsAppLog.Error"/> with a real exception includes exType/hresult/msg/stack.</summary>
    [Fact]
    public void Error_WithException_IncludesExTypeHResultMsgAndStack()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        CreateSut().Error("Source", "Evt", ex);

        var write = Assert.Single(_writes);
        Assert.Equal(nameof(InvalidOperationException), Value(write.Payload, "exType"));
        Assert.Equal(ex.HResult, Value(write.Payload, "hresult"));
        Assert.Equal("boom", Value(write.Payload, "msg"));
        Assert.True(HasKey(write.Payload, "stack"));
        Assert.NotNull(Value(write.Payload, "stack"));
    }

    /// <summary><see cref="DiagnosticsAppLog.Error"/> with a <see langword="null"/> exception carries no
    /// exception-shaped fields at all.</summary>
    [Fact]
    public void Error_WithNullException_HasNoExceptionFields()
    {
        CreateSut().Error("Source", "Evt", null);

        var write = Assert.Single(_writes);
        Assert.False(HasKey(write.Payload, "exType"));
        Assert.False(HasKey(write.Payload, "hresult"));
        Assert.False(HasKey(write.Payload, "msg"));
        Assert.False(HasKey(write.Payload, "stack"));
    }

    /// <summary>Neither <see cref="DiagnosticsAppLog.Info"/> nor <see cref="DiagnosticsAppLog.Warn"/> ever
    /// carries a <c>stack</c> field — it is exclusive to <see cref="DiagnosticsAppLog.Error"/>.</summary>
    [Fact]
    public void InfoAndWarn_NeverIncludeStackField()
    {
        var sut = CreateSut();
        sut.Info("Source", "InfoEvt");
        sut.Warn("Source", "WarnEvt");

        Assert.All(_writes, write => Assert.False(HasKey(write.Payload, "stack")));
    }

    // ── Scrubbing ────────────────────────────────────────────────────────────

    /// <summary>A path embedded in the exception message is scrubbed before being written.</summary>
    [Fact]
    public void Error_ExceptionMessageContainingPath_IsScrubbed()
    {
        var ex = new InvalidOperationException(@"failed to read C:\Users\alice\report.docx");

        CreateSut().Error("Source", "Evt", ex);

        var write = Assert.Single(_writes);
        var msg = Assert.IsType<string>(Value(write.Payload, "msg"));
        Assert.DoesNotContain("alice", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path:.docx>", msg);
    }

    /// <summary>A string value passed in <c>data</c> is scrubbed the same way the exception message is.</summary>
    [Fact]
    public void Info_StringDataValueContainingPath_IsScrubbed()
    {
        CreateSut().Info("Source", "Evt", new KeyValuePair<string, object?>("path", @"D:\Users\bob\file.txt"));

        var write = Assert.Single(_writes);
        var value = Assert.IsType<string>(Value(write.Payload, "path"));
        Assert.Equal("<path:.txt>", value);
    }

    /// <summary>A non-string value passed in <c>data</c> (e.g. an <see langword="int"/>) is written through
    /// unmodified — <see cref="LogScrubber.Scrub(string?)"/> only ever applies to strings.</summary>
    [Fact]
    public void Info_NonStringDataValue_IsNotAltered()
    {
        CreateSut().Info("Source", "Evt", new KeyValuePair<string, object?>("count", 42));

        var write = Assert.Single(_writes);
        Assert.Equal(42, Value(write.Payload, "count"));
    }

    // ── Never throws ─────────────────────────────────────────────────────────

    /// <summary>A sink that throws on <see cref="IDiagnosticsLog.Write"/> must never let that exception
    /// escape any of the three <see cref="IAppLog"/> members.</summary>
    [Fact]
    public void AllMembers_SinkThrows_NeverThrowsBackToCaller()
    {
        var throwingSink = new Mock<IDiagnosticsLog>();
        throwingSink
            .Setup(s => s.Write(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<KeyValuePair<string, object?>[]>()))
            .Throws(new InvalidOperationException("sink failed"));
        var sut = new DiagnosticsAppLog(() => throwingSink.Object);

        var infoEx = Record.Exception(() => sut.Info("Source", "Evt"));
        var warnEx = Record.Exception(() => sut.Warn("Source", "Evt"));
        var errorEx = Record.Exception(() => sut.Error("Source", "Evt", new InvalidOperationException("boom")));

        Assert.Null(infoEx);
        Assert.Null(warnEx);
        Assert.Null(errorEx);
    }

    /// <summary>A sink factory that itself throws (e.g. a disposed lazy sink) is also swallowed.</summary>
    [Fact]
    public void Write_SinkFactoryThrows_NeverThrowsBackToCaller()
    {
        var sut = new DiagnosticsAppLog(() => throw new ObjectDisposedException("sink"));

        var exception = Record.Exception(() => sut.Info("Source", "Evt"));

        Assert.Null(exception);
    }

    // ── Argument validation ───────────────────────────────────────────────────

    /// <summary>The constructor rejects a <see langword="null"/> sink factory.</summary>
    [Fact]
    public void Constructor_NullSinkFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DiagnosticsAppLog(null!));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static object? Value(KeyValuePair<string, object?>[] payload, string key) =>
        payload.Single(p => p.Key == key).Value;

    private static bool HasKey(KeyValuePair<string, object?>[] payload, string key) =>
        payload.Any(p => p.Key == key);

    private DiagnosticsAppLog CreateSut() => new(() => _sinkMock.Object);
}
