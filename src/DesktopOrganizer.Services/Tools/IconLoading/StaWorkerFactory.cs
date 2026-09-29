using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Production <see cref="IStaWorkerFactory"/> (ADR-046 D-1/D-4): every call starts a new
/// <see cref="StaWorkerThread"/>. Non-blocking — the thread comes up asynchronously and buffers work posted
/// before its pump is ready, so <see cref="StaIconLoadQueue"/> may call this under its own lock and from
/// the UI thread (lazy lane start on the first <c>Submit</c>).
/// </summary>
internal sealed class StaWorkerFactory : IStaWorkerFactory
{
    /// <inheritdoc />
    public IStaWorker Create(string name) => new StaWorkerThread(name);
}
