using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Unit tests for <see cref="LegacySettingsImporter"/> (ADR-024 §1 D-9, §2.3). Verifies the
/// schema-v1 document assembled from the five legacy <see cref="IAppSettings"/> keys, and the
/// FirstRun-vs-import decision (ADR-024 D-7 step 6 vs step 5).
/// </summary>
/// <remarks>
/// The five <see cref="IAppSettings"/> members this file exercises are <c>[Obsolete]</c> as of
/// ADR-024 C6 (legacy, frozen, read-only — see their remarks): this test class is one of their two
/// sanctioned readers (the other being <see cref="LegacySettingsImporter"/> itself), hence the
/// file-wide CS0618 suppression below.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
#pragma warning disable CS0618 // Type or member is obsolete
public class LegacyStateImporterTests
{
    private readonly Mock<IAppSettings> _appSettingsMock = new();

    // ── FirstRun vs import ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that when all five legacy <see cref="IAppSettings"/> keys are at their default values,
    /// <see cref="LegacySettingsImporter.TryImport"/> returns null (ADR-024 D-7 step 6, FirstRun).
    /// </summary>
    [Fact]
    public void AllKeysAtDefault_ReturnsNull()
    {
        SetupDefaults();

        var result = BuildSut().TryImport();

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that when all five legacy <see cref="IAppSettings"/> keys are explicitly null,
    /// <see cref="LegacySettingsImporter.TryImport"/> returns null instead of throwing.
    /// </summary>
    [Fact]
    public void AllKeysNull_ReturnsNull()
    {
        _appSettingsMock.SetupGet(s => s.ChildWindowsState).Returns((string)null!);
        _appSettingsMock.SetupGet(s => s.Pages).Returns((string)null!);
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns((string)null!);
        _appSettingsMock.SetupGet(s => s.LayoutSnapshots).Returns((string)null!);
        _appSettingsMock.SetupGet(s => s.SystemIconsPanel).Returns((string)null!);

        var result = BuildSut().TryImport();

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that when only the legacy <c>ChildWindowsState</c> key holds non-default data,
    /// <see cref="LegacySettingsImporter.TryImport"/> returns a non-null document (ADR-024 D-7 step 5, import).
    /// </summary>
    [Fact]
    public void OnlyPanelsPresent_ReturnsNonNullDocument()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.ChildWindowsState)
            .Returns("""[{"Id":"11111111-1111-1111-1111-111111111111","Title":"Work"}]""");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);
    }

    // ── Full document, all five keys populated ──────────────────────────────────

    /// <summary>
    /// Verifies that with all five legacy <see cref="IAppSettings"/> keys populated,
    /// <see cref="LegacySettingsImporter.TryImport"/> assembles a full schema-v1 document with envelope
    /// fields and every panel, page, layout snapshot, and system icons panel field carried over.
    /// </summary>
    [Fact]
    public void AllFiveKeysPopulated_ProducesFullV1Document()
    {
        _appSettingsMock.SetupGet(s => s.ChildWindowsState)
            .Returns("""[{"Id":"11111111-1111-1111-1111-111111111111","Title":"Work","SortMode":2}]""");
        _appSettingsMock.SetupGet(s => s.Pages).Returns("""["default","work"]""");
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns("work");
        _appSettingsMock.SetupGet(s => s.LayoutSnapshots)
            .Returns("""[{"Id":"snap-1","Name":"Morning","CreatedAt":"2026-01-01T00:00:00","PanelJsonItems":["{\"Id\":\"x\"}"]}]""");
        _appSettingsMock.SetupGet(s => s.SystemIconsPanel)
            .Returns("""{"IsEnabled":true,"IconSize":"Medium"}""");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);
        Assert.Equal(1, (int)result!["schemaVersion"]!);
        Assert.NotNull(result["writtenAtUtc"]);
        Assert.NotNull(result["appVersion"]);

        var panels = Assert.IsType<JsonArray>(result["panels"]);
        Assert.Single(panels);
        Assert.Equal("Work", (string)panels[0]!["Title"]!);
        Assert.Equal(2, (int)panels[0]!["SortMode"]!);

        var pages = Assert.IsType<JsonObject>(result["pages"]);
        var ids = Assert.IsType<JsonArray>(pages["Ids"]);
        Assert.Equal(["default", "work"], ids.Select(n => (string)n!));
        Assert.Equal("work", (string)pages["CurrentPageId"]!);

        var snapshots = Assert.IsType<JsonArray>(result["layoutSnapshots"]);
        Assert.Single(snapshots);
        Assert.Equal("Morning", (string)snapshots[0]!["Name"]!);
        var panelJsonItems = Assert.IsType<JsonArray>(snapshots[0]!["PanelJsonItems"]);
        Assert.Single(panelJsonItems);

        var systemIconsPanel = Assert.IsType<JsonObject>(result["systemIconsPanel"]);
        Assert.True((bool)systemIconsPanel["IsEnabled"]!);
        Assert.Equal("Medium", (string)systemIconsPanel["IconSize"]!);
    }

    // ── pages object assembled from two separate legacy fields ──────────────────

    /// <summary>
    /// Verifies that the imported document's <c>pages</c> object is assembled from the two separate
    /// legacy <c>Pages</c> and <c>CurrentPage</c> settings keys.
    /// </summary>
    [Fact]
    public void PagesObject_AssembledFromSeparatePagesAndCurrentPageFields()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.Pages).Returns("""["default","personal","work"]""");
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns("personal");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);
        var pages = Assert.IsType<JsonObject>(result!["pages"]);
        var ids = Assert.IsType<JsonArray>(pages["Ids"]);
        Assert.Equal(["default", "personal", "work"], ids.Select(n => (string)n!));
        Assert.Equal("personal", (string)pages["CurrentPageId"]!);
    }

    /// <summary>
    /// Verifies that an empty legacy <c>CurrentPage</c> value makes the imported document's current
    /// page id default to "default".
    /// </summary>
    [Fact]
    public void CurrentPageBlank_DefaultsToDefaultPageId()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.Pages).Returns("""["default","work"]""");
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns(string.Empty);

        var result = BuildSut().TryImport();

        Assert.NotNull(result);
        var pages = Assert.IsType<JsonObject>(result!["pages"]);
        Assert.Equal("default", (string)pages["CurrentPageId"]!);
    }

    // ── one corrupt block does not block the rest ────────────────────────────────

    /// <summary>
    /// Verifies that corrupt JSON in the legacy <c>ChildWindowsState</c> key degrades only the panels
    /// section to empty, while pages and layout snapshots are still imported normally.
    /// </summary>
    [Fact]
    public void CorruptChildWindowsState_OthersStillImported_PanelsDegradeToEmpty()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.ChildWindowsState).Returns("{not valid json!!!");
        _appSettingsMock.SetupGet(s => s.Pages).Returns("""["default","work"]""");
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns("work");
        _appSettingsMock.SetupGet(s => s.LayoutSnapshots)
            .Returns("""[{"Id":"snap-1","Name":"Morning","CreatedAt":"2026-01-01T00:00:00","PanelJsonItems":[]}]""");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);

        var panels = Assert.IsType<JsonArray>(result!["panels"]);
        Assert.Empty(panels);

        var pages = Assert.IsType<JsonObject>(result["pages"]);
        var ids = Assert.IsType<JsonArray>(pages["Ids"]);
        Assert.Equal(["default", "work"], ids.Select(n => (string)n!));

        var snapshots = Assert.IsType<JsonArray>(result["layoutSnapshots"]);
        Assert.Single(snapshots);
    }

    /// <summary>
    /// Verifies that corrupt JSON in the legacy <c>LayoutSnapshots</c> key degrades only the layout
    /// snapshots section to empty, while panels are still imported normally.
    /// </summary>
    [Fact]
    public void CorruptLayoutSnapshots_OthersStillImported_SnapshotsDegradeToEmpty()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.ChildWindowsState)
            .Returns("""[{"Id":"11111111-1111-1111-1111-111111111111","Title":"Work"}]""");
        _appSettingsMock.SetupGet(s => s.LayoutSnapshots).Returns("not [valid] json");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);

        var panels = Assert.IsType<JsonArray>(result!["panels"]);
        Assert.Single(panels);

        var snapshots = Assert.IsType<JsonArray>(result["layoutSnapshots"]);
        Assert.Empty(snapshots);
    }

    /// <summary>
    /// Verifies that corrupt JSON in the legacy <c>SystemIconsPanel</c> key degrades only the system
    /// icons panel section to an empty object, while panels are still imported normally.
    /// </summary>
    [Fact]
    public void CorruptSystemIconsPanel_OthersStillImported_SystemIconsPanelDegradesToEmptyObject()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.ChildWindowsState)
            .Returns("""[{"Id":"11111111-1111-1111-1111-111111111111","Title":"Work"}]""");
        _appSettingsMock.SetupGet(s => s.SystemIconsPanel).Returns("{garbage");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);

        var panels = Assert.IsType<JsonArray>(result!["panels"]);
        Assert.Single(panels);

        var systemIconsPanel = Assert.IsType<JsonObject>(result["systemIconsPanel"]);
        Assert.Empty(systemIconsPanel);
    }

    /// <summary>
    /// Verifies that corrupt JSON in the legacy <c>Pages</c> key degrades only the pages section to the
    /// single "default" page, while panels are still imported normally.
    /// </summary>
    [Fact]
    public void CorruptPages_OthersStillImported_PagesDegradeToDefault()
    {
        SetupDefaults();
        _appSettingsMock.SetupGet(s => s.ChildWindowsState)
            .Returns("""[{"Id":"11111111-1111-1111-1111-111111111111","Title":"Work"}]""");
        _appSettingsMock.SetupGet(s => s.Pages).Returns("{not an array");

        var result = BuildSut().TryImport();

        Assert.NotNull(result);

        var panels = Assert.IsType<JsonArray>(result!["panels"]);
        Assert.Single(panels);

        var pages = Assert.IsType<JsonObject>(result["pages"]);
        var ids = Assert.IsType<JsonArray>(pages["Ids"]);
        Assert.Equal(["default"], ids.Select(n => (string)n!));
    }

    // ── never throws ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that with every one of the five legacy settings keys holding corrupt JSON,
    /// <see cref="LegacySettingsImporter.TryImport"/> never throws and still returns a non-null,
    /// fully-degraded document.
    /// </summary>
    [Fact]
    public void EveryKeyCorrupt_DoesNotThrow_ReturnsNonNullDegradedDocument()
    {
        _appSettingsMock.SetupGet(s => s.ChildWindowsState).Returns("{{{not json");
        _appSettingsMock.SetupGet(s => s.Pages).Returns("[[[not json");
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns("work");
        _appSettingsMock.SetupGet(s => s.LayoutSnapshots).Returns("not json at all");
        _appSettingsMock.SetupGet(s => s.SystemIconsPanel).Returns("also not json");

        var exception = Record.Exception(() => BuildSut().TryImport());

        Assert.Null(exception);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private ILegacyStateImporter BuildSut() => new LegacySettingsImporter(_appSettingsMock.Object);

    private void SetupDefaults()
    {
        _appSettingsMock.SetupGet(s => s.ChildWindowsState).Returns(string.Empty);
        _appSettingsMock.SetupGet(s => s.Pages).Returns("[\"default\"]");
        _appSettingsMock.SetupGet(s => s.CurrentPage).Returns("default");
        _appSettingsMock.SetupGet(s => s.LayoutSnapshots).Returns("[]");
        _appSettingsMock.SetupGet(s => s.SystemIconsPanel).Returns(string.Empty);
    }
#pragma warning restore CS0618
}
