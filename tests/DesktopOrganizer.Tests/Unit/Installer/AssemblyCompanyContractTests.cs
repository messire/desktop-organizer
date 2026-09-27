using System.Reflection;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// Pins the release-polish 1.0 follow-up (2026-09-28, explicit user approval — see CLAUDE.md's frozen
/// settings-contour note): <c>DesktopOrganizer.UI.csproj</c> no longer overrides <c>&lt;Company&gt;</c> —
/// the UI exe now inherits <c>Directory.Build.props</c>'s solution-wide <c>&lt;Company&gt;</c>
/// (<c>"khagendev"</c>), same as every other project. <see cref="AssemblyCompanyAttribute"/> feeds
/// <see cref="System.Configuration"/>'s per-user <c>user.config</c> path
/// (<c>%LocalAppData%\&lt;Company&gt;\&lt;exe&gt;_Url_&lt;hash&gt;\&lt;AssemblyVersion&gt;\user.config</c>),
/// exactly like the pinned <c>AssemblyVersion</c> (see <c>Directory.Build.props</c>'s own comment) — this
/// is why an already-installed user's <c>user.config</c> folder does not simply move with this change:
/// <see cref="DesktopOrganizer.UI.Lifecycle.UserConfigFolderMigration"/> runs once at the very start of
/// <c>Program.Main</c> and copies the old <c>DesktopOrganizer.UI</c>-rooted tree into the new
/// <c>khagendev</c>-rooted one (leaving the old tree in place) before anything reads
/// <c>Properties.Settings.Default</c>. A future rename of <c>&lt;Company&gt;</c> needs the same
/// treatment — a new one-time migration, not a silent value change — the same failure mode ("upgrade
/// silently resets everything") as the <c>schemaVersion</c> incident recorded in <c>CLAUDE.md</c>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class AssemblyCompanyContractTests
{
    private const string ExpectedUiCompany = "khagendev";

    /// <summary>
    /// <see cref="DesktopOrganizer.UI.Program"/>'s assembly (<c>DesktopOrganizer.UI.exe</c>) carries
    /// <see cref="AssemblyCompanyAttribute"/> <c>"khagendev"</c> — <c>Directory.Build.props</c>'s
    /// solution-wide value, no longer overridden per-project (see this class's own remarks for why the
    /// per-user <c>user.config</c> folder this feeds is nonetheless migrated, not silently relocated).
    /// </summary>
    [Fact]
    public void UiAssembly_HasThePinnedCompanyAttribute()
    {
        var attribute = typeof(DesktopOrganizer.UI.Program).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(ExpectedUiCompany, attribute!.Company);
    }

    /// <summary>
    /// Every other project's assembly also inherits the same <c>Directory.Build.props</c>
    /// <c>&lt;Company&gt;</c> now that <c>DesktopOrganizer.UI.csproj</c> no longer overrides it — this
    /// test pins that the two stay equal, so a future accidental per-project override on either side is
    /// caught here rather than silently splitting the two again.
    /// </summary>
    [Fact]
    public void ServicesAssembly_MatchesTheUiCompany()
    {
        var attribute = typeof(DesktopOrganizer.Services.Diagnostics.AppLog).Assembly
            .GetCustomAttribute<AssemblyCompanyAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(ExpectedUiCompany, attribute!.Company);
    }

    /// <summary>
    /// m4 (release-polish 1.0 code review): <see cref="DesktopOrganizer.UI.Lifecycle.UserConfigFolderMigration.CurrentCompanyFolderName"/>
    /// is a deliberately separate literal, not a read of this attribute (see its own remarks) — but it
    /// must still agree with what the shipped exe actually carries, or
    /// <see cref="DesktopOrganizer.UI.Lifecycle.UserConfigFolderMigration.TryGetOwnEvidenceFolderName"/>'s
    /// company-segment check would skip every migration on a real install.
    /// </summary>
    [Fact]
    public void UiAssemblyCompany_MatchesUserConfigFolderMigrationCurrentCompanyFolderName()
    {
        var attribute = typeof(DesktopOrganizer.UI.Program).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DesktopOrganizer.UI.Lifecycle.UserConfigFolderMigration.CurrentCompanyFolderName, attribute!.Company);
    }
}
