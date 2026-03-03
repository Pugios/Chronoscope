using System;
using System.Collections.Generic;
using System.Text;
using WinRT.Interop;

namespace TimeViewer.Platforms.Windows;
public class WindowService
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        var window = App.Current.Windows[0].Handler.PlatformView as Microsoft.UI.Xaml.Window;
        var hwnd = WindowNative.GetWindowHandle(window);
        var hWndInsertAfter = alwaysOnTop ? new IntPtr(-1) : new IntPtr(-2);
        SetWindowPos(hwnd, hWndInsertAfter, 0, 0, 0, 0, 0x0001 | 0x0002);
    }
}