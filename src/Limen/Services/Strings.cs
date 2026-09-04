using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Markup;

namespace Limen;

/// UI text for every supported language.
///
/// The tables live in code rather than .resx so a single-file publish keeps
/// working without satellite assemblies, and so switching language repaints the
/// live window instead of asking for a restart.
public sealed partial class Strings : INotifyPropertyChanged
{
    private static Strings? _current;

    /// Created on first use: the tables live in another partial file, and a
    /// field initializer here would run before they exist.
    public static Strings Current => _current ??= new Strings();

    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Limen", "language.txt");

    private Dictionary<string, string> _table;
    private string _language;

    private Strings()
    {
        _language = Load();
        _table = _language == "ko" ? Korean : English;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// "ko" or "en". Anything else falls back to English.
    public string Language
    {
        get => _language;
        set
        {
            var next = value == "ko" ? "ko" : "en";
            if (_language == next) return;
            _language = next;
            _table = next == "ko" ? Korean : English;
            // An empty indexer name tells every binding on this object to re-read.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    /// Bindable lookup. Unknown keys surface as the key itself so a missing
    /// translation is obvious rather than blank.
    public string this[string key] =>
        _table.TryGetValue(key, out var text) ? text
        : English.TryGetValue(key, out var fallback) ? fallback
        : key;

    public static string Get(string key) => Current[key];

    /// Composed message; the placeholders live in the table so word order can
    /// differ between languages.
    public static string Format(string key, params object?[] values) =>
        string.Format(Current[key], values);

    public void Toggle() => Save(Language = Language == "ko" ? "en" : "ko");

    private static string Load()
    {
        try
        {
            if (File.Exists(PreferencePath))
            {
                var stored = File.ReadAllText(PreferencePath).Trim();
                if (stored is "ko" or "en") return stored;
            }
        }
        catch
        {
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
    }

    private static void Save(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, language);
        }
        catch
        {
        }
    }
}

/// `Text="{loc:T SomeKey}"` — a one-way binding to the string table, so the
/// window follows a language change without being rebuilt.
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = Strings.Current,
            Mode = System.Windows.Data.BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
