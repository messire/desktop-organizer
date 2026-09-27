using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="WinFormsExceptionBridge"/> (ADR-033 part 2) — driven against a fake
/// <see cref="IThreadExceptionSource"/> rather than the real, static
/// <see cref="System.Windows.Forms.Application"/> class, which cannot be exercised from a unit test
/// without a live WinForms message loop.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class WinFormsExceptionBridgeTests
{
    /// <summary><see cref="WinFormsExceptionBridge.Install(Action{Exception}, IThreadExceptionSource)"/> switches the source into catch-and-forward mode.</summary>
    [Fact]
    public void Install_SwitchesToCatchExceptionMode()
    {
        var source = new FakeThreadExceptionSource();

        WinFormsExceptionBridge.Install(_ => { }, source);

        Assert.True(source.CatchExceptionModeSet);
    }

    /// <summary>An exception raised by the source reaches the installed handler unchanged.</summary>
    [Fact]
    public void Install_ForwardsRaisedExceptionToHandler()
    {
        var source = new FakeThreadExceptionSource();
        Exception? received = null;

        WinFormsExceptionBridge.Install(ex => received = ex, source);

        var thrown = new InvalidOperationException("boom");
        source.Raise(thrown);

        Assert.Same(thrown, received);
    }

    /// <summary>A <see langword="null"/> handler is rejected.</summary>
    [Fact]
    public void Install_NullHandler_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WinFormsExceptionBridge.Install(null!, new FakeThreadExceptionSource()));
    }

    /// <summary>A <see langword="null"/> source is rejected.</summary>
    [Fact]
    public void Install_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WinFormsExceptionBridge.Install(_ => { }, null!));
    }

    private sealed class FakeThreadExceptionSource : IThreadExceptionSource
    {
        private Action<Exception>? _handler;

        public bool CatchExceptionModeSet { get; private set; }

        public void SetCatchExceptionMode() => CatchExceptionModeSet = true;

        public void Subscribe(Action<Exception> handler) => _handler = handler;

        public void Raise(Exception exception) => _handler?.Invoke(exception);
    }
}
