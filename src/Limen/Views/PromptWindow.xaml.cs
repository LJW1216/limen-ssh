using System.Windows;
using System.Windows.Input;

namespace Limen;

public partial class PromptWindow : Window
{
    private readonly bool _masked;

    public string Value => _masked ? MaskedBox.Password : PlainBox.Text;
    public bool Remember => RememberBox.IsChecked == true;

    public PromptWindow(string title, string message, bool canRemember, bool masked = true,
        string initial = "")
    {
        InitializeComponent();
        _masked = masked;
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        MaskedBox.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
        PlainBox.Visibility = masked ? Visibility.Collapsed : Visibility.Visible;
        RememberBox.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;
        PlainBox.Text = initial;
        Loaded += (_, _) =>
        {
            if (masked)
            {
                MaskedBox.Focus();
                return;
            }
            PlainBox.Focus();
            PlainBox.SelectAll();
        };
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
