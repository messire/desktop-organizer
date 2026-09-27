namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// The real <see cref="IThreadExceptionSource"/>, backed by <see cref="System.Windows.Forms.Application"/>.
/// </summary>
internal sealed class RealThreadExceptionSource : IThreadExceptionSource
{
    /// <summary>The single shared instance — this wraps a process-wide static class, so there is never more than one.</summary>
    internal static readonly RealThreadExceptionSource Instance = new();

    private RealThreadExceptionSource()
    {
    }

    /// <inheritdoc />
    public void SetCatchExceptionMode() =>
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);

    /// <inheritdoc />
    public void Subscribe(Action<Exception> handler) =>
        System.Windows.Forms.Application.ThreadException += (_, e) => handler(e.Exception);
}
