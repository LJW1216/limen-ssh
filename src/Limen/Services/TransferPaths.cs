using System.IO;

namespace Limen;

public static class TransferPaths
{
    // POSIX names can contain backslashes and spaces. Resolve '..' on the
    // server: collapsing it here would change the meaning after a symlink.
    public static string ResolveRemote(string path, string current, string home)
    {
        if (path.Length == 0) return current;
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal)) return CombineRemote(home, path[2..]);
        return path.StartsWith('/') ? path : CombineRemote(current, path);
    }

    public static string CombineRemote(string directory, string name) =>
        directory.TrimEnd('/') + "/" + name;

    public static bool IsRemoteName(string name) =>
        name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(['/', '\0']) < 0;

    public static bool IsLocalName(string name)
    {
        if (!IsRemoteName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ')) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
              "123456789¹²³".Contains(stem[3]));
    }

    public static string LocalChild(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException(Strings.Get("Sftp.NoLocalDirectory"));
        if (!IsLocalName(name)) throw new IOException(Strings.Format("Sftp.InvalidLocalName", name));
        var target = Path.GetFullPath(Path.Combine(directory, name));
        // Never write through an existing junction or symbolic link.
        for (var ancestor = new FileInfo(target) as FileSystemInfo; ancestor is not null;
             ancestor = Directory.GetParent(ancestor.FullName))
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0 && ancestor.Attributes != (FileAttributes)(-1))
                throw new IOException(Strings.Format("Sftp.LinkTransfer", ancestor.FullName));
        return target;
    }
}
