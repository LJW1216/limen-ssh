using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Limen;

/// Attached properties the theme templates read. Keeps the XAML declarative
/// instead of scattering placeholder/corner plumbing through code-behind.
public static class Ui
{
    /// Watermark shown inside an empty TextBox / PasswordBox.
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui),
            new PropertyMetadata(string.Empty));

    public static void SetPlaceholder(DependencyObject o, string v) => o.SetValue(PlaceholderProperty, v);
    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);

    /// True while a PasswordBox holds no characters. PasswordBox has no bindable
    /// Password, so the theme template drives the watermark off this instead.
    public static readonly DependencyProperty IsEmptyProperty =
        DependencyProperty.RegisterAttached("IsEmpty", typeof(bool), typeof(Ui),
            new PropertyMetadata(true));

    public static void SetIsEmpty(DependencyObject o, bool v) => o.SetValue(IsEmptyProperty, v);
    public static bool GetIsEmpty(DependencyObject o) => (bool)o.GetValue(IsEmptyProperty);

    /// Opt-in watermark tracking for PasswordBox.
    public static readonly DependencyProperty TrackEmptyProperty =
        DependencyProperty.RegisterAttached("TrackEmpty", typeof(bool), typeof(Ui),
            new PropertyMetadata(false, OnTrackEmptyChanged));

    public static void SetTrackEmpty(DependencyObject o, bool v) => o.SetValue(TrackEmptyProperty, v);
    public static bool GetTrackEmpty(DependencyObject o) => (bool)o.GetValue(TrackEmptyProperty);

    private static void OnTrackEmptyChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not PasswordBox box) return;
        if (e.NewValue is true)
        {
            box.PasswordChanged += PasswordChanged;
            SetIsEmpty(box, box.Password.Length == 0);
        }
        else
        {
            box.PasswordChanged -= PasswordChanged;
        }
    }

    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        SetIsEmpty(box, box.Password.Length == 0);
    }

    /// Corner radius applied by templated controls that expose one.
    public static readonly DependencyProperty CornerProperty =
        DependencyProperty.RegisterAttached("Corner", typeof(CornerRadius), typeof(Ui),
            new PropertyMetadata(new CornerRadius(6)));

    public static void SetCorner(DependencyObject o, CornerRadius v) => o.SetValue(CornerProperty, v);
    public static CornerRadius GetCorner(DependencyObject o) => (CornerRadius)o.GetValue(CornerProperty);

    /// Icon geometry rendered ahead of a button's text by the icon templates.
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(Ui),
            new PropertyMetadata(null));

    public static void SetIcon(DependencyObject o, Geometry? v) => o.SetValue(IconProperty, v);
    public static Geometry? GetIcon(DependencyObject o) => (Geometry?)o.GetValue(IconProperty);
}
