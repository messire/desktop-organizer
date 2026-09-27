namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// The seam <see cref="WinFormsExceptionBridge"/> wires through — abstracts the real, static
/// <see cref="System.Windows.Forms.Application"/> class so <c>WinFormsExceptionBridgeTests</c> can drive
/// it with a fake implementation instead of a live WinForms message loop.
/// </summary>
internal interface IThreadExceptionSource
{
    /// <summary>Switches WinForms into catch-and-forward mode for unhandled exceptions raised from a WinForms control's callback.</summary>
    void SetCatchExceptionMode();

    /// <summary>Registers <paramref name="handler"/> to be invoked with every future caught exception.</summary>
    /// <param name="handler">The handler to invoke.</param>
    void Subscribe(Action<Exception> handler);
}
