using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Limen;

/// Paints the native window frame to match the app theme. Without this a dark
/// app still gets a white Windows title bar, which is the loudest possible
/// "the theme is only skin deep" tell.
public static class TitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// Follows every window the app opens, now and later.
    public static void Attach()
    {
        ThemeManager.Changed += _ => ApplyToAll();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));
    }

    public static void ApplyToAll()
    {
        foreach (Window window in Application.Current.Windows) Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = ThemeManager.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Pre-Windows-10 shells simply keep the system frame.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
