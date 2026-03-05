using System;
using System.Configuration;
using System.Data;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace DesktopPlus
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstanceMutex;

        private static bool TryAcquireSingleInstanceMutex()
        {
            try
            {
                string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "DesktopPlus";
                string mutexName = $@"Local\{assemblyName}.SingleInstance";
                _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
                if (createdNew)
                {
                    return true;
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
            catch
            {
            }

            return false;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!TryAcquireSingleInstanceMutex())
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown();
                return;
            }

            if (DesktopPlus.MainWindow.TryStartPendingUpdateInstall())
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}
