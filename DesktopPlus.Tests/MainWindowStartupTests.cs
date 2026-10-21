using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class MainWindowStartupTests
{
    [Fact]
    public void WindowMarkupInitializesAndBackupDetailsCanBeRefreshed()
    {
        RunSta(() =>
        {
            var window = new MainWindow(initializeRuntime: false);
            try
            {
                Assert.IsType<Button>(window.FindName("BackupDeleteSelectedButton"));
                var list = Assert.IsType<ListBox>(window.FindName("BackupsList"));
                list.ItemsSource = new[] { new UpdateBackupInfo { ArchiveFileName = "test.zip" } };
                list.SelectedIndex = 0;
                list.SelectedIndex = -1;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SlowBackupHistoryDoesNotBlockTheWindowDispatcher()
    {
        RunSta(() =>
        {
            using var data = new TempDir();
            using var releaseIo = new ManualResetEventSlim();
            var window = new MainWindow(initializeRuntime: false);
            try
            {
                int uiThread = Environment.CurrentManagedThreadId;
                bool uiResponded = false;
                window.Show();
                Task refresh = window.RefreshBackupsTabAsync(loadBackups: () =>
                {
                    Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                    Assert.True(releaseIo.Wait(TimeSpan.FromSeconds(10)), "UI did not respond during backup I/O.");
                    return MainWindow.LoadAvailableUpdateBackups(data.Path);
                });
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    uiResponded = true;
                    releaseIo.Set();
                }), DispatcherPriority.Background);
                PumpUntil(refresh);
                Assert.True(uiResponded);
                Assert.True(((Button)window.FindName("CreateManualBackupButton")).IsEnabled);
            }
            finally { releaseIo.Set(); window.Close(); }
        });
    }

    [Fact]
    public void OlderBackupRefreshCannotReplaceNewerResults()
    {
        RunSta(() =>
        {
            using var releaseOld = new ManualResetEventSlim();
            var window = new MainWindow(initializeRuntime: false);
            try
            {
                var oldBackup = new UpdateBackupInfo { ArchivePath = "old.zip" };
                var newBackup = new UpdateBackupInfo { ArchivePath = "new.zip" };
                Task oldRefresh = window.RefreshBackupsTabAsync(loadBackups: () =>
                {
                    Assert.True(releaseOld.Wait(TimeSpan.FromSeconds(10)));
                    return new[] { oldBackup };
                });
                Task newRefresh = window.RefreshBackupsTabAsync(loadBackups: () => new[] { newBackup });
                PumpUntil(newRefresh);
                releaseOld.Set();
                PumpUntil(oldRefresh);
                Assert.Same(newBackup, ((ListBox)window.FindName("BackupsList")).SelectedItem);
            }
            finally { releaseOld.Set(); window.Close(); }
        });
    }

    [Fact]
    public void UnavailableBackupFolderDoesNotFailWindowStartup()
    {
        RunSta(() =>
        {
            var window = new MainWindow(initializeRuntime: false);
            try
            {
                Task refresh = window.RefreshBackupsTabAsync(loadBackups: () => throw new IOException("Backup drive unavailable"));
                PumpUntil(refresh);
                Assert.Contains("Backup drive unavailable", ((TextBlock)window.FindName("BackupOperationStatusText")).Text);
                Assert.True(((Button)window.FindName("CreateManualBackupButton")).IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    private static void PumpUntil(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Startup test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
