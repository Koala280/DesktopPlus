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
            bool restoreBeforeUninstall = Array.Exists(e.Args, argument =>
                string.Equals(argument, "--restore-before-uninstall", StringComparison.OrdinalIgnoreCase));
            if (!TryAcquireSingleInstanceMutex())
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(restoreBeforeUninstall ? 3 : 0);
                return;
            }

            if (restoreBeforeUninstall)
            {
                // Run only the recovery dialog: no panels, auto-sort, settings saves or updates.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string language = Array.Exists(e.Args, argument =>
                    string.Equals(argument, "--language=german", StringComparison.OrdinalIgnoreCase))
                    ? "de"
                    : "en";
                Shutdown(DesktopPlus.MainWindow.RunUninstallBackupRestore(language));
                return;
            }

            if (DesktopPlus.MainWindow.TryStartPendingUpdateInstall())
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown();
                return;
            }

            FolderSearchIndexService.Shared.IndexChanged += DesktopPanel.OnSearchIndexChanged;
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            FolderSearchIndexService.Shared.IndexChanged -= DesktopPanel.OnSearchIndexChanged;
            FolderSearchIndexService.Shared.Dispose();
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
