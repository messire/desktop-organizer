namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// A single structured diagnostics record, written as one JSON object per line (JSONL) by
/// <see cref="IDiagnosticsLog"/> implementations.
/// </summary>
/// <remarks>
/// <b>Privacy invariant:</b> instances of this type must never carry panel titles, file names, or
/// file paths — only opaque identifiers (e.g. an 8-character panel id prefix), geometry, and
/// enum/bool state. Diagnostics files are meant to be safe to hand to a tester without exposing
/// anything personal about the machine they were captured on.
/// </remarks>
/// <param name="Timestamp">Local wall-clock time the event was recorded.</param>
/// <param name="Sequence">Monotonically increasing, per-process sequence number.</param>
/// <param name="Category">Coarse grouping, e.g. <c>"session"</c> or <c>"gesture"</c>.</param>
/// <param name="Name">The specific event name, e.g. <c>"ResizeStarted"</c>.</param>
/// <param name="Scope">
/// An 8-character opaque prefix identifying the panel the event relates to, or
/// <see langword="null"/> for session-level events that aren't tied to a single panel.
/// </param>
/// <param name="Payload">
/// Additional structured fields for this event. Values must be limited to privacy-safe primitives
/// (numbers, booleans, short opaque strings, enum names) — see the type-level remarks.
/// </param>
public readonly record struct DiagnosticEvent(
    DateTimeOffset Timestamp,
    long Sequence,
    string Category,
    string Name,
    string? Scope,
    IReadOnlyList<KeyValuePair<string, object?>> Payload);
