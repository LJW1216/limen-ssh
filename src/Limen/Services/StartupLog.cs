using System.IO;
using System.Security.Principal;
using System.Text;

namespace Limen;

/// Records what the app actually saw when it looked for the session store.
/// When the list comes up empty the question is always the same — which path,
/// did it exist, and as whom — so the app answers it in writing.
public static class StartupLog
{
    private static readonly string FilePath =
        Path.Combine(Path.GetTempPath(), "Limen-startup.log");

    public static void Record(string storePath, int loaded, Exception? failure)
    {
        try
        {
            var folder = Path.GetDirectoryName(storePath) ?? string.Empty;
            var report = new StringBuilder()
                .AppendLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---")
                .AppendLine($"exe        : {Environment.ProcessPath}")
                .AppendLine($"user       : {Environment.UserName} / {Identity()}")
                .AppendLine($"elevated   : {IsElevated()}")
                .AppendLine($"APPDATA    : {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}")
                .AppendLine($"store path : {storePath}")
                .AppendLine($"file exists: {File.Exists(storePath)}")
                .AppendLine($"file size  : {Size(storePath)}")
                .AppendLine($"folder     : {folder}")
                .AppendLine($"folder sees: {Listing(folder)}")
                .AppendLine($"loaded     : {loaded}개")
                .AppendLine($"failure    : {failure?.GetType().Name} {failure?.Message}");
            File.AppendAllText(FilePath, report.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }

    private static string Identity()
    {
        try { return WindowsIdentity.GetCurrent().Name; }
        catch { return "?"; }
    }

    private static string IsElevated()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator).ToString(); }
        catch { return "?"; }
    }

    private static string Size(string path)
    {
        try { return File.Exists(path) ? $"{new FileInfo(path).Length:N0} bytes" : "-"; }
        catch (Exception ex) { return $"읽기 실패: {ex.GetType().Name}"; }
    }

    private static string Listing(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "폴더 없음";
            var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
            return names.Length == 0 ? "(비어 있음)" : string.Join(", ", names);
        }
        catch (Exception ex)
        {
            return $"열람 실패: {ex.GetType().Name}";
        }
    }
}
