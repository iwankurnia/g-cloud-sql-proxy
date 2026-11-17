using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CloudSqlProxyManager
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            IClassicDesktopStyleApplicationLifetime desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop != null)
            {
                desktop.MainWindow = new MainWindow();
                // Closing the window ends the app; the window itself asks first while a tunnel is up.
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
