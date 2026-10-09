namespace DesktopOrganizer.UI.Models;

/// <summary>
/// An enum-valued persisted field whose on-disk string this build did not recognise (typically a member added by
/// a newer additive schema revision, ADR-024 Amendment "Additive revisions"). The live model holds
/// <see cref="Fallback"/> (the documented default); <see cref="Raw"/> is what the file said and is written back
/// on save as long as the user did not change the field.
/// </summary>
/// <param name="Raw">The unrecognised on-disk string, verbatim.</param>
/// <param name="Fallback">The name of the member the model fell back to when reading <paramref name="Raw"/>.</param>
public sealed record PreservedEnumValue(string Raw, string Fallback);
