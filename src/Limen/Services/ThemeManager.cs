using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Limen;

/// Swaps the design tokens declared in Theme.xaml. Every token is a brush the
/// XAML reaches through DynamicResource, so a toggle repaints live without
/// rebuilding a single view.
public static class ThemeManager
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Limen", "theme.txt");

    public static bool IsDark { get; private set; }

    /// Raised after a swap so views holding non-WPF surfaces (the WebView2
    /// terminal) can repaint themselves.
    public static event Action<bool>? Changed;

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Chrome"] = "#F3F4F6",
        ["ChromeAlt"] = "#E9EBEF",
        ["Panel"] = "#FFFFFF",
        ["PanelAlt"] = "#F9FAFB",
        ["Line"] = "#E3E5EA",
        ["LineStrong"] = "#CBD0D8",

        ["Ink"] = "#16191D",
        ["Muted"] = "#5B6472",
        ["Faint"] = "#858D9A",
        ["DisabledInk"] = "#AFB5BE",

        ["Accent"] = "#1F6FEB",
        ["AccentHover"] = "#1A61D1",
        ["AccentPressed"] = "#154FAE",
        ["AccentSoft"] = "#E8F0FE",
        ["OnAccent"] = "#FFFFFF",
        ["FocusRing"] = "#8CBAF8",

        ["Success"] = "#15A44A",
        ["Warn"] = "#D97706",
        ["Danger"] = "#D92D20",
        ["DangerSoft"] = "#FDECEA",
        ["Idle"] = "#B4BAC3",

        ["FieldBack"] = "#FFFFFF",
        ["ButtonBack"] = "#FFFFFF",
        ["ButtonHover"] = "#F2F4F7",
        ["ButtonPressed"] = "#E4E7EC",
        ["ButtonDisabled"] = "#F4F5F7",
        ["HoverBack"] = "#F1F3F6",
        ["OverlayBack"] = "#FBFBFC",
        ["ScrollThumb"] = "#C6CBD3",
        ["ScrollThumbOver"] = "#A2A9B4",
        ["TerminalBack"] = "#FBFBFC",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Chrome"] = "#22262C",
        ["ChromeAlt"] = "#2C313A",
        ["Panel"] = "#191C21",
        ["PanelAlt"] = "#1F232A",
        ["Line"] = "#2F343C",
        ["LineStrong"] = "#3D444E",

        ["Ink"] = "#E7EBF1",
        ["Muted"] = "#A6AEBB",
        ["Faint"] = "#7C8492",
        ["DisabledInk"] = "#5C646F",

        ["Accent"] = "#4C8DF6",
        ["AccentHover"] = "#639BF8",
        ["AccentPressed"] = "#3A78DC",
        ["AccentSoft"] = "#1D3050",
        ["OnAccent"] = "#0B1220",
        ["FocusRing"] = "#3E7AC4",

        ["Success"] = "#3FBF6E",
        ["Warn"] = "#E9A23B",
        ["Danger"] = "#F0685F",
        ["DangerSoft"] = "#3A2426",
        ["Idle"] = "#5D6570",

        ["FieldBack"] = "#14171C",
        ["ButtonBack"] = "#272C33",
        ["ButtonHover"] = "#31373F",
        ["ButtonPressed"] = "#3A414A",
        ["ButtonDisabled"] = "#1E2127",
        ["HoverBack"] = "#252A31",
        ["OverlayBack"] = "#191C21",
        ["ScrollThumb"] = "#3B424B",
        ["ScrollThumbOver"] = "#4E5661",
        ["TerminalBack"] = "#14161A",
    };

    public static void Load()
    {
        try { Apply(File.Exists(PreferencePath) && File.ReadAllText(PreferencePath).Trim() == "dark"); }
        catch { Apply(false); }
    }

    public static void Toggle() => Set(!IsDark, save: true);

    public static void Apply(bool dark) => Set(dark, save: false);

    private static void Set(bool dark, bool save)
    {
        IsDark = dark;
        var tokens = dark ? Dark : Light;

        foreach (var (key, value) in tokens)
            Application.Current.Resources[key] = Brush(value);

        // WPF paints TextBox/PasswordBox selection from system colors, which the
        // templates above cannot reach.
        Application.Current.Resources[SystemColors.ControlTextBrushKey] = Brush(tokens["Ink"]);
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Brush(tokens["Ink"]);
        Application.Current.Resources[SystemColors.HighlightBrushKey] = Brush(dark ? "#2F5D96" : "#BBD6FA");
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Brush(dark ? "#FFFFFF" : "#0B1220");
        Application.Current.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brush(tokens["HoverBack"]);
        Application.Current.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brush(tokens["Ink"]);
        Application.Current.Resources[SystemColors.GrayTextBrushKey] = Brush(tokens["DisabledInk"]);

        Changed?.Invoke(dark);

        if (!save) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, dark ? "dark" : "light");
        }
        catch { }
    }

    /// Hex token lookup for surfaces WPF brushes cannot reach — currently the
    /// xterm.js palette inside WebView2.
    public static string Token(string key) => (IsDark ? Dark : Light).GetValueOrDefault(key, "#000000");

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
