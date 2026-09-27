namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Bridges WinForms' own unhandled-exception channel into this application's crash-handling pipeline
/// (ADR-033 part 2, release-hardening plan §2). Without this, an exception thrown from a
/// <see cref="System.Windows.Forms.NotifyIcon"/> callback (the tray icon — the only WinForms control
/// this application uses) would be caught by <see cref="System.Windows.Forms.NativeWindow"/>'s own
/// internal handling and shown via WinForms' own <c>ThreadExceptionDialog</c> instead of this
/// application's crash sequence — even though there is no <see cref="System.Windows.Forms.Application.Run()"/>
/// call anywhere in this (WPF) process for that default to normally apply to.
/// </summary>
/// <remarks>
/// <see cref="Install(Action{Exception})"/> is the one production entry point, called once from
/// <c>Program.Main</c>, as early as possible (plan §2). The internal overload lets tests exercise the
/// exact same wiring logic against a fake <see cref="IThreadExceptionSource"/> instead of the real
/// static <see cref="System.Windows.Forms.Application"/> class, which cannot be driven from a unit test
/// without a live WinForms message loop.
/// </remarks>
public static class WinFormsExceptionBridge
{
    /// <summary>
    /// Installs <paramref name="handler"/> as the target of every future WinForms
    /// <see cref="System.Windows.Forms.Application.ThreadException"/> event, having first switched
    /// WinForms into catch-and-forward mode (<see cref="System.Windows.Forms.UnhandledExceptionMode.CatchException"/>)
    /// so the exception reaches this handler instead of WinForms' own default dialog.
    /// </summary>
    /// <param name="handler">Invoked with the caught exception, on whichever thread raised it (the UI thread, for this application's only WinForms control).</param>
    public static void Install(Action<Exception> handler) =>
        Install(handler, RealThreadExceptionSource.Instance);

    /// <summary>
    /// Test-only overload: wires <paramref name="handler"/> through <paramref name="source"/> instead of
    /// the real <see cref="System.Windows.Forms.Application"/> static class.
    /// </summary>
    /// <param name="handler">See <see cref="Install(Action{Exception})"/>.</param>
    /// <param name="source">The (real or fake) thread-exception source to wire through.</param>
    internal static void Install(Action<Exception> handler, IThreadExceptionSource source)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(source);

        source.SetCatchExceptionMode();
        source.Subscribe(handler);
    }
}
