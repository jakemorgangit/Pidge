using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using QuickMail.Data;

namespace QuickMail.Helpers;

public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmBorderColor = 34;

    /// <summary>Gives a standard window a dark title bar that blends into the app background.</summary>
    public static void ApplyDark(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
            int caption = 0x00160F0E; // COLORREF (BGR) for #0E1016
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            int border = 0x003A2B26;
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
        };
    }
}

public static class WebViewEnv
{
    private static Task<CoreWebView2Environment>? _env;

    /// <summary>One shared WebView2 environment with its profile in LocalAppData.</summary>
    public static Task<CoreWebView2Environment> GetAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Db.DataDir, "WebView2"));
}
