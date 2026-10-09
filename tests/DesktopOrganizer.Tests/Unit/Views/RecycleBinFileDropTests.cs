using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Unit tests for <see cref="RecycleBinFileDrop"/> — a file drop on the sidebar's Recycle Bin icon (ADR-035
/// amendment, ADR-audit review cycle 1 G1): the Recycle Bin operation (which can show the nuke warning and the
/// progress dialog) never runs inside the OLE <c>Drop</c> call, and the source is never told to delete the
/// originals itself.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class RecycleBinFileDropTests
{
    /// <summary>
    /// The operation is queued, not run: nothing happens while the Drop handler is on the stack; it runs once the
    /// dispatcher gets to its queue, with the dropped paths.
    /// </summary>
    [Fact]
    public void TryQueue_FileDrop_DoesNotRecycleSynchronously_RunsAfterTheDispatcherPumps()
    {
        StaTestRunner.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\a.txt", @"C:\b.txt" });
            var calls = new List<string[]>();

            var queued = RecycleBinFileDrop.TryQueue(data, dispatcher, calls.Add);

            Assert.True(queued);
            Assert.Empty(calls);

            FlushDispatcherQueue(dispatcher);

            var paths = Assert.Single(calls);
            Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, paths);
        });
    }

    /// <summary>The queued operation works on a private copy — the data object's array may be gone or reused by then.</summary>
    [Fact]
    public void TryQueue_FileDrop_PassesAPrivateCopyOfThePaths()
    {
        StaTestRunner.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var dropped = new[] { @"C:\a.txt" };
            var data = new Mock<IDataObject>();
            data.Setup(d => d.GetDataPresent(DataFormats.FileDrop)).Returns(true);
            data.Setup(d => d.GetData(DataFormats.FileDrop)).Returns(dropped);
            string[]? received = null;

            Assert.True(RecycleBinFileDrop.TryQueue(data.Object, dispatcher, p => received = p));
            dropped[0] = @"C:\changed.txt";
            FlushDispatcherQueue(dispatcher);

            Assert.NotNull(received);
            Assert.NotSame(dropped, received);
            Assert.Equal(@"C:\a.txt", received![0]);
        });
    }

    /// <summary>A non-file drag (the sidebar's own reorder drag) is not handled, so it bubbles to the ListBox.</summary>
    [Fact]
    public void TryQueue_ReorderDrag_IsNotHandled()
    {
        StaTestRunner.Run(() =>
        {
            var data = new DataObject("SystemIconKind", 3);
            var called = false;

            Assert.False(RecycleBinFileDrop.TryQueue(data, Dispatcher.CurrentDispatcher, _ => called = true));

            FlushDispatcherQueue(Dispatcher.CurrentDispatcher);
            Assert.False(called);
        });
    }

    /// <summary>An empty file list and a throwing foreign data object are "not a file drop" — never an exception.</summary>
    [Fact]
    public void TryQueue_EmptyOrThrowingData_IsNotHandled()
    {
        StaTestRunner.Run(() =>
        {
            var empty = new Mock<IDataObject>();
            empty.Setup(d => d.GetDataPresent(DataFormats.FileDrop)).Returns(true);
            empty.Setup(d => d.GetData(DataFormats.FileDrop)).Returns(Array.Empty<string>());

            var throwing = new Mock<IDataObject>();
            throwing.Setup(d => d.GetDataPresent(DataFormats.FileDrop)).Throws(new System.Runtime.InteropServices.COMException("RPC"));

            Assert.False(RecycleBinFileDrop.TryQueue(empty.Object, Dispatcher.CurrentDispatcher, _ => { }));
            Assert.False(RecycleBinFileDrop.TryQueue(throwing.Object, Dispatcher.CurrentDispatcher, _ => { }));
        });
    }

    /// <summary>
    /// The effect reported to the source is <see cref="DragDropEffects.None"/>: with <see cref="DragDropEffects.Move"/>
    /// an "unoptimized move" source would delete the originals itself — permanently, before the Recycle Bin
    /// operation even ran, and even if the user then declined the nuke warning.
    /// </summary>
    [Fact]
    public void ReportedEffect_NeverAsksTheSourceToDeleteTheOriginals()
    {
        Assert.Equal(DragDropEffects.None, RecycleBinFileDrop.ReportedEffect);
    }

    /// <summary>
    /// Source guard for the sidebar window (an OLE <c>DragEventArgs</c> cannot be constructed in a unit test): its
    /// Drop handler reaches <c>SendToRecycleBin</c> only through <see cref="RecycleBinFileDrop.TryQueue"/>, with no
    /// owner window, and reports <see cref="RecycleBinFileDrop.ReportedEffect"/>.
    /// </summary>
    [Fact]
    public void SidebarWindow_DropHandler_RecyclesOnlyThroughTheDeferredQueue()
    {
        var path = Path.Combine(RepoRootLocator.Find(), "src", "DesktopOrganizer.UI", "Views", "SystemIconsPanelWindow.xaml.cs");
        var source = File.ReadAllText(path);

        var calls = Regex.Matches(source, @"SendToRecycleBin\(");
        Assert.Single(calls);
        Assert.Matches(
            new Regex(@"RecycleBinFileDrop\.TryQueue\(\s*e\.Data,\s*Dispatcher,\s*paths\s*=>\s*_viewModel\.SendToRecycleBin\(paths,\s*IntPtr\.Zero\)\)"),
            source);
        Assert.Contains("e.Effects = RecycleBinFileDrop.ReportedEffect;", source, StringComparison.Ordinal);
    }

    private static void FlushDispatcherQueue(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
