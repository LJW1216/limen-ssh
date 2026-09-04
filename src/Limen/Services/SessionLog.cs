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

    private readonly StreamWriter _writer;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
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

    public SessionLog(string path, string header)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine(header);
    }

    public void Append(byte[] buffer, int count)
    {
        lock (_gate)
        {
            if (_disposed || count <= 0) return;

            var needed = _decoder.GetCharCount(buffer, 0, count, flush: false);
            if (needed > _chars.Length) _chars = new char[needed];
            var produced = _decoder.GetChars(buffer, 0, count, _chars, 0, flush: false);

            for (var i = 0; i < produced; i++) Consume(_chars[i]);
        }
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
                _writer.Write(Environment.NewLine);
                return;
            case '\r':
                return;                      // progress redraws would double the lines
            case '\t':
                _writer.Write(c);
                return;
            default:
                if (!char.IsControl(c)) _writer.Write(c);
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
