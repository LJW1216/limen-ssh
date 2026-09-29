using System.IO;
using System.Text;

namespace Limen;

/// Writes terminal output to a plain text file. Escape sequences are stripped
/// on the way in — a log full of raw CSI codes is unreadable in every viewer
/// that is not a terminal, which defeats the point of keeping one.
public sealed class SessionLog : IDisposable
{
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    private readonly TextWriter _writer;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();
    private char[] _chars = new char[8 * 1024];
    private State _state = State.Text;
    private bool _disposed;

    /// Where the escape parser currently is, kept across chunk boundaries
    /// because a sequence can be split between two reads.
    private enum State
    {
        Text,
        Seen,
        Csi,
        Osc,
        OscEnd
    }

    public string Path { get; }

    /// Raised once, from the reading thread, when the file stops accepting
    /// writes — a full disk, a removed drive. The log stops itself; the
    /// session it was recording must not stop with it.
    public event Action<Exception>? Faulted;

    public SessionLog(string path, string header)
        : this(new StreamWriter(Prepare(path), append: true, Encoding.UTF8) { AutoFlush = true },
            System.IO.Path.GetFullPath(path), header)
    {
    }

    internal SessionLog(TextWriter writer, string path, string header)
    {
        Path = path;
        _writer = writer;
        _writer.WriteLine(header);
    }

    private static string Prepare(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    public void Append(byte[] buffer, int count)
    {
        Exception? fault = null;
        lock (_gate)
        {
            if (_disposed || count <= 0) return;

            var needed = _decoder.GetCharCount(buffer, 0, count, flush: false);
            if (needed > _chars.Length) _chars = new char[needed];
            var produced = _decoder.GetChars(buffer, 0, count, _chars, 0, flush: false);

            _text.Clear();
            for (var i = 0; i < produced; i++) Consume(_chars[i]);
            if (_text.Length == 0) return;

            // One write per chunk: the writer flushes after every call, and a
            // flush per character would be a system call per character.
            try
            {
                _writer.Write(_text);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                fault = ex;
                _disposed = true;
                try { _writer.Dispose(); } catch (IOException) { }
            }
        }
        if (fault is not null) Faulted?.Invoke(fault);
    }

    private void Consume(char c)
    {
        switch (_state)
        {
            case State.Text:
                if (c == Escape) _state = State.Seen;
                else Emit(c);
                return;

            case State.Seen:
                _state = c switch
                {
                    '[' => State.Csi,
                    ']' => State.Osc,
                    _ => State.Text          // two-character escape, already consumed
                };
                return;

            case State.Csi:
                // Parameter and intermediate bytes, terminated by @ through ~.
                if (c is >= '@' and <= '~') _state = State.Text;
                return;

            case State.Osc:
                if (c == Bell) _state = State.Text;
                else if (c == Escape) _state = State.OscEnd;
                return;

            case State.OscEnd:
                _state = State.Text;         // ST terminator, or a stray escape
                return;
        }
    }

    private void Emit(char c)
    {
        switch (c)
        {
            case '\n':
                _text.Append(Environment.NewLine);
                return;
            case '\r':
                return;                      // progress redraws would double the lines
            case '\t':
                _text.Append(c);
                return;
            default:
                if (!char.IsControl(c)) _text.Append(c);
                return;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.WriteLine();
                _writer.WriteLine(Strings.Format("Log.Ended", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
