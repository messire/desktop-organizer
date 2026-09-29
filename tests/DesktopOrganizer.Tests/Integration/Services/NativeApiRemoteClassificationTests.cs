using System.IO;
using System.Runtime.InteropServices;
using DesktopOrganizer.Services.Enums;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// Real-OS checks for the two <see cref="NativeApi"/> members behind <c>RemotePathClassifier</c> (ADR-046
/// D-2): <see cref="NativeApi.TryGetShortcutRawTarget"/> against <c>.lnk</c> files written by the shell's own
/// <c>CLSID_ShellLink</c>, and <see cref="NativeApi.GetDriveKind"/> against the system drive / an unused
/// letter. Only local targets: nothing here may touch the network. Runs on an STA thread
/// (<see cref="StaTestRunner"/>), like the production icon worker.
/// </summary>
[Trait(IntegrationTests, nameof(Services))]
public sealed class NativeApiRemoteClassificationTests : IDisposable
{
    private readonly NativeApi _nativeApi = new();

    // H4: Path.GetTempPath() can come back in 8.3 short form on some CI runners (e.g. "RUNNER~1"), while
    // NativeApi.TryGetShortcutRawTarget's IShellLinkW::GetPath(SLGP_RAWPATH) call answers with the long
    // form — the two would then never string-compare equal even though they name the same file.
    // AppContext.BaseDirectory (the test binaries' own output folder) does not have this problem.
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "DO.NativeApiLnk." + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeApiRemoteClassificationTests"/> class with a fresh
    /// scratch directory.
    /// </summary>
    public NativeApiRemoteClassificationTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose()
    {
        _nativeApi.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    /// <summary>A shell-written link to an existing local file yields that file's path.</summary>
    [Fact]
    public void TryGetShortcutRawTarget_LinkToLocalFile_ReturnsTargetPath()
    {
        var target = Path.Combine(_directory, "target.txt");
        File.WriteAllText(target, "x");
        var link = Path.Combine(_directory, "target.lnk");

        var (ok, raw) = StaTestRunner.Run(() =>
        {
            CreateShortcut(link, target);
            var found = _nativeApi.TryGetShortcutRawTarget(link, out var path);
            return (found, path);
        });

        Assert.True(ok);
        Assert.Equal(target, raw, ignoreCase: true);
    }

    /// <summary>A missing <c>.lnk</c> and a <c>.lnk</c> that is not a shell link both answer <see langword="false"/> without throwing.</summary>
    [Fact]
    public void TryGetShortcutRawTarget_MissingOrCorruptLink_ReturnsFalse()
    {
        var corrupt = Path.Combine(_directory, "corrupt.lnk");
        File.WriteAllBytes(corrupt, [1, 2, 3, 4, 5, 6, 7, 8]);
        var missing = Path.Combine(_directory, "missing.lnk");

        var results = StaTestRunner.Run(() =>
        (
            Missing: _nativeApi.TryGetShortcutRawTarget(missing, out var missingTarget),
            MissingTarget: missingTarget,
            Corrupt: _nativeApi.TryGetShortcutRawTarget(corrupt, out var corruptTarget),
            CorruptTarget: corruptTarget,
            Empty: _nativeApi.TryGetShortcutRawTarget(string.Empty, out _)));

        Assert.False(results.Missing);
        Assert.Null(results.MissingTarget);
        Assert.False(results.Corrupt);
        Assert.Null(results.CorruptTarget);
        Assert.False(results.Empty);
    }

    /// <summary>The system drive is a fixed disk.</summary>
    [Fact]
    public void GetDriveKind_SystemDrive_IsFixed()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.Equal(DriveKind.Fixed, _nativeApi.GetDriveKind(root));
    }

    /// <summary>A letter with no volume is <see cref="DriveKind.NoRootDirectory"/>; an empty root never falls back to the current directory.</summary>
    [Fact]
    public void GetDriveKind_UnusedLetterOrEmpty_IsNotRemote()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();

        // L9: FirstOrDefault (not First) — on a CI runner with every letter D..Z mapped (extremely unlikely,
        // but possible with enough mapped/virtual drives) there is no unused letter at all; skip just that
        // half of the assertion rather than failing the whole test with an unrelated
        // InvalidOperationException.
        var unused = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).FirstOrDefault(c => !used.Contains(c));

        if (unused != default)
        {
            Assert.Equal(DriveKind.NoRootDirectory, _nativeApi.GetDriveKind($@"{unused}:\"));
        }

        Assert.Equal(DriveKind.Unknown, _nativeApi.GetDriveKind(string.Empty));
    }

    private static void CreateShortcut(string linkPath, string targetPath)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
        var link = Activator.CreateInstance(type)!;
        try
        {
            Marshal.ThrowExceptionForHR(((IShellLinkWForTests)link).SetPath(targetPath));
            ((ComTypes.IPersistFile)link).Save(linkPath, fRemember: true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// Test-only <c>IShellLinkW</c> declaration up to <c>SetPath</c> (its 18th method), used to WRITE links.
    /// The first 17 methods are never called, so they are declared parameterless: each still occupies exactly
    /// one vtable slot, which is all that matters. Order MUST match the native vtable.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkWForTests
    {
        /// <summary>Vtable slot: <c>GetPath</c> (never called).</summary>
        void GetPath();

        /// <summary>Vtable slot: <c>GetIDList</c> (never called).</summary>
        void GetIDList();

        /// <summary>Vtable slot: <c>SetIDList</c> (never called).</summary>
        void SetIDList();

        /// <summary>Vtable slot: <c>GetDescription</c> (never called).</summary>
        void GetDescription();

        /// <summary>Vtable slot: <c>SetDescription</c> (never called).</summary>
        void SetDescription();

        /// <summary>Vtable slot: <c>GetWorkingDirectory</c> (never called).</summary>
        void GetWorkingDirectory();

        /// <summary>Vtable slot: <c>SetWorkingDirectory</c> (never called).</summary>
        void SetWorkingDirectory();

        /// <summary>Vtable slot: <c>GetArguments</c> (never called).</summary>
        void GetArguments();

        /// <summary>Vtable slot: <c>SetArguments</c> (never called).</summary>
        void SetArguments();

        /// <summary>Vtable slot: <c>GetHotkey</c> (never called).</summary>
        void GetHotkey();

        /// <summary>Vtable slot: <c>SetHotkey</c> (never called).</summary>
        void SetHotkey();

        /// <summary>Vtable slot: <c>GetShowCmd</c> (never called).</summary>
        void GetShowCmd();

        /// <summary>Vtable slot: <c>SetShowCmd</c> (never called).</summary>
        void SetShowCmd();

        /// <summary>Vtable slot: <c>GetIconLocation</c> (never called).</summary>
        void GetIconLocation();

        /// <summary>Vtable slot: <c>SetIconLocation</c> (never called).</summary>
        void SetIconLocation();

        /// <summary>Vtable slot: <c>SetRelativePath</c> (never called).</summary>
        void SetRelativePath();

        /// <summary>Vtable slot: <c>Resolve</c> (never called).</summary>
        void Resolve();

        /// <summary><c>IShellLinkW.SetPath</c>: sets the link target.</summary>
        /// <param name="pszFile">The target path.</param>
        /// <returns>HRESULT.</returns>
        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
