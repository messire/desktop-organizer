using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;

namespace DesktopOrganizer.Tests.Unit.State;

/// <summary>
/// Forward-compatibility tests for <see cref="ApplicationStateService"/> (ADR-024 Amendment "Additive revisions",
/// docs/PLANS/schema-additive-revision-v7.md §2–§3): unknown fields written by a newer revision survive a
/// load → save cycle at every level of the document, and an unknown enum string degrades to the default instead of
/// quarantining the document — and is written back while the user has not changed the field.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ApplicationStateServiceForwardCompatTests
{
    private readonly Mock<IApplicationStateStore> _storeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IPageManager> _pageManagerMock = new();
    private readonly Mock<ILayoutSnapshotService> _layoutSnapshotServiceMock = new();
    private readonly Mock<ISystemIconsPanelManager> _systemIconsPanelManagerMock = new();
    private readonly FakeDiagnosticsLog _diagnostics = new();
    private readonly List<JsonObject> _saved = [];

    // ── The main round trip ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies the central guarantee: a document carrying unknown keys at all ten DTO levels (root, panel and its
    /// geometry/appearance/content, pages section and page item, snapshot and its panels, system icons panel and its
    /// items — including an item of an unknown kind), unknown enum values and a higher revision, loads without
    /// quarantine and every key and value is back in the document produced by the following save.
    /// </summary>
    [Fact]
    public void UnknownFields_LoadThenSave_AllPreserved()
    {
        var input = LoadPayload("v7-unknown-fields.json");
        var sut = LoadAndWire(input);

        sut.SaveNow();

        var saved = Assert.Single(_saved);
        AssertSubset(input, saved, string.Empty);
        _storeMock.Verify(s => s.QuarantineLoadedDocument(It.IsAny<string>()), Times.Never);
        Assert.NotEqual(StateLoadStatus.Quarantined, sut.LastLoadStatus);
    }

    /// <summary>
    /// Verifies that unknown values of every enum field degrade to the documented default on the live model
    /// (no throw, no quarantine) and are written back verbatim by the next save.
    /// </summary>
    [Fact]
    public void UnknownEnum_FallsBackToDefault_NotQuarantined_RawWrittenBack()
    {
        var input = LoadPayload("v7-unknown-fields.json");
        var sut = LoadAndWire(input);

        var panel = sut.Panels[2];
        Assert.Equal(System.Windows.WindowState.Normal, panel.WindowState);
        Assert.Equal(PanelGeometryMode.LegacyFreeform, panel.GeometryMode);
        Assert.Equal(PanelSizingMode.Manual, panel.SizingMode);
        Assert.Equal(PanelSurfaceStyle.Ambient, panel.SurfaceStyle);
        Assert.Equal(IconLayoutPolicy.LegacyStretch, panel.IconLayoutPolicy);
        Assert.Equal(PanelViewMode.List, panel.ViewMode);
        Assert.Equal(PanelContentFilter.ShowAll, panel.ContentFilter);
        Assert.Equal(PanelSortMode.Manual, panel.SortMode);
        Assert.Equal(SystemIconSize.Medium, _systemIconsPanelManagerMock.Object.Model.IconSize);
        _storeMock.Verify(s => s.QuarantineLoadedDocument(It.IsAny<string>()), Times.Never);

        sut.SaveNow();

        var savedPanel = _saved.Single()["panels"]![2]!;
        Assert.Equal("Floating", savedPanel["geometry"]!["windowState"]!.GetValue<string>());
        Assert.Equal("Masonry", savedPanel["geometry"]!["geometryMode"]!.GetValue<string>());
        Assert.Equal("AutoWidth", savedPanel["geometry"]!["sizingMode"]!.GetValue<string>());
        Assert.Equal("Mica", savedPanel["appearance"]!["surfaceStyle"]!.GetValue<string>());
        Assert.Equal("Packed", savedPanel["appearance"]!["iconLayoutPolicy"]!.GetValue<string>());
        Assert.Equal("Tiles", savedPanel["content"]!["viewMode"]!.GetValue<string>());
        Assert.Equal("ImagesOnly", savedPanel["content"]!["contentFilter"]!.GetValue<string>());
        Assert.Equal("Newest", savedPanel["content"]!["sortMode"]!.GetValue<string>());
        Assert.Equal("Gigantic", _saved.Single()["systemIconsPanel"]!["iconSize"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a field the user changed after loading is written with the new value, not the preserved raw
    /// one, while the untouched unknown values next to it are still written back.
    /// </summary>
    [Fact]
    public void UnknownEnum_UserChangedValue_WritesNewValue()
    {
        var sut = LoadAndWire(LoadPayload("v7-unknown-fields.json"));
        var panel = sut.Panels[2];

        panel.SurfaceStyle = PanelSurfaceStyle.Glass;
        _systemIconsPanelManagerMock.Object.Model.IconSize = SystemIconSize.Large;
        sut.SaveNow();

        var saved = _saved.Single();
        Assert.Equal("Glass", saved["panels"]![2]!["appearance"]!["surfaceStyle"]!.GetValue<string>());
        Assert.Equal("Large", saved["systemIconsPanel"]!["iconSize"]!.GetValue<string>());
        Assert.Equal("Tiles", saved["panels"]![2]!["content"]!["viewMode"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a JSON <em>number</em> in an enum field is still treated as corruption (the type does not match a
    /// string): the document is quarantined as before, exactly like the legacy behaviour.
    /// </summary>
    [Fact]
    public void NumericEnum_StillQuarantines()
    {
        var payload = PanelPayload("""{ "windowState": 2 }""", "{}", "{}");

        var sut = Load(payload);

        Assert.Equal(StateLoadStatus.Quarantined, sut.LastLoadStatus);
        Assert.Empty(sut.Panels);
        _storeMock.Verify(s => s.QuarantineLoadedDocument("PayloadDeserializationFailed"), Times.Once);
    }

    /// <summary>
    /// Verifies that a numeric <em>string</em> in an enum field is rejected as an unknown value (the meaning of the
    /// old <c>allowIntegerValues: false</c> is kept): default on the model, raw string written back.
    /// </summary>
    [Fact]
    public void NumericString_TreatedAsUnknown()
    {
        var payload = PanelPayload("""{ "windowState": "2" }""", """{ "surfaceStyle": "1" }""", "{}");

        var sut = LoadAndWire(payload);

        Assert.Equal(System.Windows.WindowState.Normal, sut.Panels[0].WindowState);
        Assert.Equal(PanelSurfaceStyle.Ambient, sut.Panels[0].SurfaceStyle);
        sut.SaveNow();
        var panel = _saved.Single()["panels"]![0]!;
        Assert.Equal("2", panel["geometry"]!["windowState"]!.GetValue<string>());
        Assert.Equal("1", panel["appearance"]!["surfaceStyle"]!.GetValue<string>());
        _storeMock.Verify(s => s.QuarantineLoadedDocument(It.IsAny<string>()), Times.Never);
    }

    // ── Serialization facts (plan §2.4) ──────────────────────────────────────

    /// <summary>
    /// Verifies that extension-data keys are written verbatim (no naming policy: a PascalCase twin of a known
    /// camelCase key is kept next to it), JSON nulls inside extras are kept, and the saved document contains no
    /// duplicate keys (an unknown enum raw goes through the property, never through extras).
    /// </summary>
    [Fact]
    public void Extras_KeysWrittenVerbatim_NullsPreserved_NoDuplicateKeys()
    {
        var payload = JsonNode.Parse("""
            {
              "panels": [{
                "id": "11111111-1111-1111-1111-111111111111",
                "title": "T",
                "Title": "pascal-twin",
                "pageId": "default",
                "nullable": null,
                "geometry": { "windowState": "Weird", "x": 1 },
                "appearance": { "surfaceStyle": "Mica" },
                "content": {}
              }]
            }
            """)!.AsObject();
        var sut = LoadAndWire(payload);

        sut.SaveNow();

        var json = _saved.Single().ToJsonString();
        var reparsed = JsonNode.Parse(json)!.AsObject(); // throws on duplicate keys
        var panel = reparsed["panels"]![0]!.AsObject();
        Assert.Equal("T", panel["title"]!.GetValue<string>());
        Assert.Equal("pascal-twin", panel["Title"]!.GetValue<string>());
        Assert.True(panel.ContainsKey("nullable"));
        Assert.Null(panel["nullable"]);
        Assert.Equal(1, CountOccurrences(json, "\"windowState\""));
        Assert.Equal(1, CountOccurrences(json, "\"surfaceStyle\""));
        Assert.Equal("Weird", panel["geometry"]!["windowState"]!.GetValue<string>());
    }

    /// <summary>Verifies that a document without any unknown field produces no forward-compat diagnostics event.</summary>
    [Fact]
    public void Load_NoUnknownFields_WritesNoForwardCompatEvent()
    {
        Load(LoadPayload("v7-r0-full.json"));

        Assert.DoesNotContain(_diagnostics.Events, e => e.Name == "StateForwardCompatFieldsPreserved");
    }

    /// <summary>
    /// Verifies that a load carrying unknown fields writes exactly one <c>StateForwardCompatFieldsPreserved</c> event
    /// whose payload is counters only (no key names, no raw values).
    /// </summary>
    [Fact]
    public void Load_UnknownFields_WritesOneCountersOnlyEvent()
    {
        Load(LoadPayload("v7-unknown-fields.json"));

        var evt = Assert.Single(_diagnostics.Events, e => e.Name == "StateForwardCompatFieldsPreserved");
        Assert.Equal("state", evt.Category);
        Assert.Equal(
            ["unknownKeyCount", "unknownEnumCount", "unknownSystemIconItemCount"],
            evt.Payload.Select(p => p.Key));
        Assert.All(evt.Payload, p => Assert.IsType<int>(p.Value));
        Assert.True((int)evt.Payload.Single(p => p.Key == "unknownKeyCount").Value! > 0);
        Assert.Equal(10, (int)evt.Payload.Single(p => p.Key == "unknownEnumCount").Value!);
        Assert.Equal(1, (int)evt.Payload.Single(p => p.Key == "unknownSystemIconItemCount").Value!);
        var text = string.Join('|', evt.Payload.Select(p => $"{p.Key}={p.Value}"));
        Assert.DoesNotContain("Mica", text);
        Assert.DoesNotContain("future", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Deleted entities ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that extras of a page that no longer exists are simply not written (and extras of the remaining
    /// page still are).
    /// </summary>
    [Fact]
    public void PageExtras_FollowThePageById_DeletedPageDropsItsExtras()
    {
        var payload = LoadPayload("v7-unknown-fields.json");
        var sut = LoadAndWire(payload);
        var remaining = sut.Pages.Where(p => p.Id != "work").ToList();
        _pageManagerMock.SetupGet(p => p.PageDescriptors).Returns(remaining);

        sut.SaveNow();

        var items = _saved.Single()["pages"]!["items"]!.AsArray();
        Assert.DoesNotContain(items, i => i!["id"]!.GetValue<string>() == "work");
        Assert.DoesNotContain("futurePage", _saved.Single()["pages"]!["items"]!.ToJsonString());
        Assert.NotNull(_saved.Single()["pages"]!["futurePages"]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static JsonObject LoadPayload(string fixtureName)
    {
        var path = Path.Combine("Resources", "Storage", "golden", fixtureName);
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var key in new[] { "schemaVersion", "schemaRevision", "writtenAtUtc", "appVersion" })
        {
            document.Remove(key);
        }

        return document;
    }

    private static JsonObject PanelPayload(string geometry, string appearance, string content) =>
        JsonNode.Parse($$"""
            { "panels": [{ "id": "22222222-2222-2222-2222-222222222222", "title": "P", "pageId": "default",
              "geometry": {{geometry}}, "appearance": {{appearance}}, "content": {{content}} }] }
            """)!.AsObject();

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Asserts that every key of <paramref name="expected"/> exists in <paramref name="actual"/> with an equal value.
    /// Known properties the writer omits when null are tolerated; unknown ones (names starting with "future") are not.
    /// Arrays compare by index, except the system icons items, which compare as a set (the build re-adds its own
    /// default items).
    /// </summary>
    private static void AssertSubset(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case JsonObject expectedObject:
                var actualObject = Assert.IsAssignableFrom<JsonObject>(actual);
                foreach (var (key, expectedValue) in expectedObject)
                {
                    var childPath = path.Length == 0 ? key : $"{path}.{key}";
                    if (!actualObject.TryGetPropertyValue(key, out var actualValue))
                    {
                        Assert.True(
                            expectedValue is null && !key.StartsWith("future", StringComparison.Ordinal),
                            $"Key lost on save: {childPath}");
                        continue;
                    }

                    AssertSubset(expectedValue, actualValue, childPath);
                }

                break;
            case JsonArray expectedArray:
                var actualArray = Assert.IsAssignableFrom<JsonArray>(actual);
                if (path == "systemIconsPanel.items")
                {
                    foreach (var expectedItem in expectedArray)
                    {
                        Assert.Contains(actualArray, candidate => IsSubset(expectedItem, candidate));
                    }
                }
                else
                {
                    Assert.Equal(expectedArray.Count, actualArray.Count);
                    for (var i = 0; i < expectedArray.Count; i++)
                    {
                        AssertSubset(expectedArray[i], actualArray[i], $"{path}[{i}]");
                    }
                }

                break;
            case JsonValue expectedValueNode:
                Assert.NotNull(actual);
                Assert.True(ValuesEqual(expectedValueNode, actual), $"Value differs at {path}: expected {expectedValueNode.ToJsonString()}, actual {actual.ToJsonString()}");
                break;
            default:
                Assert.Null(actual);
                break;
        }
    }

    private static bool IsSubset(JsonNode? expected, JsonNode? actual)
    {
        try
        {
            AssertSubset(expected, actual, "systemIconsPanel.items.item");
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
    }

    private static bool ValuesEqual(JsonValue expected, JsonNode actual)
    {
        var e = JsonSerializer.SerializeToElement(expected);
        var a = JsonSerializer.SerializeToElement(actual);
        if (e.ValueKind == JsonValueKind.Number && a.ValueKind == JsonValueKind.Number)
        {
            return e.GetDouble().Equals(a.GetDouble());
        }

        // Known timestamps are normalised by the DTO round trip (7 fractional digits vs. none); compare as instants.
        if (e.ValueKind == JsonValueKind.String && a.ValueKind == JsonValueKind.String
            && e.TryGetDateTime(out var expectedTime) && a.TryGetDateTime(out var actualTime))
        {
            return expectedTime == actualTime;
        }

        return e.ValueKind == a.ValueKind && e.ToString() == a.ToString();
    }

    private ApplicationStateService Load(JsonObject payload)
    {
        _storeMock.Setup(s => s.Load())
            .Returns(new StateLoadResult(StateLoadStatus.Loaded, payload, SchemaVersionOnDisk: 7, MigrationsApplied: 0, QuarantinePath: null));
        _storeMock.Setup(s => s.Save(It.IsAny<JsonObject>()))
            .Callback<JsonObject>(p => _saved.Add((JsonObject)JsonNode.Parse(p.ToJsonString())!))
            .Returns(new StateSaveOutcome(StateSaveResult.Saved, null));

        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IDiagnosticsLog))).Returns(_diagnostics);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IWindowManager))).Returns(_windowManagerMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IPageManager))).Returns(_pageManagerMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(ILayoutSnapshotService))).Returns(_layoutSnapshotServiceMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(ISystemIconsPanelManager))).Returns(_systemIconsPanelManagerMock.Object);

        var sut = new ApplicationStateService(_storeMock.Object, _serviceProviderMock.Object);
        sut.Load();
        return sut;
    }

    /// <summary>Loads <paramref name="payload"/> and wires the mocked owners so the next save reads back what was loaded.</summary>
    private ApplicationStateService LoadAndWire(JsonObject payload)
    {
        var sut = Load(payload);

        var systemIconsModel = new SystemIconsPanelModel();
        if (sut.SystemIconsPanel is { } dto)
        {
            SystemIconsPanelStateMapper.ApplyTo(dto, systemIconsModel);
        }

        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([.. sut.Panels]);
        _pageManagerMock.SetupGet(p => p.PageDescriptors).Returns(sut.Pages);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns(sut.CurrentPageId);
        _layoutSnapshotServiceMock.SetupGet(l => l.Snapshots).Returns(sut.Snapshots);
        _systemIconsPanelManagerMock.SetupGet(s => s.Model).Returns(systemIconsModel);

        sut.MarkConsumersReady();
        return sut;
    }

    private sealed class FakeDiagnosticsLog : IDiagnosticsLog
    {
        public List<(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)> Events { get; } = [];

        public bool IsEnabled => true;

        public void Write(string category, string name, string? scope, params KeyValuePair<string, object?>[] payload) =>
            Events.Add((category, name, scope, payload));

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
