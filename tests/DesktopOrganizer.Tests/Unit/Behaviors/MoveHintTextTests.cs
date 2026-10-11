using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Store-publication B4: <see cref="MoveHintText.Compose"/> names the folder the moved file lives in, and adds the
/// "stays there after uninstall" sentence only under package identity (the Inno uninstaller returns the files).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class MoveHintTextTests
{
    private const string Folder = @"C:\Users\someone\DesktopOrganizer\PanelItems";

    /// <summary>Verifies the folder is substituted and no retention sentence appears outside package identity.</summary>
    [Fact]
    public void Compose_NotPackaged_English_NamesFolderWithoutRetentionSentence()
    {
        var text = MoveHintText.Compose(TestLocalization.English, Folder, survivesUninstall: false);

        Assert.Equal($"The file was moved into the panel and is kept in {Folder}.", text);
        Assert.DoesNotContain("uninstall", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies the retention sentence is appended under package identity.</summary>
    [Fact]
    public void Compose_Packaged_English_AppendsRetentionSentence()
    {
        var text = MoveHintText.Compose(TestLocalization.English, Folder, survivesUninstall: true);

        Assert.Equal($"The file was moved into the panel and is kept in {Folder}. It stays there even if you uninstall the app.", text);
    }

    /// <summary>Verifies the Russian wording, with and without the retention sentence.</summary>
    [Fact]
    public void Compose_Russian_NamesFolder_AndRetentionOnlyWhenPackaged()
    {
        var plain = MoveHintText.Compose(TestLocalization.Russian, Folder, survivesUninstall: false);
        var packaged = MoveHintText.Compose(TestLocalization.Russian, Folder, survivesUninstall: true);

        Assert.Equal($"Файл перемещён в панель и хранится в папке {Folder}.", plain);
        Assert.Equal(plain + " Файл останется там и после удаления приложения.", packaged);
    }

    /// <summary>Verifies a missing localization is rejected.</summary>
    [Fact]
    public void Compose_NullLocalization_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MoveHintText.Compose(null!, Folder, survivesUninstall: false));
    }
}
