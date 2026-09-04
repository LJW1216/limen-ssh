using System.Windows;

namespace Limen;

/// Guard for multi-line pastes. A shell runs every line the moment it arrives,
/// so the one thing worth interrupting for is text the user did not realise
/// carried newlines.
public partial class ConfirmPasteWindow : Window
{
    private const int PreviewLines = 10;

    public ConfirmPasteWindow(string text)
    {
        InitializeComponent();

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .TrimEnd('\n')
            .Split('\n');

        HeadingText.Text = Strings.Format("Paste.LineCount", lines.Length);
        PreviewText.Text = string.Join(Environment.NewLine, lines.Take(PreviewLines));

        if (lines.Length > PreviewLines)
        {
            MoreText.Text = Strings.Format("Paste.More", lines.Length - PreviewLines);
            MoreText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => AcceptButton.Focus();
    }

    /// True when the text would run more than one command on arrival.
    public static bool NeedsConfirmation(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Contains('\n');

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
