using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using static DpiProbe.NativeMethods;

namespace DpiProbe;

/// <summary>
/// F0 DPI spike (docs/PLANS/release-hardening-1.0.md, package F, sub-package F0).
/// Answers one question: does a window embedded into the desktop layer (the way
/// DesktopIntegrationService.EmbedInDesktop does it) get per-monitor DPI, or the single DPI of its
/// top-level parent? Read-only with respect to the product: it only creates and destroys its own windows.
/// </summary>
internal static class Program
{
    private const string ClassName = "DesktopOrganizer.DpiProbe.Window";
    private const uint ProgmanSpawnWorkerW = 0x052C;

    private static readonly WndProc WindowProcDelegate = WindowProc;
    private static readonly List<string> Events = [];
    private static readonly List<MonitorRecord> Monitors = [];

    private static int holdSeconds;

    private static int Main(string[] args)
    {
        // Usage: dpi-probe [processName] [--hold seconds]
        //   processName   running process whose windows are inspected read-only (default: the product).
        //   --hold N      keep every test window alive N extra seconds after the moves and log DPI messages,
        //                 so the display scale can be changed in Settings mid-run (manual verify).
        var inspectProcess = "DesktopOrganizer.UI";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--hold" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var seconds))
            {
                holdSeconds = seconds;
                i++;
            }
            else
            {
                inspectProcess = args[i];
            }
        }

        var instance = GetModuleHandleW(0);
        var cls = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
            HInstance = instance,
            HbrBackground = 6, // COLOR_WINDOW + 1
            LpszClassName = ClassName,
        };

        if (RegisterClassExW(ref cls) == 0)
        {
            Console.WriteLine($"FATAL: RegisterClassExW failed, error {Marshal.GetLastWin32Error()}");
            return 1;
        }

        try
        {
            PrintEnvironment();
            PrintMonitors();
            var shell = ProbeShell();
            InspectProcessWindows(inspectProcess, shell);

            // E0: control — a plain PMv2 top-level popup never reparented. This is what the product does
            // when GetOrCreateWorkerW() returns IntPtr.Zero (EmbedInDesktop silently skips SetParent).
            RunExperiment("E0 top-level PMv2 popup, NOT reparented (control)", 0, DpiAwarenessContextPerMonitorAwareV2);

            // E1: the product's own lookup result, if any.
            if (shell.AppWorkerW != 0)
            {
                RunExperiment("E1 PMv2 popup -> SetParent(product WorkerW lookup)", shell.AppWorkerW, DpiAwarenessContextPerMonitorAwareV2);
                RunExperiment("E2 SYSTEM_AWARE popup -> SetParent(product WorkerW lookup)", shell.AppWorkerW, DpiAwarenessContextSystemAware);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("E1/E2 skipped: the product's WorkerW lookup returned 0 on this build (EmbedInDesktop would NOT reparent).");
            }

            // E3/E4: the Windows 11 24H2+ desktop layout (WorkerW is a CHILD of Progman) and Progman itself,
            // i.e. what a lookup fixed for 24H2 would parent into.
            if (shell.ProgmanChildWorkerW != 0)
            {
                RunExperiment("E3 PMv2 popup -> SetParent(WorkerW child of Progman, 24H2 layout)", shell.ProgmanChildWorkerW, DpiAwarenessContextPerMonitorAwareV2);
            }

            if (shell.Progman != 0)
            {
                RunExperiment("E4 PMv2 popup -> SetParent(Progman)", shell.Progman, DpiAwarenessContextPerMonitorAwareV2);
                RunExperiment("E5 SYSTEM_AWARE popup -> SetParent(Progman)", shell.Progman, DpiAwarenessContextSystemAware);
            }

            Console.WriteLine();
            Console.WriteLine("DONE");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: {ex}");
            return 1;
        }
        finally
        {
            UnregisterClassW(ClassName, instance);
        }
    }

    private static void PrintEnvironment()
    {
        Header("ENVIRONMENT");
        Console.WriteLine($"OS                         : {Environment.OSVersion.VersionString} (64-bit process: {Environment.Is64BitProcess})");
        Console.WriteLine($"Process                    : pid {Environment.ProcessId}, {Environment.ProcessPath}");
        var processCtx = GetDpiAwarenessContextForProcess(0);
        Console.WriteLine($"Process awareness          : {DescribeContext(processCtx)}");
        Console.WriteLine($"Thread awareness           : {DescribeContext(GetThreadDpiAwarenessContext())}");
        Console.WriteLine($"GetDpiForSystem            : {DescribeDpi(GetDpiForSystem())}");
        Console.WriteLine($"GetSystemDpiForProcess     : {DescribeDpi(GetSystemDpiForProcess(Process.GetCurrentProcess().Handle))}");
        Console.WriteLine(
            $"Virtual screen (px)        : origin ({GetSystemMetrics(SmXVirtualScreen)},{GetSystemMetrics(SmYVirtualScreen)}) " +
            $"size {GetSystemMetrics(SmCxVirtualScreen)}x{GetSystemMetrics(SmCyVirtualScreen)}, monitors {GetSystemMetrics(SmCMonitors)}");
    }

    private static void PrintMonitors()
    {
        Header("MONITORS (EnumDisplayMonitors + GetDpiForMonitor)");
        EnumDisplayMonitors(0, 0, CollectMonitor, 0);

        foreach (var m in Monitors)
        {
            Console.WriteLine(
                $"{m.Device,-14} hmon=0x{m.Handle:X} primary={m.Primary,-5} rc(px)={m.Bounds} work(px)={m.WorkArea} " +
                $"effectiveDpi={DescribeDpi(m.EffectiveDpi)} rawDpi={m.RawDpi}");
        }

        static bool CollectMonitor(nint hMonitor, nint hdc, ref Rect rect, nint data)
        {
            var info = new MonitorInfoEx { CbSize = (uint)Marshal.SizeOf<MonitorInfoEx>(), SzDevice = string.Empty };
            GetMonitorInfoW(hMonitor, ref info);
            var eff = GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var effX, out _) == 0 ? effX : 0u;
            var raw = GetDpiForMonitor(hMonitor, MdtRawDpi, out var rawX, out _) == 0 ? rawX : 0u;
            Monitors.Add(new MonitorRecord(
                hMonitor,
                info.SzDevice,
                (info.DwFlags & MonitorInfoFPrimary) != 0,
                info.RcMonitor,
                info.RcWork,
                eff,
                raw));
            return true;
        }
    }

    private static ShellWindows ProbeShell()
    {
        Header("SHELL DESKTOP LAYER");

        // Mirrors DesktopIntegrationService.GetOrCreateWorkerW() exactly: FindWindow("Progman"),
        // SendMessage(progman, 0x052C, 0, 0), then EnumWindows looking for the top-level window that hosts
        // SHELLDLL_DefView and taking FindWindowEx(NULL, thatWindow, "WorkerW", NULL).
        var progman = FindWindowW("Progman", null);
        Console.WriteLine($"FindWindow(Progman)        : {DescribeWindow(progman)}");
        if (progman == 0) return new ShellWindows(0, 0, 0);

        var sent = SendMessageTimeoutW(progman, ProgmanSpawnWorkerW, 0, 0, SmtoAbortIfHung, 2000, out _);
        Console.WriteLine($"SendMessage(Progman,0x052C): {(sent != 0 ? "delivered" : $"FAILED/timeout err {Marshal.GetLastWin32Error()}")}");

        nint appWorkerW = 0;
        nint defViewHost = 0;
        EnumWindows(
            (hwnd, _) =>
            {
                var defView = FindWindowExW(hwnd, 0, "SHELLDLL_DefView", null);
                if (defView != 0)
                {
                    defViewHost = hwnd;
                    appWorkerW = FindWindowExW(0, hwnd, "WorkerW", null);
                }

                return true;
            },
            0);

        Console.WriteLine($"Top-level SHELLDLL_DefView host: {DescribeWindow(defViewHost)}");
        Console.WriteLine($"PRODUCT LOOKUP RESULT (WorkerW) : {DescribeWindow(appWorkerW)}");

        Console.WriteLine();
        Console.WriteLine("Direct children of Progman:");
        foreach (var child in DirectChildren(progman))
        {
            Console.WriteLine($"  {DescribeWindow(child)}");
        }

        var progmanChildWorkerW = FindWindowExW(progman, 0, "WorkerW", null);

        Console.WriteLine();
        Console.WriteLine("All top-level WorkerW windows:");
        EnumWindows(
            (hwnd, _) =>
            {
                if (ClassOf(hwnd) == "WorkerW") Console.WriteLine($"  {DescribeWindow(hwnd)}");
                return true;
            },
            0);

        Console.WriteLine();
        Console.WriteLine($"GetDpiForWindow(Progman)          = {DescribeDpi(GetDpiForWindow(progman))}");
        Console.WriteLine($"GetDpiForWindow(product WorkerW)  = {(appWorkerW == 0 ? "n/a (lookup returned 0)" : DescribeDpi(GetDpiForWindow(appWorkerW)))}");
        Console.WriteLine($"GetDpiForWindow(Progman>WorkerW)  = {(progmanChildWorkerW == 0 ? "n/a (no WorkerW child)" : DescribeDpi(GetDpiForWindow(progmanChildWorkerW)))}");

        return new ShellWindows(progman, appWorkerW, progmanChildWorkerW);
    }

    /// <summary>
    /// Read-only: lists where a running process's windows actually live (top-level vs. inside the
    /// desktop layer) and what DPI/awareness Windows reports for them. Nothing is modified.
    /// </summary>
    private static void InspectProcessWindows(string processName, ShellWindows shell)
    {
        Header($"RUNNING PROCESS '{processName}' (read-only inspection)");
        var pids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
        if (pids.Count == 0)
        {
            Console.WriteLine("not running");
            return;
        }

        Console.WriteLine($"pids: {string.Join(",", pids)}");
        Console.WriteLine("Top-level windows owned by the process (visible only):");
        EnumWindows(
            (hwnd, _) =>
            {
                if (pids.Contains(OwnerPid(hwnd)) && IsWindowVisible(hwnd)) Console.WriteLine($"  {DescribeWindow(hwnd)}");
                return true;
            },
            0);

        foreach (var (label, host) in new[] { ("Progman", shell.Progman), ("product WorkerW", shell.AppWorkerW), ("Progman>WorkerW", shell.ProgmanChildWorkerW) })
        {
            if (host == 0) continue;
            Console.WriteLine($"Direct children of {label} owned by the process:");
            foreach (var child in DirectChildren(host))
            {
                if (pids.Contains(OwnerPid(child))) Console.WriteLine($"  {DescribeWindow(child)}");
            }
        }
    }

    private static void RunExperiment(string title, nint parent, nint creationContext)
    {
        Header(title);
        Events.Clear();

        var primary = Monitors.FirstOrDefault(m => m.Primary) ?? Monitors.FirstOrDefault();
        var startX = primary is null ? 100 : primary.WorkArea.Left + 100;
        var startY = primary is null ? 100 : primary.WorkArea.Top + 100;

        var previous = SetThreadDpiAwarenessContext(creationContext);
        var hwnd = CreateWindowExW(
            WsExToolWindow | WsExNoActivate,
            ClassName,
            "dpi-probe",
            WsPopup,
            startX,
            startY,
            320,
            200,
            0,
            0,
            GetModuleHandleW(0),
            0);
        var createError = Marshal.GetLastWin32Error();
        SetThreadDpiAwarenessContext(previous);

        if (hwnd == 0)
        {
            Console.WriteLine($"CreateWindowEx FAILED, error {createError}");
            return;
        }

        var reparented = false;
        try
        {
            ShowWindow(hwnd, SwShowNoActivate);
            Pump();
            Snapshot("created (top-level)", hwnd);

            if (parent != 0)
            {
                var old = SetParent(hwnd, parent);
                var err = Marshal.GetLastWin32Error();
                reparented = old != 0 || err == 0;
                Console.WriteLine($"SetParent(child, {DescribeHandle(parent)}) -> prev=0x{old:X}, lastError={err}, reparented={reparented}");
                Pump();
                Snapshot("after SetParent", hwnd);
                var origin = default(Point);
                ClientToScreen(parent, ref origin);
                GetWindowRect(parent, out var parentRect);
                Console.WriteLine($"  parent client origin (screen px) = {origin}, parent window rect = {parentRect}");
            }

            foreach (var m in Monitors)
            {
                var x = m.Bounds.Left + 100;
                var y = m.Bounds.Top + 100;
                var ok = SetWindowPos(hwnd, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                var err = Marshal.GetLastWin32Error();
                Pump();
                GetWindowRect(hwnd, out var rect);
                var onMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNull);
                var monitorName = Monitors.FirstOrDefault(r => r.Handle == onMonitor)?.Device ?? $"0x{onMonitor:X}";
                Console.WriteLine();
                Console.WriteLine(
                    $"Move to {m.Device} (effective {DescribeDpi(m.EffectiveDpi)}): SetWindowPos({x},{y}) ok={ok} err={(ok ? 0 : err)} " +
                    $"-> GetWindowRect={rect} (delta vs requested: {rect.Left - x},{rect.Top - y})");
                Console.WriteLine(
                    $"  MonitorFromWindow={monitorName}  GetDpiForWindow(child)={DescribeDpi(GetDpiForWindow(hwnd))}  " +
                    $"awareness={DescribeContext(GetWindowDpiAwarenessContext(hwnd))}");
                FlushEvents();
            }

            if (holdSeconds > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"HOLD {holdSeconds}s: change the display scale now; DPI messages are logged as they arrive.");
                var until = Environment.TickCount64 + (holdSeconds * 1000L);
                while (Environment.TickCount64 < until)
                {
                    Pump();
                    if (Events.Count > 0)
                    {
                        Console.WriteLine($"  t-{(until - Environment.TickCount64) / 1000}s GetDpiForWindow(child)={DescribeDpi(GetDpiForWindow(hwnd))}");
                        FlushEvents();
                    }
                }

                Snapshot("after hold", hwnd);
            }
        }
        finally
        {
            if (reparented)
            {
                var back = SetParent(hwnd, 0);
                Console.WriteLine();
                Console.WriteLine($"cleanup: SetParent(child, NULL) -> prev=0x{back:X}, lastError={Marshal.GetLastWin32Error()}");
                Pump();
                Snapshot("after SetParent(NULL)", hwnd);
            }

            var destroyed = DestroyWindow(hwnd);
            Console.WriteLine($"cleanup: DestroyWindow -> {destroyed}");
            Pump();
            Events.Clear();
        }
    }

    private static void Snapshot(string label, nint hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        var style = (uint)GetWindowLongPtrW(hwnd, GwlStyle).ToInt64();
        var exStyle = (uint)GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        Console.WriteLine($"[{label}]");
        Console.WriteLine($"  GetDpiForWindow(child)     = {DescribeDpi(GetDpiForWindow(hwnd))}");
        Console.WriteLine($"  awareness(child)           = {DescribeContext(GetWindowDpiAwarenessContext(hwnd))}");
        Console.WriteLine($"  hostingBehavior            = {GetWindowDpiHostingBehavior(hwnd)} (0=DEFAULT,1=MIXED)");
        Console.WriteLine($"  GA_PARENT                  = {DescribeHandle(GetAncestor(hwnd, GaParent))}");
        Console.WriteLine($"  GA_ROOT                    = {DescribeHandle(GetAncestor(hwnd, GaRoot))}");
        Console.WriteLine(
            $"  style=0x{style:X8} (POPUP={(style & WsPopup) != 0}, CHILD={(style & WsChild) != 0}, VISIBLE={(style & WsVisible) != 0}) exStyle=0x{exStyle:X8}");
        Console.WriteLine($"  GetWindowRect              = {rect}");
        FlushEvents();
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WmDpiChanged:
                    var suggested = Marshal.PtrToStructure<Rect>(lParam);
                    Events.Add($"WM_DPICHANGED dpi={wParam.ToInt64() & 0xFFFF} suggested={suggested}");
                    break;
                case WmDpiChangedBeforeParent:
                    Events.Add($"WM_DPICHANGED_BEFOREPARENT (GetDpiForWindow now {GetDpiForWindow(hwnd)})");
                    break;
                case WmDpiChangedAfterParent:
                    Events.Add($"WM_DPICHANGED_AFTERPARENT (GetDpiForWindow now {GetDpiForWindow(hwnd)})");
                    break;
                case WmGetDpiScaledSize:
                    Events.Add($"WM_GETDPISCALEDSIZE newDpi={wParam.ToInt64()}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Events.Add($"wndproc logging failed: {ex.Message}");
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void Pump()
    {
        var until = Environment.TickCount64 + 250;
        while (Environment.TickCount64 < until)
        {
            while (PeekMessageW(out var msg, 0, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            Thread.Sleep(10);
        }
    }

    private static void FlushEvents()
    {
        Console.WriteLine(Events.Count == 0
            ? "  DPI messages received      : none"
            : "  DPI messages received      : " + string.Join("; ", Events));
        Events.Clear();
    }

    private static List<nint> DirectChildren(nint parent)
    {
        var result = new List<nint>();
        EnumChildWindows(
            parent,
            (hwnd, _) =>
            {
                if (GetAncestor(hwnd, GaParent) == parent) result.Add(hwnd);
                return true;
            },
            0);
        return result;
    }

    private static string DescribeWindow(nint hwnd)
    {
        if (hwnd == 0) return "0 (not found)";
        GetWindowRect(hwnd, out var rect);
        var tid = GetWindowThreadProcessId(hwnd, out var pid);
        return $"0x{hwnd:X} class={ClassOf(hwnd)} pid={pid} tid={tid} visible={IsWindowVisible(hwnd)} rect={rect} " +
               $"dpi={GetDpiForWindow(hwnd)} awareness={DescribeContext(GetWindowDpiAwarenessContext(hwnd))} " +
               $"GA_PARENT={DescribeHandle(GetAncestor(hwnd, GaParent))}";
    }

    private static uint OwnerPid(nint hwnd) => GetWindowThreadProcessId(hwnd, out var pid) != 0 ? pid : 0;

    private static string DescribeHandle(nint hwnd) => hwnd == 0 ? "0" : $"0x{hwnd:X}({ClassOf(hwnd)})";

    private static string ClassOf(nint hwnd)
    {
        unsafe
        {
            const int capacity = 256;
            var buffer = stackalloc char[capacity];
            var length = GetClassNameW(hwnd, buffer, capacity);
            return length > 0 ? new string(buffer, 0, length) : "?";
        }
    }

    private static string DescribeContext(nint context)
    {
        if (context == 0) return "null";
        var awareness = GetAwarenessFromDpiAwarenessContext(context) switch
        {
            0 => "UNAWARE",
            1 => "SYSTEM_AWARE",
            2 => "PER_MONITOR_AWARE",
            var other => $"INVALID({other})",
        };
        var v2 = AreDpiAwarenessContextsEqual(context, DpiAwarenessContextPerMonitorAwareV2) ? " (V2)" : string.Empty;
        var gdi = AreDpiAwarenessContextsEqual(context, DpiAwarenessContextUnawareGdiScaled) ? " (GDI scaled)" : string.Empty;
        var dpi = GetDpiFromDpiAwarenessContext(context);
        return $"{awareness}{v2}{gdi}{(dpi != 0 ? $" ctxDpi={dpi}" : string.Empty)}";
    }

    private static string DescribeDpi(uint dpi) =>
        dpi == 0 ? "0 (failed)" : string.Create(CultureInfo.InvariantCulture, $"{dpi} ({dpi * 100 / 96}%)");

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + title + " ===");
    }

    private sealed record MonitorRecord(nint Handle, string Device, bool Primary, Rect Bounds, Rect WorkArea, uint EffectiveDpi, uint RawDpi);

    private sealed record ShellWindows(nint Progman, nint AppWorkerW, nint ProgmanChildWorkerW);
}
