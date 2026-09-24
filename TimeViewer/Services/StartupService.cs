using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TimeViewer;

// Starting with Windows, done the way Task Manager expects, so its Startup apps tab and the
// Settings page are two views of ONE switch rather than two settings that drift apart:
//
//  - HKCU\...\Run holds the command line. Its presence is what puts TimeViewer on Task Manager's
//    list at all, so it stays written while the switch is off.
//  - HKCU\...\Explorer\StartupApproved\Run holds the on/off state Task Manager toggles. It is
//    undocumented but stable since Windows 8: 12 bytes, the first one odd (03) when disabled and
//    even (02) when enabled, the rest a timestamp of the change. No value at all counts as enabled.
//
// "Start minimized" is not in settings.json: it is the --minimized flag on the Run command, so
// the registry alone says how TimeViewer starts and there is nothing to keep in step with it.
// The installer writes the same value (TimeViewer.iss) and removes both on uninstall.
public class StartupService
{
    public const string MinimizedArg = "--minimized";

    private const string ValueName = "TimeViewer";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // Everything here is registry, so on any other OS the Settings page hides the section
    public bool IsSupported => OperatingSystem.IsWindows();

    public (bool Enabled, bool Minimized) Read()
    {
        if (!OperatingSystem.IsWindows()) return (false, false);
        return ReadWindows();
    }

    // Returns false when the registry refused the write; the caller says so
    public bool Write(bool enabled, bool minimized)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            WriteWindows(enabled, minimized);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not change the startup entry: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (bool Enabled, bool Minimized) ReadWindows()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string command) return (false, false);

        bool minimized = command.Contains(MinimizedArg, StringComparison.OrdinalIgnoreCase);

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        bool disabled = approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;

        return (!disabled, minimized);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(bool enabled, bool minimized)
    {
        // Always the exe that is running now, so a moved install repairs its own entry on the
        // next Save. From a dev build this registers bin\Debug - expected, and fixed the same way.
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("The path of TimeViewer.exe is unknown.");
        string command = $"\"{exe}\"" + (minimized ? $" {MinimizedArg}" : "");

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, command, RegistryValueKind.String);

        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        var state = new byte[12];
        state[0] = enabled ? (byte)0x02 : (byte)0x03;
        // Task Manager stamps when an entry was disabled; do the same so it looks like its own
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(state, 4);
        approved.SetValue(ValueName, state, RegistryValueKind.Binary);
    }
}
