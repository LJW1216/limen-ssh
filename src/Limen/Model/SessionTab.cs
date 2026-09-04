using System.ComponentModel;
using System.Windows.Controls;

namespace Limen;

public sealed class SessionTab : INotifyPropertyChanged
{
    private bool _connected;

    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required SshProfile Profile { get; init; }
    public required UserControl Content { get; init; }

    public bool Connected
    {
        get => _connected;
        set
        {
            if (_connected == value) return;
            _connected = value;
            Raise(nameof(Connected));
            Raise(nameof(Header));
            Raise(nameof(Tip));
        }
    }

    /// Plain-text form, used where a template is not available (window title).
    public string Header => $"{(Connected ? "●" : "○")} {Kind}  {Title}";

    public bool HasColor => SessionColors.IsTagged(Profile.Color);
    public System.Windows.Media.SolidColorBrush? Accent => SessionColors.Brush(Profile.Color);

    public string Tip => $"{Kind} · {Profile.DisplayPath}\n{Profile.RoutedTarget}\n{(Connected ? "연결됨" : "끊김")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
