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

        HeadingText.Text = lines.Length == 1
            ? Strings.Get("Paste.TrailingNewline")
            : Strings.Format("Paste.LineCount", lines.Length);
        PreviewText.Text = string.Join(Environment.NewLine, lines.Take(PreviewLines));

        if (lines.Length > PreviewLines)
        {
            MoreText.Text = Strings.Format("Paste.More", lines.Length - PreviewLines);
            MoreText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => AcceptButton.Focus();
    }

    /// True when any line break is present — a trailing one included. A single
    /// command copied together with its line ending runs the instant it lands,
    /// which is exactly the paste nobody meant to execute.
    public static bool NeedsConfirmation(string text) => text.Contains('\n') || text.Contains('\r');

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
