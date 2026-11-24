namespace Chronoscope.Tracker.Sampling;

// What the tracker sees on one poll. Everything OS-specific stays behind IActivitySource, so
// the recorder, the store and the reports never learn which platform they run on. Linux (X11 /
// Wayland) and macOS backends only have to produce these two records.

// The window that has focus. Process is the executable's file name without extension
// ("firefox"); AppName is the human-readable name ("Firefox"), which is what ManicTime shows
// and what Chronoscope's tags are keyed on.
public sealed record WindowInfo(string Title, string Process, string AppName, string? ExePath);

// Window is null when nothing has focus (locked screen, desktop switch). IdleFor is the time
// since the last keyboard or mouse input.
public sealed record ActivitySample(TimeSpan IdleFor, WindowInfo? Window);

public interface IActivitySource
{
    ActivitySample Poll();
}

public static class ActivitySourceFactory
{
    public static IActivitySource Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsActivitySource();
        }

        // Linux: X11 via _NET_ACTIVE_WINDOW + XScreenSaver idle time (Wayland needs a
        // compositor-specific route). macOS: NSWorkspace.frontmostApplication + CGEventSource idle.
        throw new PlatformNotSupportedException(
            "No activity source for this OS yet. Implement IActivitySource and return it here.");
    }
}
