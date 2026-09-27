using System.Globalization;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Package D / ADR-035 texts: the "could not move to the Recycle Bin" message exists in both languages
/// with its <c>{0}</c> count placeholder, and the panel/page removal confirmations no longer promise a
/// permanent delete — they say the files go to the Recycle Bin.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class RecycleBinTextsTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Both <c>Recycle_*</c> keys resolve (no key-name fallback) in English and Russian, and the message
    /// formats its item count.
    /// </summary>
    /// <param name="cultureName">The culture to resolve the keys in.</param>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void RecycleFailedKeys_ExistAndFormatCount(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var title = LocalizationResources.GetString("Recycle_Failed_Title", culture);
        var message = LocalizationResources.Format("Recycle_Failed_Message_Format", culture, 3);

        Assert.NotEqual("Recycle_Failed_Title", title);
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.Contains("3", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The managed-files removal confirmations (panel and page) say the files go to the Recycle Bin and
    /// no longer claim they are deleted from disk for good.
    /// </summary>
    /// <param name="key">The confirmation message key.</param>
    [Theory]
    [InlineData("Settings_Confirm_RemovePanel_ManagedFiles_Message")]
    [InlineData("Page_Confirm_Remove_ManagedFiles_Message")]
    public void ManagedFilesConfirmations_MentionRecycleBin(string key)
    {
        var en = LocalizationResources.GetString(key, English);
        var ru = LocalizationResources.GetString(key, Russian);

        Assert.Contains("moved to the Recycle Bin", en, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted from disk", en, StringComparison.Ordinal);
        Assert.Contains("перемещены в Корзину", ru, StringComparison.Ordinal);
        Assert.DoesNotContain("удалены с диска", ru, StringComparison.Ordinal);
    }
}
