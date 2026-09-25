using System.IO;
using System.Text;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Serializes a <see cref="DiagnosticEvent"/> to a single-line JSON object (no trailing newline),
/// UTF-8, no BOM. Hand-rolled with <see cref="Utf8JsonWriter"/> rather than reflection-based
/// <c>JsonSerializer.Serialize</c> so the exact shape of each line — and the small, closed set of
/// payload value types that are allowed onto disk — stays fully under control.
/// </summary>
internal static class DiagnosticEventJsonWriter
{
    /// <summary>
    /// Serializes <paramref name="diagnosticEvent"/> to a single JSONL line.
    /// </summary>
    /// <param name="diagnosticEvent">The event to serialize.</param>
    /// <returns>A single-line, UTF-8 JSON representation of <paramref name="diagnosticEvent"/>, with no trailing newline.</returns>
    public static string ToJsonLine(in DiagnosticEvent diagnosticEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", diagnosticEvent.Timestamp);
            writer.WriteNumber("seq", diagnosticEvent.Sequence);
            writer.WriteString("category", diagnosticEvent.Category);
            writer.WriteString("name", diagnosticEvent.Name);

            if (diagnosticEvent.Scope is null) writer.WriteNull("scope");
            else writer.WriteString("scope", diagnosticEvent.Scope);

            writer.WriteStartObject("payload");
            foreach (var field in diagnosticEvent.Payload)
                WriteValue(writer, field.Key, field.Value);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        // Utf8JsonWriter never emits a BOM; Encoding.UTF8.GetString here just decodes the raw bytes
        // it wrote, so the returned string round-trips to UTF-8-without-BOM when written to file.
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a single payload field, restricted to privacy-safe primitive shapes (see
    /// <see cref="DiagnosticEvent"/> remarks). Anything outside this closed set falls back to the
    /// value's runtime type name — never <see cref="object.ToString"/> — so this closed set is
    /// enforced mechanically: an arbitrary future call site that accidentally passes a rich object
    /// (e.g. one whose <c>ToString()</c> happens to include a file path) can never leak its contents
    /// onto disk, only its type name (see M-4 / the privacy invariant in the type remarks).
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case int i:
                writer.WriteNumber(name, i);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case double d:
                // Utf8JsonWriter.WriteNumber throws ArgumentException for NaN/Infinity, which are
                // legitimate values for gesture geometry math (e.g. a divide-by-zero ratio); write
                // null instead of letting that exception escape the serializer.
                if (double.IsFinite(d)) writer.WriteNumber(name, d);
                else writer.WriteNull(name);
                break;
            case float f:
                if (float.IsFinite(f)) writer.WriteNumber(name, f);
                else writer.WriteNull(name);
                break;
            case Guid g:
                writer.WriteString(name, g);
                break;
            case DateTimeOffset dto:
                writer.WriteString(name, dto);
                break;
            case Enum e:
                writer.WriteString(name, e.ToString());
                break;
            default:
                writer.WriteString(name, value.GetType().Name);
                break;
        }
    }
}
