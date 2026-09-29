using System.Windows.Media;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Result of one <see cref="IconLoadRequest"/>, delivered to an <see cref="IIconLoadGroup"/>'s
/// <c>onResults</c> callback (ADR-046). A single request can produce more than one result over time — an
/// <see cref="IconLoadOutcome.Interim"/> type icon followed by the real, terminal outcome.
/// </summary>
/// <param name="Path">Same as the originating <see cref="IconLoadRequest.Path"/>.</param>
/// <param name="PixelSize">The snapped pixel size the icon was fetched for (<see cref="IconPixelSize.Snap"/>).</param>
/// <param name="Generation">
/// The group's <see cref="IIconLoadGroup.Generation"/> at the time the originating request was submitted.
/// A caller that keeps its own generation counter (e.g. after a full rebuild) can use this to recognize —
/// and discard — a result that raced a <see cref="IIconLoadGroup.CancelPending"/> the queue itself already
/// accounted for, though the queue never delivers a result for a generation it has already cancelled.
/// </param>
/// <param name="Outcome">How the request was resolved.</param>
/// <param name="Icon">Frozen (C-4); non-null only for <see cref="IconLoadOutcome.Loaded"/>/<see cref="IconLoadOutcome.Interim"/>.</param>
/// <param name="Exists">
/// Whether the item existed at probe time, or <see langword="null"/> when unknown (<see cref="IconLoadOutcome.Interim"/>,
/// watchdog timeout, <see cref="IconLoadOutcome.Failed"/>, <see cref="IconLoadOutcome.Dropped"/>).
/// </param>
/// <param name="IsDirectory">Whether the item is a directory, or <see langword="null"/> when unknown (see <paramref name="Exists"/>).</param>
public readonly record struct IconLoadResult(
    string Path,
    int PixelSize,
    int Generation,
    IconLoadOutcome Outcome,
    ImageSource? Icon,
    bool? Exists,
    bool? IsDirectory);
