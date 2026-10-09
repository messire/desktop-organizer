using System.Globalization;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// ADR-audit WP-B review cycle 1 (F5/F6) texts: the read-only notice, the read-only drop hint, the tray
/// tooltip suffix and the layout-snapshot restore messages exist in both languages (no key-name fallback)
/// and say what the user needs to know.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class ReadOnlyAndSnapshotRestoreTextsTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Every plain (non-placeholder) key resolves to a real, non-empty value in both languages.
    /// </summary>
    /// <param name="key">The RESX key.</param>
    [Theory]
    [InlineData("ReadOnlySession_Notice_Title")]
    [InlineData("ReadOnlySession_Notice_Hint")]
    [InlineData("ReadOnlySession_Notice_NewerSchema_Message")]
    [InlineData("ReadOnlySession_Notice_NewerSchema_Detail")]
    [InlineData("ReadOnlySession_Notice_NewerSchema_Action")]
    [InlineData("ReadOnlySession_Notice_Locked_Message")]
    [InlineData("ReadOnlySession_Notice_Locked_Detail")]
    [InlineData("ReadOnlySession_Notice_Locked_Action")]
    [InlineData("Hint_ReadOnlyDrop_Message")]
    [InlineData("Snapshot_Restore_Message")]
    [InlineData("Snapshot_Restore_Files_Message")]
    [InlineData("Snapshot_Restore_ItemsNotReturned_Title")]
    [InlineData("Settings_Backup_LayoutSnapshots_Description")]
    public void PlainKeys_ExistInBothLanguages(string key)
    {
        foreach (var culture in new[] { English, Russian })
        {
            var value = LocalizationResources.GetString(key, culture);

            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty in {culture.Name}.");
            Assert.NotEqual(key, value);
        }
    }

    /// <summary>
    /// The notice title, the shared "files stay" sentence and the per-reason sentences carry the agreed
    /// wording in both languages (decisions review cycle 1, U4): a newer on-disk schema says the panels are
    /// not deleted and to run the latest version; a held lock says changes are not saved and to close the
    /// other instance.
    /// </summary>
    [Fact]
    public void ReadOnlyNotice_NamesTheReason_FilesStay_AndWhatToDo()
    {
        Assert.Equal("Read-only mode", LocalizationResources.GetString("ReadOnlySession_Notice_Title", English));
        Assert.Equal("Режим только для чтения", LocalizationResources.GetString("ReadOnlySession_Notice_Title", Russian));

        Assert.Contains("stay where they are", LocalizationResources.GetString("ReadOnlySession_Notice_Hint", English), StringComparison.Ordinal);
        Assert.Contains("остаются на прежнем месте", LocalizationResources.GetString("ReadOnlySession_Notice_Hint", Russian), StringComparison.Ordinal);

        Assert.Contains("newer version", LocalizationResources.GetString("ReadOnlySession_Notice_NewerSchema_Message", English), StringComparison.Ordinal);
        Assert.Contains("более новой версией", LocalizationResources.GetString("ReadOnlySession_Notice_NewerSchema_Message", Russian), StringComparison.Ordinal);
        Assert.Contains("not deleted", LocalizationResources.GetString("ReadOnlySession_Notice_NewerSchema_Detail", English), StringComparison.Ordinal);
        Assert.Contains("не удалены", LocalizationResources.GetString("ReadOnlySession_Notice_NewerSchema_Detail", Russian), StringComparison.Ordinal);
        Assert.Contains("latest version", LocalizationResources.GetString("ReadOnlySession_Notice_NewerSchema_Action", English), StringComparison.Ordinal);
        Assert.Contains("актуальную версию", LocalizationResources.GetString("ReadOnlySession_Notice_NewerSchema_Action", Russian), StringComparison.Ordinal);

        Assert.Contains("Another instance", LocalizationResources.GetString("ReadOnlySession_Notice_Locked_Message", English), StringComparison.Ordinal);
        Assert.Contains("Другой экземпляр", LocalizationResources.GetString("ReadOnlySession_Notice_Locked_Message", Russian), StringComparison.Ordinal);
        Assert.Contains("not be saved", LocalizationResources.GetString("ReadOnlySession_Notice_Locked_Detail", English), StringComparison.Ordinal);
        Assert.Contains("не сохранятся", LocalizationResources.GetString("ReadOnlySession_Notice_Locked_Detail", Russian), StringComparison.Ordinal);
        Assert.Contains("other instance", LocalizationResources.GetString("ReadOnlySession_Notice_Locked_Action", English), StringComparison.Ordinal);
        Assert.Contains("другой экземпляр", LocalizationResources.GetString("ReadOnlySession_Notice_Locked_Action", Russian), StringComparison.Ordinal);
    }

    /// <summary>
    /// The OneDrive hint points to dragging the item out of the panel (ADR-048: deleting a panel sends the
    /// stored file to the Recycle Bin), never to removing the panel (decisions review cycle 1, U1).
    /// </summary>
    [Fact]
    public void CloudDropHint_PointsToDragOut_NotToRemovingThePanel()
    {
        var hintEn = LocalizationResources.GetString("Hint_CloudDrop_Hint", English);
        var hintRu = LocalizationResources.GetString("Hint_CloudDrop_Hint", Russian);

        Assert.Contains("drag", hintEn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove the panel", hintEn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("перетащите", hintRu, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Удалите", hintRu, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("file or folder", LocalizationResources.GetString("Hint_CloudDrop_Message", English), StringComparison.Ordinal);
        Assert.Contains("Файл или папка", LocalizationResources.GetString("Hint_CloudDrop_Message", Russian), StringComparison.Ordinal);
    }

    /// <summary>
    /// The restore confirmation tells that panels outside the snapshot close and their files go back.
    /// </summary>
    [Fact]
    public void RestoreConfirmation_MentionsClosedPanelsAndReturnedFiles()
    {
        Assert.Contains("closed", LocalizationResources.GetString("Snapshot_Restore_Message", English), StringComparison.Ordinal);
        Assert.Contains("original location", LocalizationResources.GetString("Snapshot_Restore_Files_Message", English), StringComparison.Ordinal);
        Assert.Contains("закрыты", LocalizationResources.GetString("Snapshot_Restore_Message", Russian), StringComparison.Ordinal);
        Assert.Contains("на прежнее место", LocalizationResources.GetString("Snapshot_Restore_Files_Message", Russian), StringComparison.Ordinal);
    }

    /// <summary>
    /// The snapshot description mentions that the file set is captured too.
    /// </summary>
    [Fact]
    public void SnapshotDescription_MentionsFiles()
    {
        Assert.Contains("files", LocalizationResources.GetString("Settings_Backup_LayoutSnapshots_Description", English), StringComparison.Ordinal);
        Assert.Contains("состав файлов", LocalizationResources.GetString("Settings_Backup_LayoutSnapshots_Description", Russian), StringComparison.Ordinal);
    }

    /// <summary>
    /// The placeholder keys format their value in both languages: the not-returned message carries the count
    /// and the manual-drag hint, the returned message carries the count, the tray tooltip keeps the name and
    /// adds the read-only suffix.
    /// </summary>
    [Fact]
    public void FormatKeys_FormatCountsAndNames()
    {
        var notReturnedEn = LocalizationResources.Format("Snapshot_Restore_ItemsNotReturned_Format", English, 3);
        var notReturnedRu = LocalizationResources.Format("Snapshot_Restore_ItemsNotReturned_Format", Russian, 3);
        Assert.Contains("3", notReturnedEn, StringComparison.Ordinal);
        Assert.Contains("drag them out", notReturnedEn, StringComparison.Ordinal);
        Assert.Contains("3", notReturnedRu, StringComparison.Ordinal);
        Assert.Contains("перетащите их из панели вручную", notReturnedRu, StringComparison.Ordinal);

        Assert.Contains("5", LocalizationResources.Format("Snapshot_Restore_ItemsReturned_Format", English, 5), StringComparison.Ordinal);
        Assert.Contains("Возвращено на прежнее место: 5", LocalizationResources.Format("Snapshot_Restore_ItemsReturned_Format", Russian, 5), StringComparison.Ordinal);

        Assert.Equal("App (read-only)", LocalizationResources.Format("Tray_Tooltip_ReadOnly_Format", English, "App"));
        Assert.Equal("App (только чтение)", LocalizationResources.Format("Tray_Tooltip_ReadOnly_Format", Russian, "App"));
    }
}
