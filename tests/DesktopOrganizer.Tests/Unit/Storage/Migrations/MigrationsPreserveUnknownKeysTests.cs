using System.IO;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Guards against repeating <c>V4ToV5Migration</c>'s allow-list mistake (F4 of
/// docs/PLANS/schema-additive-revision-v7.md): every migration from schema v6 onward must keep keys it does not know
/// about, at every level of the document, because those are exactly the fields a newer additive revision wrote.
/// A migration that legitimately reshapes some key must be given its own expectation here.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class MigrationsPreserveUnknownKeysTests
{
    private const string Marker = "zzUnknownMarker";

    /// <summary>All registered migrations whose source version is 6 or higher.</summary>
    /// <returns>One theory row per migration.</returns>
    public static IEnumerable<object[]> MigrationsFromV6Onward() =>
        typeof(V6ToV7Migration).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISettingsMigration).IsAssignableFrom(t))
            .Select(t => (ISettingsMigration)Activator.CreateInstance(t)!)
            .Where(m => m.FromVersion >= 6)
            .OrderBy(m => m.FromVersion)
            .Select(m => new object[] { m.GetType().Name });

    /// <summary>
    /// Verifies that an unknown marker key injected into every object of a v6 document (root, panels and their
    /// geometry/appearance/content, pages and their items, snapshots and their panels, system icons panel and its
    /// items) is still present at the same path after the migration.
    /// </summary>
    /// <param name="migrationTypeName">The migration under test.</param>
    [Theory]
    [MemberData(nameof(MigrationsFromV6Onward))]
    public void Migration_PreservesUnknownKeysAtEveryLevel(string migrationTypeName)
    {
        var migration = (ISettingsMigration)Activator.CreateInstance(
            typeof(V6ToV7Migration).Assembly.GetTypes().Single(t => t.Name == migrationTypeName))!;
        var input = StateEnvelope.StripEnvelope(JsonNode.Parse(File.ReadAllText(Path.Combine("Resources", "Storage", "golden", "v6-full.json")))!.AsObject());
        var markerPaths = new List<string>();
        InjectMarkers(input, string.Empty, markerPaths);
        Assert.True(markerPaths.Count >= 10, "The fixture must exercise at least the ten DTO levels.");

        var output = migration.Migrate(input);

        foreach (var path in markerPaths)
        {
            Assert.True(TryResolve(output, path, out var value), $"Unknown key lost at '{path}' by {migrationTypeName}.");
            Assert.Equal(Marker, value);
        }
    }

    private static void InjectMarkers(JsonNode? node, string path, List<string> markerPaths)
    {
        switch (node)
        {
            case JsonObject obj:
                obj[Marker] = Marker;
                markerPaths.Add(path.Length == 0 ? Marker : $"{path}/{Marker}");
                foreach (var (key, value) in obj.Where(kv => kv.Key != Marker).ToList())
                {
                    InjectMarkers(value, path.Length == 0 ? key : $"{path}/{key}", markerPaths);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    InjectMarkers(array[i], $"{path}/{i}", markerPaths);
                }

                break;
        }
    }

    private static bool TryResolve(JsonNode root, string path, out string? value)
    {
        value = null;
        JsonNode? current = root;
        foreach (var segment in path.Split('/'))
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index < array.Count => array[index],
                _ => null,
            };

            if (current is null)
            {
                return false;
            }
        }

        value = current.GetValue<string>();
        return true;
    }
}
