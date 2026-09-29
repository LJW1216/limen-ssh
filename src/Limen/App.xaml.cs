using System.Windows;

namespace Limen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashGuard.Install(this, ex => MessageBox.Show(
            Strings.Format("App.ErrorBody", ex.Message), Strings.Get("App.ErrorTitle"),
            MessageBoxButton.OK, MessageBoxImage.Error));
        ThemeManager.Load();
        TitleBar.Attach();
        base.OnStartup(e);
    }
}
