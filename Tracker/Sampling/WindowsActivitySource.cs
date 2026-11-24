using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Chronoscope.Tracker.Sampling;

// Windows backend. Three documented Win32 questions, asked once per poll:
//   GetForegroundWindow  -> which window has focus
//   GetWindowText / GetWindowThreadProcessId / QueryFullProcessImageName -> its title and exe
//   GetLastInputInfo     -> how long since the last key or mouse event
// Polling rather than SetWinEventHook keeps this a plain loop with no message pump; at 1 s it
// costs next to nothing, and a hook can be swapped in later without touching anything else.
[SupportedOSPlatform("windows")]
internal sealed class WindowsActivitySource : IActivitySource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const string UwpFrameHost = "ApplicationFrameHost";

    // FileVersionInfo opens the exe and parses its resources; it never changes, so once per path.
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);

    public ActivitySample Poll()
    {
        TimeSpan idle = ReadIdleTime();

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return new ActivitySample(idle, null);
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string? exePath = ReadExePath(pid);

        // A UWP app (Calculator, Settings, Photos...) is hosted in a frame window owned by
        // ApplicationFrameHost.exe; the real app is a child window in another process.
        // Reporting the host would lump every UWP app together.
        if (exePath is not null && Path.GetFileNameWithoutExtension(exePath)
                .Equals(UwpFrameHost, StringComparison.OrdinalIgnoreCase))
        {
            string? realExe = FindHostedAppExe(hwnd, pid);
            if (realExe is not null)
            {
                exePath = realExe;
            }
        }

        string process = exePath is null ? "unknown" : Path.GetFileNameWithoutExtension(exePath);
        return new ActivitySample(idle, new WindowInfo(ReadTitle(hwnd), process, ResolveAppName(exePath, process), exePath));
    }

    private static TimeSpan ReadIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values are 32-bit tick counts that wrap every ~49.7 days; unchecked subtraction
        // is correct across the wrap.
        uint now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - info.Time));
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // PROCESS_QUERY_LIMITED_INFORMATION is enough for this and, unlike the rights
    // Process.MainModule needs, is granted even for elevated processes.
    private static string? ReadExePath(uint pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? FindHostedAppExe(IntPtr frameWindow, uint frameHostPid)
    {
        string? found = null;
        EnumChildWindows(frameWindow, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint childPid);
            if (childPid == frameHostPid)
            {
                return true;
            }

            found = ReadExePath(childPid);
            return found is null;   // keep looking only if this one could not be read
        }, IntPtr.Zero);
        return found;
    }

    // The human-readable app name, chosen to reproduce ManicTime's, because Chronoscope's tags
    // are keyed on it. Inferred from comparing the two tools, not documented:
    //   devenv.exe    ProductName "Microsoft® Visual Studio®"         -> ManicTime shows the product
    //   notepad.exe   ProductName "Notepad", description "Notepad.exe" -> the product, not the description
    //   explorer.exe  ProductName is the generic OS string             -> ManicTime shows the description
    // So: ProductName, unless it is the shared "Windows Operating System" string, then
    // FileDescription; then the bare file name for exes that carry no version resource.
    private string ResolveAppName(string? exePath, string process)
    {
        if (exePath is null)
        {
            return process;
        }

        if (_appNames.TryGetValue(exePath, out string? cached))
        {
            return cached;
        }

        string name = process;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(exePath);
            string product = version.ProductName?.Trim() ?? "";
            string description = version.FileDescription?.Trim() ?? "";

            bool genericProduct = product.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                                  && product.Contains("Operating System", StringComparison.OrdinalIgnoreCase);

            if (product.Length > 0 && !genericProduct)
            {
                name = product;
            }
            else if (description.Length > 0)
            {
                name = description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? description[..^4] : description;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        _appNames[exePath] = name;
        return name;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
