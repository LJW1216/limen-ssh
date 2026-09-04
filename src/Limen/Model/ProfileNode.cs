using System.Collections.ObjectModel;

namespace Limen;

public sealed class ProfileNode
{
    public required string Name { get; init; }
    public SshProfile? Profile { get; init; }
    public ObservableCollection<ProfileNode> Children { get; } = [];

    public bool IsFolder => Profile is null;
    public bool HasColor => Profile is not null && SessionColors.IsTagged(Profile.Color);
    public System.Windows.Media.SolidColorBrush? Accent =>
        Profile is null ? null : SessionColors.Brush(Profile.Color);
    public string ToolTip => Profile is null ? Name : Profile.RoutedTarget;

    /// Row detail: the connection target under a session name, the number of
    /// sessions beside a folder name.
    public string Subtitle => Profile is null ? CountLabel() : Profile.Target;

    private string CountLabel()
    {
        var sessions = Count(this);
        return sessions == 0 ? string.Empty : sessions.ToString();
    }

    private static int Count(ProfileNode node) =>
        node.Children.Sum(child => child.IsFolder ? Count(child) : 1);

    public override string ToString() => Name;
}
