using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Limen;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand NewSessionCommand = new();
    public static readonly RoutedCommand ReloadCommand = new();
    public static readonly RoutedCommand FindCommand = new();
    public static readonly RoutedCommand CloseTabCommand = new();
    public static readonly RoutedCommand NextTabCommand = new();
    public static readonly RoutedCommand PrevTabCommand = new();

    /// Severity of a status-bar message, shown as the colour of the leading dot.
    private enum Note { Idle, Busy, Good, Bad }

    private readonly ProfileStore _store;
    private readonly SshConnector _connector;
    private readonly ObservableCollection<SessionTab> _tabs = [];
    private SshProfile? _selected;
    private WindowState _lastUsableWindowState = WindowState.Normal;
    private readonly bool _persistPlacement;
    private DispatcherTimer? _placementSave;

    public MainWindow() : this(autoLoad: true, storePath: null)
    {
    }

    public MainWindow(bool autoLoad, string? storePath = null)
    {
        _store = new ProfileStore(storePath);
        // Only the real app owns the saved placement; test and screenshot hosts
        // must not read it or write their own geometry over it.
        _persistPlacement = autoLoad;
        InitializeComponent();
        RestoreWindowPlacement();
        ApplyThemeButton();
        ApplyLanguageButton();
        _connector = new SshConnector(_store, this);
        Tabs.ItemsSource = _tabs;
        _tabs.CollectionChanged += (_, _) =>
        {
            EmptyState.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TabStrip.Visibility = _tabs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        };

        if (autoLoad) Loaded += (_, _) => LoadStore();
        // A narrow window has no room for both the status message and a full
        // file path; the path is the one with a tooltip, so it yields first.
        SizeChanged += (_, _) => StoreText.Visibility =
            ActualWidth < 820 ? Visibility.Collapsed : Visibility.Visible;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastUsableWindowState = WindowState;
            QueuePlacementSave();
        };
        Closing += OnClosing;

        if (_persistPlacement) TrackPlacementChanges();
    }

    /// First run opens roomier than the XAML defaults; after that the window
    /// comes back exactly where the user left it.
    private const double FirstRunScale = 1.4;
    private const double MaxWorkAreaShare = 0.94;

    private void RestoreWindowPlacement()
    {
        var saved = _persistPlacement ? WindowPlacementStore.Load() : null;
        if (saved is not null && IsUsablePlacement(saved))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
            _lastUsableWindowState = WindowState;
            return;
        }

        // Scales the sizes declared in XAML, capped so the window still fits
        // the work area on a small display.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width * FirstRunScale, workArea.Width * MaxWorkAreaShare);
        Height = Math.Min(Height * FirstRunScale, workArea.Height * MaxWorkAreaShare);
    }

    /// Rejects geometry that would strand the window off every monitor — the
    /// display setup can change between runs.
    private bool IsUsablePlacement(WindowPlacement placement)
    {
        if (!double.IsFinite(placement.Left) || !double.IsFinite(placement.Top) ||
            !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
            placement.Width < MinWidth || placement.Height < MinHeight)
            return false;

        var right = placement.Left + placement.Width;
        var bottom = placement.Top + placement.Height;
        var visibleWidth = Math.Min(right, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) -
                           Math.Max(placement.Left, SystemParameters.VirtualScreenLeft);
        var visibleHeight = Math.Min(bottom, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) -
                            Math.Max(placement.Top, SystemParameters.VirtualScreenTop);
        return visibleWidth >= 160 && visibleHeight >= 100;
    }

    /// Closing alone is not a durable trigger: a crash, a kill, or a Windows
    /// shutdown never reaches it. Resizing and moving write through instead,
    /// debounced so a drag does not hit the disk on every frame.
    private void TrackPlacementChanges()
    {
        _placementSave = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background,
            (_, _) =>
            {
                _placementSave!.Stop();
                SaveWindowPlacement();
            },
            Dispatcher);
        _placementSave.Stop();

        SizeChanged += (_, _) => QueuePlacementSave();
        LocationChanged += (_, _) => QueuePlacementSave();
    }

    private void QueuePlacementSave()
    {
        // Layout settles before the window is loaded; saving then would just
        // persist intermediate geometry.
        if (_placementSave is null || !IsLoaded) return;
        _placementSave.Stop();
        _placementSave.Start();
    }

    private void SaveWindowPlacement()
    {
        if (!_persistPlacement) return;

        // RestoreBounds is what Windows itself would restore the window to, so
        // it stays correct while maximized or minimized. It is empty before the
        // window is sourced, hence the fallback.
        var bounds = RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            bounds = new Rect(Left, Top, Width, Height);
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width < MinWidth || bounds.Height < MinHeight)
            return;

        WindowPlacementStore.Save(new WindowPlacement(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            _lastUsableWindowState == WindowState.Maximized));
    }

    // 세션 저장소 -----------------------------------------------------------

    private void LoadStore()
    {
        try
        {
            _store.Load();
            StartupLog.Record(_store.FilePath, _store.Profiles.Count, null);
        }
        catch (Exception ex)
        {
            StartupLog.Record(_store.FilePath, 0, ex);
            Status(Strings.Format("Main.StoreReadFailedShort", ex.Message), Note.Bad);
            MessageBox.Show(this, Strings.Format("Main.StoreReadFailed", ex.Message), "Limen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        StoreText.Text = _store.FilePath;
        StoreButton.ToolTip = Strings.Format("Main.StoreTip", _store.FilePath);
        RebuildTree();
        Status(_store.Profiles.Count == 0
            ? Strings.Get("Main.StoreEmpty")
            : Strings.Format("Main.Loaded", _store.Profiles.Count));
    }

    private void RebuildTree()
    {
        var previous = _selected?.Id;
        var query = SearchBox.Text.Trim();

        // Built detached, then handed to the tree in one go: folder rows read
        // their child count when the container is realised, so the whole shape
        // has to exist first.
        var roots = new ObservableCollection<ProfileNode>();
        var visible = _store.Profiles
            .Where(profile => profile.Matches(query))
            .OrderBy(profile => profile.Folder, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var profile in visible)
        {
            var level = roots;
            foreach (var segment in profile.Folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var folder = level.FirstOrDefault(node => node.IsFolder && node.Name.Equals(segment, StringComparison.CurrentCultureIgnoreCase));
                if (folder is null)
                {
                    folder = new ProfileNode { Name = segment };
                    level.Add(folder);
                }
                level = folder.Children;
            }
            level.Add(new ProfileNode { Name = profile.Name, Profile = profile });
        }

        SessionTree.ItemsSource = roots;

        var total = _store.Profiles.Count;
        CountText.Text = query.Length == 0 ? Strings.Format("Main.CountAll", total) : $"{visible.Count} / {total}";
        ClearSearch.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        TreeEmpty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TreeEmptyText.Text = total == 0
            ? Strings.Get("Main.TreeEmpty")
            : Strings.Format("Main.NoMatch", query);

        if (previous is not null && _store.Profiles.All(p => p.Id != previous)) SetSelection(null);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SessionTree is not null) RebuildTree();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearch_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Down || e.Key == Key.Enter)
        {
            SessionTree.Focus();
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.SelectAll();
        SearchBox.Focus();
    }

    private void Reload_Executed(object sender, ExecutedRoutedEventArgs e) => LoadStore();

    private void StorePath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _store.FilePath;
            var argument = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status(Strings.Format("Main.FolderOpenFailed", ex.Message), Note.Bad);
        }
    }

    // 세션 편집 -------------------------------------------------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var profile = new SshProfile
        {
            Name = Strings.Get("Main.NewSession"),
            Port = 22,
            UserName = Environment.UserName
        };
        if (new ProfileEditorWindow(profile, isNew: true, _store.Profiles) { Owner = this }.ShowDialog() != true) return;
        _store.AddOrUpdate(profile);
        RebuildTree();
        Status(Strings.Format("Main.Added", profile.DisplayPath), Note.Good);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var draft = _selected.Clone();
        if (new ProfileEditorWindow(draft, isNew: false, _store.Profiles) { Owner = this }.ShowDialog() != true) return;
        _store.AddOrUpdate(draft);
        _selected = draft;
        RebuildTree();
        SetSelection(draft);
        Status(Strings.Format("Main.Saved", draft.DisplayPath), Note.Good);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Strings.Format("Main.CopySuffix", copy.Name);
        _store.AddOrUpdate(copy);
        RebuildTree();
        Status(Strings.Format("Main.Duplicated", copy.DisplayPath), Note.Good);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var confirm = MessageBox.Show(this,
            Strings.Format("Main.DeleteBody", _selected.DisplayPath),
            Strings.Get("Main.DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var removed = _selected;
        _store.Remove(removed);
        SetSelection(null);
        RebuildTree();
        Status(Strings.Format("Main.Deleted", removed.DisplayPath));
    }

    // 선택 -----------------------------------------------------------------

    private void Tree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        SetSelection((e.NewValue as ProfileNode)?.Profile);

    private void SetSelection(SshProfile? profile)
    {
        _selected = profile;
        var has = profile is not null;
        EditButton.IsEnabled = DeleteButton.IsEnabled = DuplicateButton.IsEnabled =
            SshButton.IsEnabled = SftpButton.IsEnabled = has;

        SelName.Text = has ? profile!.DisplayPath : Strings.Get("Main.NoSelection");
        // SetResourceReference, not FindResource: the latter resolves the brush
        // once, so a later theme flip leaves the text painted for the old theme
        // — near-white on a light toolbar.
        SelName.SetResourceReference(ForegroundProperty, has ? "Ink" : "Faint");
        SelTarget.Text = has ? profile!.RoutedTarget : string.Empty;
        SelIcon.Opacity = has ? 1 : 0.5;

        if (has) Status(Strings.Format("Main.SelectionStatus", profile!.DisplayPath, profile.RoutedTarget));
    }

    /// WPF does not select a TreeViewItem on right-click, so the context menu
    /// used to act on whatever was selected before — doing nothing when that was
    /// a folder, and silently editing the wrong session when it was not.
    private void Tree_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null) return;
        item.IsSelected = true;
        item.Focus();
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Every entry acts on a session; a folder row has none.
        if (_selected is null) e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T) node = VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A folder row toggles instead of trying to connect to nothing.
        if (SessionTree.SelectedItem is ProfileNode { IsFolder: true }) return;
        if (_selected is not null) OpenTab("SSH");
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (_selected is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                OpenTab("SSH");
                e.Handled = true;
                break;
            case Key.F2:
                Edit_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Delete:
                Delete_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    // 탭 -------------------------------------------------------------------

    private void OpenSsh_Click(object sender, RoutedEventArgs e) => OpenTab("SSH");

    private void OpenSftp_Click(object sender, RoutedEventArgs e) => OpenTab("SFTP");

    private void OpenTab(string kind)
    {
        if (_selected is null) return;
        if (_selected.UserName.Trim().Length == 0)
        {
            MessageBox.Show(this, Strings.Get("Main.NeedUserBody"),
                Strings.Get("Main.NeedUserTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var profile = _selected;
        UserControl view = kind == "SSH"
            ? new SshWorkspaceView(profile, _connector)
            : new SftpView(profile, _connector);

        var tab = new SessionTab { Kind = kind, Title = profile.Name, Profile = profile, Content = view };
        switch (view)
        {
            case SshWorkspaceView workspace:
                workspace.StatusChanged += Status;
                workspace.ConnectedChanged += connected => tab.Connected = connected;
                break;
            case SftpView sftp:
                sftp.StatusChanged += Status;
                sftp.ConnectedChanged += connected => tab.Connected = connected;
                break;
        }

        TabHost.Children.Add(view);
        _tabs.Add(tab);
        Tabs.SelectedItem = tab;
        Status(Strings.Format("Main.TabOpened", kind, profile.DisplayPath), Note.Busy);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SessionTab tab) CloseTab(tab);
    }

    private void CloseActiveTab_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (Tabs.SelectedItem is SessionTab tab) CloseTab(tab);
    }

    private void CloseTab(SessionTab tab)
    {
        TabHost.Children.Remove(tab.Content);
        (tab.Content as IDisposable)?.Dispose();
        _tabs.Remove(tab);
    }

    private void NextTab_Executed(object sender, ExecutedRoutedEventArgs e) => StepTab(1);

    private void PrevTab_Executed(object sender, ExecutedRoutedEventArgs e) => StepTab(-1);

    private void StepTab(int step)
    {
        if (_tabs.Count < 2) return;
        var index = Tabs.SelectedIndex < 0 ? 0 : Tabs.SelectedIndex;
        Tabs.SelectedIndex = (index + step + _tabs.Count) % _tabs.Count;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var tab in _tabs)
            tab.Content.Visibility = ReferenceEquals(tab, Tabs.SelectedItem) ? Visibility.Visible : Visibility.Collapsed;
        if (Tabs.SelectedItem is SessionTab { Content: SshWorkspaceView workspace }) workspace.FocusTerminal();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var active = _tabs.Count(tab => tab.Connected);
        if (active > 0)
        {
            var confirm = MessageBox.Show(this, Strings.Format("Main.QuitBody", active),
                Strings.Get("Main.QuitTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _placementSave?.Stop();
        SaveWindowPlacement();
        foreach (var tab in _tabs) (tab.Content as IDisposable)?.Dispose();
        _tabs.Clear();
    }

    // 상태 표시줄 -----------------------------------------------------------

    private void Status(string message) => Status(message, Note.Idle);

    private void Status(string message, Note note)
    {
        StatusText.Text = message;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, note switch
        {
            Note.Good => "Success",
            Note.Busy => "Accent",
            Note.Bad => "Danger",
            _ => "Idle"
        });
    }

    // 테마 -----------------------------------------------------------------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        ApplyThemeButton();
        Status(ThemeManager.IsDark ? Strings.Get("Main.ThemeDarkApplied") : Strings.Get("Main.ThemeLightApplied"));
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        Strings.Current.Toggle();
        ApplyLanguageButton();
        ApplyThemeButton();
        RebuildTree();
    }

    private void ApplyLanguageButton()
    {
        // Shows the language you would switch to, not the current one.
        var korean = Strings.Current.Language == "ko";
        LanguageText.Text = korean ? "EN" : "한국어";
        LanguageButton.ToolTip = korean ? "Switch to English" : "한국어로 전환";
    }

    private void ApplyThemeButton()
    {
        ThemeIcon.Data = (Geometry)FindResource(ThemeManager.IsDark ? "IcoSun" : "IcoMoon");
        ThemeButton.ToolTip = ThemeManager.IsDark ? Strings.Get("Main.ThemeToLight") : Strings.Get("Main.ThemeToDark");
    }
}
