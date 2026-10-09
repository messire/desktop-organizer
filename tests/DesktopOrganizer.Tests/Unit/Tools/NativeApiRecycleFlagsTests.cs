using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Contract test for the <c>SHFileOperation</c> flags shared by <c>NativeApi.SendToRecycleBin</c> (drop on the
/// sidebar Recycle Bin icon) and <c>NativeApi.RecycleItems</c> (panel-item removal), ADR-035: both must carry
/// <c>FOF_WANTNUKEWARNING</c> so an item that cannot go to the Recycle Bin is never deleted permanently without
/// the shell asking first.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class NativeApiRecycleFlagsTests
{
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    /// <summary>
    /// The recycle flags must request the permanent-delete warning and keep undo, without extra confirmations.
    /// </summary>
    [Fact]
    public void RecycleFlags_RequestNukeWarning_AllowUndo_AndNoOrdinaryConfirmation()
    {
        var flags = NativeApi.RecycleFlags;

        Assert.NotEqual(0, flags & FofWantNukeWarning);
        Assert.NotEqual(0, flags & FofAllowUndo);
        Assert.NotEqual(0, flags & FofNoConfirmation);
        Assert.NotEqual(0, flags & FofNoErrorUi);
    }

    /// <summary>
    /// FOF_SILENT must stay off so the shell is able to surface the nuke warning and a cancellable progress UI.
    /// </summary>
    [Fact]
    public void RecycleFlags_DoNotSuppressShellUiWithSilent()
    {
        Assert.Equal(0, NativeApi.RecycleFlags & FofSilent);
    }
}
