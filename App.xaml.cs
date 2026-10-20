using System.Windows;

namespace ShakerRouter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Set after App.xaml resources load (InitializeComponent), before MainWindow is created,
        // so the Fluent dictionaries merge into our resources instead of being replaced by them.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        base.OnStartup(e);
    }
}
