using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopPlus
{
    public partial class TrayMenuWindow : Window
    {
        private readonly Action _openMainAction;
        private readonly Action _exitAction;
        private bool _isClosing;
        private bool _closeRequested;

        public TrayMenuWindow(Action openMainAction, Action exitAction)
        {
            InitializeComponent();
            _openMainAction = openMainAction;
            _exitAction = exitAction;
            RefreshTexts();

            Closing += (_, _) => _isClosing = true;
            Closed += (_, _) => _isClosing = true;
            Deactivated += (_, _) => RequestClose();
            PreviewKeyDown += TrayMenuWindow_PreviewKeyDown;
        }

        public void RequestClose()
        {
            if (_closeRequested || _isClosing) return;

            _closeRequested = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing || !IsLoaded) return;

                try
                {
                    Close();
                }
                catch (InvalidOperationException)
                {
                    // The window is already closing; no further action required.
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void RefreshTexts()
        {
            OpenMainButton.Content = MainWindow.GetString("Loc.TrayOpen");
            ExitButton.Content = MainWindow.GetString("Loc.TrayExit");
        }

        private void TrayMenuWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RequestClose();
                e.Handled = true;
            }
        }

        private void OpenMainButton_Click(object sender, RoutedEventArgs e)
        {
            _openMainAction?.Invoke();
            RequestClose();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            _exitAction?.Invoke();
            RequestClose();
        }
    }
}
