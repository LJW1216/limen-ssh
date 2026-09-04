namespace Limen;

public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public string Display => IsParent ? "상위 폴더" : Name;
    public string SizeText => IsDirectory || IsParent ? string.Empty : Format(Size);
    public string ModifiedText => IsParent ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm");

    /// Directories first, then case-insensitive name order.
    public static int Compare(FileEntry a, FileEntry b)
    {
        if (a.IsParent != b.IsParent) return a.IsParent ? -1 : 1;
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }
}
