using System.Windows.Media;

namespace Limen;

/// The tag colours a session can carry. Deliberately few and far apart: the
/// point is telling production from staging at a glance, not decoration.
public static class SessionColors
{
    public sealed record Choice(string NameKey, string Hex)
    {
        public string Name => Strings.Get(NameKey);
    }

    public static readonly IReadOnlyList<Choice> Palette =
    [
        new("Colour.None", ""),
        new("Colour.Red", "#E5484D"),
        new("Colour.Orange", "#E8963C"),
        new("Colour.Yellow", "#D9B23A"),
        new("Colour.Green", "#3FBF6E"),
        new("Colour.Blue", "#4C8DF6"),
        new("Colour.Purple", "#A97BE8"),
        new("Colour.Grey", "#8B94A3"),
    ];

    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    public static bool IsTagged(string hex) => hex.Trim().Length > 0;

    /// Null when untagged, so callers can fall back to a theme brush.
    public static SolidColorBrush? Brush(string hex)
    {
        var key = hex.Trim();
        if (key.Length == 0) return null;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(key));
            brush.Freeze();
            Cache[key] = brush;
            return brush;
        }
        catch
        {
            return null;
        }
    }
}
