namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Creates <see cref="IStaWorker"/> instances (ADR-046 D-1/D-4) — a seam so
/// <c>StaIconLoadQueue</c>'s watchdog can create a replacement worker after abandoning a stuck one, and so
/// tests can substitute a fake worker without spinning up a real STA thread.
/// </summary>
internal interface IStaWorkerFactory
{
    /// <summary>
    /// Creates a new, ready-to-use STA worker.
    /// </summary>
    /// <param name="name">
    /// The thread's name (e.g. <c>"DO.IconWorker.Local#2"</c>) — helps identify it in a debugger or crash
    /// dump; carries no other meaning.
    /// </param>
    /// <returns>The new worker.</returns>
    IStaWorker Create(string name);
}
