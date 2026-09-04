using System.IO;

namespace Limen;

/// Terminal preferences the user sets from inside the terminal itself, kept
/// beside the theme choice so both survive a restart.
public static class TerminalPrefs
{
    public const int DefaultFontSize = 14;
    public const int MinFontSize = 9;
    public const int MaxFontSize = 28;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Limen", "terminal.txt");

    private static int? _fontSize;

    public static int FontSize
    {
        get => _fontSize ??= Load();
        set
        {
            var clamped = Math.Clamp(value, MinFontSize, MaxFontSize);
            if (_fontSize == clamped) return;
            _fontSize = clamped;
            Save(clamped);
        }
    }

    private static int Load()
    {
        try
        {
            if (File.Exists(FilePath) && int.TryParse(File.ReadAllText(FilePath).Trim(), out var stored))
                return Math.Clamp(stored, MinFontSize, MaxFontSize);
        }
        catch
        {
        }
        return DefaultFontSize;
    }

    private static void Save(int size)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, size.ToString());
        }
        catch
        {
        }
    }
}
