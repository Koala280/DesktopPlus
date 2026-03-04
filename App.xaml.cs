using System.Configuration;
using System.Data;
using System.Windows;

namespace DesktopPlus
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (DesktopPlus.MainWindow.TryStartPendingUpdateInstall())
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }

}
