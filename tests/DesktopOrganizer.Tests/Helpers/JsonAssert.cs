using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Small helper for asserting structural JSON equality (key order irrelevant, array order
/// significant) with a readable diff on failure, used by the storage/migration test suites.
/// </summary>
public static class JsonAssert
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Asserts that <paramref name="actual"/> and <paramref name="expected"/> are structurally equal
    /// per <see cref="JsonNode.DeepEquals(JsonNode?, JsonNode?)"/> — object key order does not matter,
    /// array element order does. On failure, both sides are printed indented for a readable diff.
    /// </summary>
    public static void Equal(JsonNode? expected, JsonNode? actual)
    {
        if (JsonNode.DeepEquals(expected, actual))
        {
            return;
        }

        var expectedText = expected?.ToJsonString(IndentedOptions) ?? "null";
        var actualText = actual?.ToJsonString(IndentedOptions) ?? "null";
        Assert.Fail($"JSON documents are not structurally equal.\n--- expected ---\n{expectedText}\n--- actual ---\n{actualText}");
    }
}
