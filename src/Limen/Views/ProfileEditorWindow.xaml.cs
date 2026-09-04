using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Limen;

public partial class ProfileEditorWindow : Window
{

    public SshProfile Profile { get; }

    public ProfileEditorWindow(SshProfile profile, bool isNew, IReadOnlyList<SshProfile>? availableProfiles = null)
    {
        InitializeComponent();
        Profile = profile;
        Title = isNew ? Strings.Get("Main.NewSession") : Strings.Format("Editor.TitleFor", profile.Name);
        HeadingText.Text = isNew ? Strings.Get("Main.NewSession") : profile.Name;
        SubheadingText.Text = isNew
            ? Strings.Get("Editor.SubtitleNew")
            : profile.RoutedTarget;

        NameBox.Text = profile.Name;
        FolderBox.Text = profile.Folder;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UserBox.Text = profile.UserName;
        KeyPathBox.Text = profile.PrivateKeyPath;
        RemoteDirBox.Text = profile.RemoteDirectory;
        LocalDirBox.Text = profile.LocalDirectory;
        LoginCommandBox.Text = profile.LoginCommand;
        PasswordAuth.IsChecked = profile.Auth == AuthMode.Password;
        KeyAuth.IsChecked = profile.Auth == AuthMode.PrivateKey;

        var choices = new List<JumpChoice> { new(Strings.Get("Editor.NoJump"), null) };
        if (availableProfiles is not null)
            choices.AddRange(availableProfiles
                .Where(candidate => candidate.Id != profile.Id)
                .OrderBy(candidate => candidate.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
                .Select(candidate => new JumpChoice(candidate.DisplayPath, candidate)));
        JumpProfileBox.ItemsSource = choices;
        JumpProfileBox.SelectedItem = choices.FirstOrDefault(choice => choice.Profile?.Id == profile.JumpProfileId)
            ?? choices.FirstOrDefault(choice => choice.Profile is not null && profile.JumpHost is not null
                && choice.Profile.Host.Equals(profile.JumpHost.Host, StringComparison.OrdinalIgnoreCase)
                && choice.Profile.Port == profile.JumpHost.Port
                && choice.Profile.UserName.Equals(profile.JumpHost.UserName, StringComparison.Ordinal))
            ?? choices[0];
        JumpProfileBox.SelectionChanged += (_, _) => UpdateJumpHint();
        UpdateJumpHint();

        // An empty box means "keep what is stored". Nothing stands in for the
        // secret itself: a placeholder marker inside a PasswordBox is fragile —
        // any template change loses it, and the save silently wipes the
        // credential.
        if (profile.ProtectedPassword.Length > 0)
            Ui.SetPlaceholder(PasswordBoxCtl, Strings.Get("Editor.KeepPassword"));
        if (profile.ProtectedPassphrase.Length > 0)
            Ui.SetPlaceholder(PassphraseBox, Strings.Get("Editor.KeepPassphrase"));

        var hasSecret = profile.ProtectedPassword.Length > 0 || profile.ProtectedPassphrase.Length > 0;
        ForgetButton.IsEnabled = hasSecret;
        if (hasSecret)
            PasswordHint.Text = Strings.Get("Editor.HasPassword");

        var trusted = profile.HostKeyFingerprint.Length > 0;
        FingerprintText.Text = trusted
            ? Strings.Format("Editor.HostKeyTrusted", profile.HostKeyFingerprint)
            : Strings.Get("Editor.HostKeyNew");
        // Only an actual fingerprint earns the monospace treatment; prose in a
        // mono face just looks broken in Korean.
        FingerprintText.FontFamily = (System.Windows.Media.FontFamily)FindResource(trusted ? "MonoFont" : "UiFont");
        FingerprintText.FontSize = trusted ? 10.5 : 11.5;

        BuildColorPicker(profile.Color);
        Loaded += (_, _) => NameBox.Focus();
    }

    private void BuildColorPicker(string selected)
    {
        foreach (var choice in SessionColors.Palette)
        {
            var swatch = new ToggleButton
            {
                Style = (Style)FindResource("ColorSwatch"),
                Tag = choice.Hex,
                ToolTip = choice.Name,
                IsChecked = string.Equals(choice.Hex, selected.Trim(), StringComparison.OrdinalIgnoreCase),
                Background = SessionColors.Brush(choice.Hex)
                    ?? (System.Windows.Media.Brush)FindResource("FieldBack")
            };
            swatch.Checked += Swatch_Checked;
            ColorPicker.Children.Add(swatch);
        }

        if (!ColorPicker.Children.OfType<ToggleButton>().Any(button => button.IsChecked == true))
            ((ToggleButton)ColorPicker.Children[0]).IsChecked = true;
    }

    /// Radio behaviour without a shared group: clearing the others here keeps
    /// the swatches plain ToggleButtons.
    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        foreach (var other in ColorPicker.Children.OfType<ToggleButton>())
            if (!ReferenceEquals(other, sender))
                other.IsChecked = false;
    }

    private string SelectedColor() =>
        ColorPicker.Children.OfType<ToggleButton>()
            .FirstOrDefault(button => button.IsChecked == true)?.Tag as string ?? string.Empty;

    private void AuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordRow is null) return;
        var password = PasswordAuth.IsChecked == true;
        PasswordRow.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        KeyRow.Visibility = password ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Editor.PickKey"),
            Filter = Strings.Get("Editor.KeyFilter"),
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        };
        if (dialog.ShowDialog(this) == true) KeyPathBox.Text = dialog.FileName;
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        PasswordBoxCtl.Clear();
        PassphraseBox.Clear();
        Profile.ProtectedPassword = string.Empty;
        Profile.ProtectedPassphrase = string.Empty;
        ForgetButton.IsEnabled = false;
        Ui.SetPlaceholder(PasswordBoxCtl, Strings.Get("Editor.PasswordPlaceholder"));
        Ui.SetPlaceholder(PassphraseBox, Strings.Get("Editor.PassphrasePlaceholder"));
        PasswordHint.Text = Strings.Get("Editor.Forgotten");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (HostBox.Text.Trim().Length == 0)
        {
            Warn(Strings.Get("Editor.NeedHost"), HostBox);
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Warn(Strings.Get("Editor.NeedPort"), PortBox);
            return;
        }
        if (UserBox.Text.Trim().Length == 0)
        {
            Warn(Strings.Get("Editor.NeedUser"), UserBox);
            return;
        }
        if (KeyAuth.IsChecked == true && !File.Exists(KeyPathBox.Text.Trim()))
        {
            Warn(Strings.Get("Editor.NeedKeyFile"), KeyPathBox);
            return;
        }
        ErrorBar.Visibility = Visibility.Collapsed;

        Profile.Name = NameBox.Text.Trim().Length == 0 ? HostBox.Text.Trim() : NameBox.Text.Trim();
        Profile.Folder = FolderBox.Text.Trim().Trim('/', '\\');
        Profile.Host = HostBox.Text.Trim();
        Profile.Port = port;
        Profile.UserName = UserBox.Text.Trim();
        Profile.Auth = KeyAuth.IsChecked == true ? AuthMode.PrivateKey : AuthMode.Password;
        Profile.PrivateKeyPath = KeyPathBox.Text.Trim();
        Profile.RemoteDirectory = RemoteDirBox.Text.Trim();
        Profile.LocalDirectory = LocalDirBox.Text.Trim();
        Profile.LoginCommand = LoginCommandBox.Text.Trim();
        Profile.Color = SelectedColor();
        Profile.ProtectedPassword = Encode(PasswordBoxCtl.Password, Profile.ProtectedPassword);
        Profile.ProtectedPassphrase = Encode(PassphraseBox.Password, Profile.ProtectedPassphrase);

        var jumpSource = (JumpProfileBox.SelectedItem as JumpChoice)?.Profile;
        Profile.JumpProfileId = jumpSource?.Id ?? string.Empty;
        Profile.JumpHost = jumpSource is null ? null : new JumpHostProfile
        {
            Host = jumpSource.Host,
            Port = jumpSource.Port,
            UserName = jumpSource.UserName,
            Auth = jumpSource.Auth,
            PrivateKeyPath = jumpSource.PrivateKeyPath,
            ProtectedPassword = jumpSource.ProtectedPassword,
            ProtectedPassphrase = jumpSource.ProtectedPassphrase,
            HostKeyFingerprint = jumpSource.HostKeyFingerprint
        };

        DialogResult = true;
    }

    /// Typed text replaces the secret; an empty box keeps whatever is already
    /// stored. Clearing goes through "자격 증명 삭제", which zeroes the profile
    /// first so `existing` is already empty here.
    private static string Encode(string entered, string existing) =>
        entered.Length > 0 ? Secret.Protect(entered) : existing;

    /// Validation stays in the dialog: a strip above the buttons plus focus on
    /// the field at fault, instead of stacking a message box on a modal.
    private void Warn(string message, Control? field = null)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
        field?.Focus();
        if (field is TextBox box) box.SelectAll();
    }

    private void UpdateJumpHint()
    {
        var source = (JumpProfileBox.SelectedItem as JumpChoice)?.Profile;
        JumpHint.Text = source is null
            ? Strings.Get("Editor.NoJumpHint")
            : Strings.Format("Editor.JumpHint", $"{source.UserName}@{source.Host}:{source.Port}");
    }

    private sealed record JumpChoice(string Label, SshProfile? Profile)
    {
        public override string ToString() => Label;
    }
}
