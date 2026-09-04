using System.Windows;

namespace Limen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Load();
        TitleBar.Attach();
        base.OnStartup(e);
    }
}
