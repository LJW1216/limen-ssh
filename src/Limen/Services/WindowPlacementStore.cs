using System.IO;
using System.Text.Json;

namespace Limen;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

public static class WindowPlacementStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Limen", "window.json");

    public static WindowPlacement? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(placement));
        }
        catch
        {
        }
    }
}
