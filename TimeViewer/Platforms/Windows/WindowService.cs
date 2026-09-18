using WinRT.Interop;

namespace TimeViewer.Platforms.Windows;
public class WindowService
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        // The whole chain is nullable during startup and teardown; nothing to pin if it is
        var platformWindow = Microsoft.Maui.Controls.Application.Current?
            .Windows.FirstOrDefault()?
            .Handler?.PlatformView as Microsoft.UI.Xaml.Window;

        if (platformWindow is null) return;

        var hwnd = WindowNative.GetWindowHandle(platformWindow);
        var hWndInsertAfter = alwaysOnTop ? new IntPtr(-1) : new IntPtr(-2);
        SetWindowPos(hwnd, hWndInsertAfter, 0, 0, 0, 0, 0x0001 | 0x0002);
    }
}