using System.IO;

namespace Limen;

public static class AtomicDownload
{
    public static async Task WriteAsync(string directory, string name,
        Func<Stream, CancellationToken, Task> download, CancellationToken token)
    {
        var target = TransferPaths.LocalChild(directory, name);
        var temporary = Path.Combine(directory, $".limen-{Guid.NewGuid():N}.part");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await download(stream, token);
            token.ThrowIfCancellationRequested();
            TransferPaths.LocalChild(directory, name);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
