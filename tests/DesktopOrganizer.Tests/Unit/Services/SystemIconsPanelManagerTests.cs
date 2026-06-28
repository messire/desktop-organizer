using System.Text.Json;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for SystemIconsPanelManager.
/// IAppSettings, IDesktopIntegrationService, IMonitorService and the window factory
/// are all mocked — no WPF window is created.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SystemIconsPanelManagerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private readonly Mock<IAppSettings> _settingsMock = new();
    private readonly Mock<IDesktopIntegrationService> _desktopMock = new();
    private readonly Mock<IMonitorService> _monitorMock = new();

    // Tracks whether the factory delegate was invoked.
    private bool _factoryInvoked;

    private SystemIconsPanelManager BuildSut(string settingsJson = "")
    {
        _settingsMock.SetupProperty(s => s.SystemIconsPanel, settingsJson);
        _settingsMock.Setup(s => s.Save());

        _monitorMock.SetupAdd(m => m.MonitorsChanged += It.IsAny<Action>());
        _monitorMock.SetupRemove(m => m.MonitorsChanged -= It.IsAny<Action>());
        _monitorMock.Setup(m => m.GetPrimaryMonitor())
            .Returns(new MonitorInfo(
                DeviceName: "\\\\.\\DISPLAY1",
                Bounds: new System.Windows.Rect(0, 0, 1920, 1080),
                WorkArea: new System.Windows.Rect(0, 0, 1920, 1040),
                IsPrimary: true));

        _factoryInvoked = false;

        // The window factory is a Func<SystemIconsPanelWindow>; we never call it successfully
        // (no real WPF dispatcher), so the manager's Apply() will swallow the exception and
        // _window stays null.  We just track that the factory was invoked.
#pragma warning disable CA1416 // Platform check not needed in unit tests
        SystemIconsPanelWindow FactoryDelegate()
        {
            _factoryInvoked = true;
            // Throw immediately — no WPF dispatcher available in unit tests.
            // The manager swallows window-creation exceptions gracefully.
            throw new InvalidOperationException("Window creation not supported in unit tests.");
        }
#pragma warning restore CA1416

        return new SystemIconsPanelManager(
            _settingsMock.Object,
            _desktopMock.Object,
            _monitorMock.Object,
            FactoryDelegate);
    }

    /// <summary>
    /// Builds a JSON string in the format expected by SystemIconsPanelManager.
    /// Mirrors the private PanelDto / ItemDto structure.
    /// </summary>
    private static string BuildJson(bool isEnabled, string iconSize = "Medium",
        IEnumerable<(SystemIconKind Kind, bool Visible, int Order)>? items = null)
    {
        var itemList = (items ?? DefaultItemTuples()).Select(t => new
        {
            Kind = t.Kind.ToString(),
            t.Visible,
            t.Order,
        });

        var dto = new
        {
            IsEnabled = isEnabled,
            IconSize = iconSize,
            Items = itemList,
        };

        return JsonSerializer.Serialize(dto);
    }

    private static IEnumerable<(SystemIconKind Kind, bool Visible, int Order)> DefaultItemTuples() =>
    [
        (SystemIconKind.ThisPc,       true, 0),
        (SystemIconKind.ControlPanel, true, 1),
        (SystemIconKind.RecycleBin,   true, 2),
        (SystemIconKind.Network,      true, 3),
        (SystemIconKind.UserFolder,   true, 4),
    ];

    // ── Load: empty settings → defaults ──────────────────────────────────────

    [Fact]
    public void Load_WithEmptySettings_UsesDefaults_IsEnabledFalse()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "");

        // Act
        sut.Load();

        // Assert — feature is off by default
        Assert.False(sut.Model.IsEnabled);
    }

    [Fact]
    public void Load_WithEmptySettings_UsesDefaults_AllFiveIconsPresent()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "");

        // Act
        sut.Load();

        // Assert — all 5 well-known icons are present
        var allKinds = Enum.GetValues<SystemIconKind>();
        foreach (var kind in allKinds)
        {
            Assert.Contains(sut.Model.Items, i => i.Kind == kind);
        }

        Assert.Equal(allKinds.Length, sut.Model.Items.Count);
    }

    [Fact]
    public void Load_WithEmptySettings_UsesDefaults_AllIconsVisible()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "");

        // Act
        sut.Load();

        // Assert — default visibility is true for every icon
        Assert.All(sut.Model.Items, item => Assert.True(item.IsVisible));
    }

    // ── Load: saved JSON restores model ──────────────────────────────────────

    [Fact]
    public void Load_WithSavedJson_RestoresIsEnabled()
    {
        // Arrange
        var json = BuildJson(isEnabled: true);
        var sut = BuildSut(settingsJson: json);

        // Act
        sut.Load();

        // Assert
        Assert.True(sut.Model.IsEnabled);
    }

    [Fact]
    public void Load_WithSavedJson_RestoresItemVisibility()
    {
        // Arrange — ControlPanel is marked invisible
        var items = new[]
        {
            (SystemIconKind.ThisPc,       true,  0),
            (SystemIconKind.ControlPanel, false, 1),
            (SystemIconKind.RecycleBin,   true,  2),
            (SystemIconKind.Network,      true,  3),
            (SystemIconKind.UserFolder,   true,  4),
        };
        var json = BuildJson(isEnabled: false, items: items);
        var sut = BuildSut(settingsJson: json);

        // Act
        sut.Load();

        // Assert — ControlPanel was saved as invisible
        var cp = sut.Model.Items.Single(i => i.Kind == SystemIconKind.ControlPanel);
        Assert.False(cp.IsVisible);
    }

    [Fact]
    public void Load_WithSavedJson_RestoresItemOrder()
    {
        // Arrange — items in reversed order
        var items = new[]
        {
            (SystemIconKind.UserFolder,   true, 0),
            (SystemIconKind.Network,      true, 1),
            (SystemIconKind.RecycleBin,   true, 2),
            (SystemIconKind.ControlPanel, true, 3),
            (SystemIconKind.ThisPc,       true, 4),
        };
        var json = BuildJson(isEnabled: false, items: items);
        var sut = BuildSut(settingsJson: json);

        // Act
        sut.Load();

        // Assert — order values are restored
        Assert.Equal(4, sut.Model.Items.Single(i => i.Kind == SystemIconKind.ThisPc).Order);
        Assert.Equal(0, sut.Model.Items.Single(i => i.Kind == SystemIconKind.UserFolder).Order);
    }

    // ── Save: serialises model to IAppSettings.SystemIconsPanel ──────────────

    [Fact]
    public void Save_SerializesModelToSettings()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "");
        sut.Load();
        sut.Model.IsEnabled = true;

        // Act
        sut.Save();

        // Assert — property was written with non-empty JSON
        var written = _settingsMock.Object.SystemIconsPanel;
        Assert.False(string.IsNullOrWhiteSpace(written));

        using var doc = JsonDocument.Parse(written);
        Assert.True(doc.RootElement.GetProperty("IsEnabled").GetBoolean());
    }

    [Fact]
    public void Save_CallsAppSettingsSave()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "");
        sut.Load();

        // Act
        sut.Save();

        // Assert — IAppSettings.Save() was called to flush to disk
        _settingsMock.Verify(s => s.Save(), Times.Once);
    }

    // ── Apply: disabled → factory not invoked ────────────────────────────────

    [Fact]
    public void Apply_WhenDisabled_DoesNotInvokeWindowFactory()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "");
        sut.Load();

        Assert.False(sut.Model.IsEnabled); // precondition

        // Act
        sut.Apply();

        // Assert — window factory was never invoked
        Assert.False(_factoryInvoked);
    }

    [Fact]
    public void Apply_WhenEnabled_InvokesWindowFactory()
    {
        // Arrange
        var json = BuildJson(isEnabled: true);
        var sut = BuildSut(settingsJson: json);
        sut.Load();

        // Act — factory throws (no WPF dispatcher), but we can detect it was called
        sut.Apply();

        // Assert
        Assert.True(_factoryInvoked);
    }

    // ── Load: partial / invalid JSON → does not throw, uses defaults ─────────

    [Fact]
    public void Load_PartialJson_DoesNotThrow()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "{\"IsEnabled\": true"); // truncated JSON

        // Act & Assert
        var ex = Record.Exception(() => sut.Load());
        Assert.Null(ex);
    }

    [Fact]
    public void Load_InvalidJson_UsesDefaultModel()
    {
        // Arrange
        var sut = BuildSut(settingsJson: "NOT_VALID_JSON");

        // Act
        sut.Load();

        // Assert — fell back to defaults (IsEnabled=false, 5 items)
        Assert.False(sut.Model.IsEnabled);
        Assert.Equal(Enum.GetValues<SystemIconKind>().Length, sut.Model.Items.Count);
    }
}
