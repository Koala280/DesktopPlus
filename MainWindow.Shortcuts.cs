using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private const int WmHotKey = 0x0312;
        private const int HotkeyHidePanelsId = 0x5001;
        private const int HotkeyForegroundPanelsId = 0x5002;
        private const uint HotkeyModAlt = 0x0001;
        private const uint HotkeyModControl = 0x0002;
        private const uint HotkeyModShift = 0x0004;
        private const uint HotkeyModWin = 0x0008;
        private const uint HotkeyModNoRepeat = 0x4000;
        private const string DefaultHidePanelsHotkey = "Ctrl + Alt + H";
        private const string DefaultForegroundPanelsHotkey = "Ctrl + Alt + F";
        private const int ForegroundShortcutPollMs = 35;

        private HwndSource? _mainWindowSource;
        private bool _hidePanelsHotkeyRegistered;
        private bool _foregroundPanelsHotkeyRegistered;
        private CancellationTokenSource? _temporaryForegroundCts;
        private GlobalShortcutSettings _globalShortcuts = new GlobalShortcutSettings();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private void InitializeGlobalShortcuts()
        {
            SourceInitialized += MainWindow_SourceInitialized;
            EnsureGlobalShortcutWindowSource();
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            EnsureGlobalShortcutWindowSource();
            RegisterGlobalShortcuts();
        }

        private void EnsureGlobalShortcutWindowSource()
        {
            if (_mainWindowSource != null)
            {
                return;
            }

            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.Handle;
            if (handle == IntPtr.Zero)
            {
                handle = helper.EnsureHandle();
            }

            if (handle == IntPtr.Zero)
            {
                return;
            }

            _mainWindowSource = HwndSource.FromHwnd(handle);
            _mainWindowSource?.AddHook(MainWindowWindowProc);
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LWin || key == Key.RWin;
        }

        private static bool TryParseModifierToken(string token, out ModifierKeys modifier)
        {
            string normalized = token.Trim().ToUpperInvariant();
            switch (normalized)
            {
                case "CTRL":
                case "CONTROL":
                case "STRG":
                    modifier = ModifierKeys.Control;
                    return true;
                case "ALT":
                case "OPTION":
                    modifier = ModifierKeys.Alt;
                    return true;
                case "SHIFT":
                case "UMSCHALT":
                    modifier = ModifierKeys.Shift;
                    return true;
                case "WIN":
                case "WINDOWS":
                case "META":
                    modifier = ModifierKeys.Windows;
                    return true;
                default:
                    modifier = ModifierKeys.None;
                    return false;
            }
        }

        private static bool TryParseHotkeyKeyToken(string token, out Key key)
        {
            key = Key.None;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string normalized = token.Trim();
            string upper = normalized.ToUpperInvariant();

            if (upper.Length == 1)
            {
                char ch = upper[0];
                if (ch >= 'A' && ch <= 'Z' &&
                    Enum.TryParse<Key>(ch.ToString(), true, out var letterKey))
                {
                    key = letterKey;
                    return true;
                }

                if (ch >= '0' && ch <= '9' &&
                    Enum.TryParse<Key>($"D{ch}", true, out var digitKey))
                {
                    key = digitKey;
                    return true;
                }
            }

            if (upper.StartsWith("NUM", StringComparison.Ordinal) &&
                upper.Length == 4 &&
                char.IsDigit(upper[3]) &&
                Enum.TryParse<Key>($"NumPad{upper[3]}", true, out var numPadKey))
            {
                key = numPadKey;
                return true;
            }

            if (upper.StartsWith("F", StringComparison.Ordinal) &&
                int.TryParse(upper.Substring(1), out int functionKeyNumber) &&
                functionKeyNumber >= 1 &&
                functionKeyNumber <= 24 &&
                Enum.TryParse<Key>($"F{functionKeyNumber}", true, out var functionKey))
            {
                key = functionKey;
                return true;
            }

            switch (upper)
            {
                case "ESC":
                case "ESCAPE":
                    key = Key.Escape;
                    return true;
                case "ENTER":
                case "RETURN":
                    key = Key.Return;
                    return true;
                case "TAB":
                    key = Key.Tab;
                    return true;
                case "SPACE":
                case "SPACEBAR":
                    key = Key.Space;
                    return true;
                case "PLUS":
                case "OEMPLUS":
                    key = Key.OemPlus;
                    return true;
                case "MINUS":
                case "OEMMINUS":
                    key = Key.OemMinus;
                    return true;
            }

            if (Enum.TryParse<Key>(normalized, true, out var parsed) &&
                parsed != Key.None &&
                !IsModifierKey(parsed))
            {
                key = parsed;
                return true;
            }

            return false;
        }

        private static string GetHotkeyKeyLabel(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return key.ToString().ToUpperInvariant();
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                int number = (int)key - (int)Key.D0;
                return number.ToString();
            }

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                int number = (int)key - (int)Key.NumPad0;
                return $"Num{number}";
            }

            return key switch
            {
                Key.Escape => "Esc",
                Key.Return => "Enter",
                Key.Space => "Space",
                Key.OemPlus => "Plus",
                Key.OemMinus => "Minus",
                _ => key.ToString()
            };
        }

        private static string FormatHotkey(ModifierKeys modifiers, Key key)
        {
            var parts = new List<string>(5);
            if ((modifiers & ModifierKeys.Control) != 0)
            {
                parts.Add("Ctrl");
            }
            if ((modifiers & ModifierKeys.Alt) != 0)
            {
                parts.Add("Alt");
            }
            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                parts.Add("Shift");
            }
            if ((modifiers & ModifierKeys.Windows) != 0)
            {
                parts.Add("Win");
            }

            string keyLabel = GetHotkeyKeyLabel(key);
            if (string.IsNullOrWhiteSpace(keyLabel))
            {
                return string.Empty;
            }

            parts.Add(keyLabel);
            return string.Join(" + ", parts);
        }

        private static bool TryParseHotkey(string? text, out ModifierKeys modifiers, out Key key, out string normalized)
        {
            modifiers = ModifierKeys.None;
            key = Key.None;
            normalized = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var tokens = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                if (TryParseModifierToken(token, out var modifier))
                {
                    modifiers |= modifier;
                    continue;
                }

                if (key != Key.None)
                {
                    return false;
                }

                if (!TryParseHotkeyKeyToken(token, out key))
                {
                    return false;
                }
            }

            if (key == Key.None || modifiers == ModifierKeys.None || IsModifierKey(key))
            {
                return false;
            }

            normalized = FormatHotkey(modifiers, key);
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private static uint BuildNativeModifiers(ModifierKeys modifiers)
        {
            uint nativeModifiers = HotkeyModNoRepeat;

            if ((modifiers & ModifierKeys.Control) != 0)
            {
                nativeModifiers |= HotkeyModControl;
            }
            if ((modifiers & ModifierKeys.Alt) != 0)
            {
                nativeModifiers |= HotkeyModAlt;
            }
            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                nativeModifiers |= HotkeyModShift;
            }
            if ((modifiers & ModifierKeys.Windows) != 0)
            {
                nativeModifiers |= HotkeyModWin;
            }

            return nativeModifiers;
        }

        private static bool TryBuildNativeHotkey(string? text, out uint nativeModifiers, out uint virtualKey, out string normalizedText)
        {
            nativeModifiers = 0;
            virtualKey = 0;
            normalizedText = string.Empty;

            if (!TryParseHotkey(text, out var modifiers, out var key, out normalizedText))
            {
                return false;
            }

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0)
            {
                return false;
            }

            nativeModifiers = BuildNativeModifiers(modifiers);
            virtualKey = (uint)vk;
            return true;
        }

        private static bool AreSameHotkey(uint leftModifiers, uint leftVirtualKey, uint rightModifiers, uint rightVirtualKey)
        {
            return leftModifiers == rightModifiers && leftVirtualKey == rightVirtualKey;
        }

        private static string NormalizeHotkeyOrDefault(string? shortcut, string fallback)
        {
            if (TryParseHotkey(shortcut, out _, out _, out string normalized))
            {
                return normalized;
            }

            return fallback;
        }

        private void NormalizeGlobalShortcutSettings()
        {
            _globalShortcuts ??= new GlobalShortcutSettings();

            string normalizedHide = NormalizeHotkeyOrDefault(_globalShortcuts.HidePanelsHotkey, DefaultHidePanelsHotkey);
            string normalizedForeground = NormalizeHotkeyOrDefault(_globalShortcuts.ForegroundPanelsHotkey, DefaultForegroundPanelsHotkey);

            if (string.Equals(normalizedHide, normalizedForeground, StringComparison.OrdinalIgnoreCase))
            {
                normalizedForeground = DefaultForegroundPanelsHotkey;
                if (string.Equals(normalizedHide, normalizedForeground, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedHide = DefaultHidePanelsHotkey;
                }
            }

            _globalShortcuts.HidePanelsHotkey = normalizedHide;
            _globalShortcuts.ForegroundPanelsHotkey = normalizedForeground;
        }

        private void ApplyGlobalShortcutSettingsToUi()
        {
            if (HidePanelsHotkeyInput != null)
            {
                HidePanelsHotkeyInput.Text = _globalShortcuts.HidePanelsHotkey;
            }

            if (ForegroundPanelsHotkeyInput != null)
            {
                ForegroundPanelsHotkeyInput.Text = _globalShortcuts.ForegroundPanelsHotkey;
            }
        }

        private void UnregisterRegisteredGlobalShortcuts(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (_hidePanelsHotkeyRegistered)
            {
                UnregisterHotKey(handle, HotkeyHidePanelsId);
                _hidePanelsHotkeyRegistered = false;
            }

            if (_foregroundPanelsHotkeyRegistered)
            {
                UnregisterHotKey(handle, HotkeyForegroundPanelsId);
                _foregroundPanelsHotkeyRegistered = false;
            }
        }

        private void ShowInvalidGlobalShortcutMessage(string actionLabel)
        {
            System.Windows.MessageBox.Show(
                string.Format(GetString("Loc.MsgShortcutInvalid"), actionLabel),
                GetString("Loc.MsgError"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void ShowGlobalShortcutDuplicateMessage()
        {
            System.Windows.MessageBox.Show(
                GetString("Loc.MsgShortcutDuplicate"),
                GetString("Loc.MsgError"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void ShowGlobalShortcutRegisterFailedMessage(string shortcutText)
        {
            int errorCode = Marshal.GetLastWin32Error();
            string message = string.Format(GetString("Loc.MsgShortcutRegisterFailed"), shortcutText);
            if (errorCode != 0)
            {
                message = $"{message} (Win32: {errorCode})";
            }

            System.Windows.MessageBox.Show(
                message,
                GetString("Loc.MsgError"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private bool TryRegisterConfiguredGlobalShortcuts(IntPtr handle, bool showErrors)
        {
            if (!TryBuildNativeHotkey(
                    _globalShortcuts.HidePanelsHotkey,
                    out uint hideModifiers,
                    out uint hideVirtualKey,
                    out string normalizedHide))
            {
                if (showErrors)
                {
                    ShowInvalidGlobalShortcutMessage(GetString("Loc.ShortcutsInputHide"));
                }
                return false;
            }

            if (!TryBuildNativeHotkey(
                    _globalShortcuts.ForegroundPanelsHotkey,
                    out uint foregroundModifiers,
                    out uint foregroundVirtualKey,
                    out string normalizedForeground))
            {
                if (showErrors)
                {
                    ShowInvalidGlobalShortcutMessage(GetString("Loc.ShortcutsInputForeground"));
                }
                return false;
            }

            if (AreSameHotkey(hideModifiers, hideVirtualKey, foregroundModifiers, foregroundVirtualKey))
            {
                if (showErrors)
                {
                    ShowGlobalShortcutDuplicateMessage();
                }
                return false;
            }

            bool hideRegistered = RegisterHotKey(handle, HotkeyHidePanelsId, hideModifiers, hideVirtualKey);
            if (!hideRegistered)
            {
                if (showErrors)
                {
                    ShowGlobalShortcutRegisterFailedMessage(normalizedHide);
                }
                return false;
            }

            bool foregroundRegistered = RegisterHotKey(handle, HotkeyForegroundPanelsId, foregroundModifiers, foregroundVirtualKey);
            if (!foregroundRegistered)
            {
                UnregisterHotKey(handle, HotkeyHidePanelsId);
                if (showErrors)
                {
                    ShowGlobalShortcutRegisterFailedMessage(normalizedForeground);
                }
                return false;
            }

            _hidePanelsHotkeyRegistered = true;
            _foregroundPanelsHotkeyRegistered = true;
            _globalShortcuts.HidePanelsHotkey = normalizedHide;
            _globalShortcuts.ForegroundPanelsHotkey = normalizedForeground;
            return true;
        }

        private void RegisterGlobalShortcuts()
        {
            IntPtr handle = _mainWindowSource?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            UnregisterRegisteredGlobalShortcuts(handle);
            if (!TryRegisterConfiguredGlobalShortcuts(handle, showErrors: false))
            {
                _globalShortcuts = new GlobalShortcutSettings();
                NormalizeGlobalShortcutSettings();
                UnregisterRegisteredGlobalShortcuts(handle);
                TryRegisterConfiguredGlobalShortcuts(handle, showErrors: false);
            }

            ApplyGlobalShortcutSettingsToUi();
        }

        private bool TryApplyGlobalHotkeysFromInputs(bool showErrors)
        {
            string hideInput = HidePanelsHotkeyInput?.Text ?? string.Empty;
            string foregroundInput = ForegroundPanelsHotkeyInput?.Text ?? string.Empty;

            if (!TryBuildNativeHotkey(hideInput, out uint hideModifiers, out uint hideVirtualKey, out string normalizedHide))
            {
                if (showErrors)
                {
                    ShowInvalidGlobalShortcutMessage(GetString("Loc.ShortcutsInputHide"));
                }
                return false;
            }

            if (!TryBuildNativeHotkey(foregroundInput, out uint foregroundModifiers, out uint foregroundVirtualKey, out string normalizedForeground))
            {
                if (showErrors)
                {
                    ShowInvalidGlobalShortcutMessage(GetString("Loc.ShortcutsInputForeground"));
                }
                return false;
            }

            if (AreSameHotkey(hideModifiers, hideVirtualKey, foregroundModifiers, foregroundVirtualKey))
            {
                if (showErrors)
                {
                    ShowGlobalShortcutDuplicateMessage();
                }
                return false;
            }

            var previous = new GlobalShortcutSettings
            {
                HidePanelsHotkey = _globalShortcuts.HidePanelsHotkey,
                ForegroundPanelsHotkey = _globalShortcuts.ForegroundPanelsHotkey
            };

            _globalShortcuts.HidePanelsHotkey = normalizedHide;
            _globalShortcuts.ForegroundPanelsHotkey = normalizedForeground;

            IntPtr handle = _mainWindowSource?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                UnregisterRegisteredGlobalShortcuts(handle);
                if (!TryRegisterConfiguredGlobalShortcuts(handle, showErrors))
                {
                    _globalShortcuts = previous;
                    UnregisterRegisteredGlobalShortcuts(handle);
                    TryRegisterConfiguredGlobalShortcuts(handle, showErrors: false);
                    ApplyGlobalShortcutSettingsToUi();
                    return false;
                }
            }

            ApplyGlobalShortcutSettingsToUi();
            return true;
        }

        private void GlobalHotkeyInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab)
            {
                return;
            }

            if (key == Key.Back || key == Key.Delete || key == Key.Escape)
            {
                textBox.Text = string.Empty;
                e.Handled = true;
                return;
            }

            if (IsModifierKey(key))
            {
                e.Handled = true;
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            if (modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                return;
            }

            string formatted = FormatHotkey(modifiers, key);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                textBox.Text = formatted;
                textBox.CaretIndex = textBox.Text.Length;
            }

            e.Handled = true;
        }

        private void ShortcutsTabRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            if (FindAncestor<System.Windows.Controls.TextBox>(source) != null ||
                FindAncestor<System.Windows.Controls.Button>(source) != null)
            {
                return;
            }

            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox focusedInput &&
                (ReferenceEquals(focusedInput, HidePanelsHotkeyInput) ||
                 ReferenceEquals(focusedInput, ForegroundPanelsHotkeyInput)))
            {
                Keyboard.ClearFocus();
            }
        }

        private void ApplyGlobalShortcuts_Click(object sender, RoutedEventArgs e)
        {
            if (!TryApplyGlobalHotkeysFromInputs(showErrors: true))
            {
                return;
            }

            SaveSettings();
        }

        private void ResetGlobalShortcuts_Click(object sender, RoutedEventArgs e)
        {
            _globalShortcuts = new GlobalShortcutSettings();
            NormalizeGlobalShortcutSettings();
            ApplyGlobalShortcutSettingsToUi();
            RegisterGlobalShortcuts();
            SaveSettings();
        }

        private void CleanupGlobalShortcuts()
        {
            var pendingForeground = Interlocked.Exchange(ref _temporaryForegroundCts, null);
            pendingForeground?.Cancel();
            pendingForeground?.Dispose();

            IntPtr handle = _mainWindowSource?.Handle ?? IntPtr.Zero;
            UnregisterRegisteredGlobalShortcuts(handle);

            if (_mainWindowSource != null)
            {
                _mainWindowSource.RemoveHook(MainWindowWindowProc);
                _mainWindowSource = null;
            }
        }

        private IntPtr MainWindowWindowProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg != WmHotKey)
            {
                return IntPtr.Zero;
            }

            int id = wParam.ToInt32();
            if (id == HotkeyHidePanelsId)
            {
                ToggleAllPanelsVisibilityByShortcut();
                handled = true;
            }
            else if (id == HotkeyForegroundPanelsId)
            {
                BringPanelsToForegroundWhileShortcutHeld();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void ToggleAllPanelsVisibilityByShortcut()
        {
            bool hasVisiblePanels = Application.Current.Windows
                .OfType<DesktopPanel>()
                .Any(panel => IsUserPanel(panel) && panel.IsVisible);

            if (hasVisiblePanels)
            {
                HideAllUserPanels();
            }
            else
            {
                ShowAllUserPanels();
            }
        }

        private void BringPanelsToForegroundWhileShortcutHeld()
        {
            var openPanels = Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .ToList();

            if (openPanels.Count == 0)
            {
                return;
            }

            var replacement = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _temporaryForegroundCts, replacement);
            previous?.Cancel();
            previous?.Dispose();

            foreach (var panel in openPanels)
            {
                panel.Show();
                panel.SetTemporaryForegroundMode(true);
            }

            string hotkeyText = _globalShortcuts.ForegroundPanelsHotkey;
            _ = RestorePanelsFromForegroundModeAsync(openPanels, hotkeyText, replacement);
        }

        private static bool IsVirtualKeyPressed(int virtualKey)
        {
            if (virtualKey <= 0)
            {
                return false;
            }

            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static bool AreRequiredModifiersPressed(ModifierKeys modifiers)
        {
            if ((modifiers & ModifierKeys.Control) != 0 &&
                !IsVirtualKeyPressed(0xA2) &&
                !IsVirtualKeyPressed(0xA3) &&
                !IsVirtualKeyPressed(0x11))
            {
                return false;
            }

            if ((modifiers & ModifierKeys.Alt) != 0 &&
                !IsVirtualKeyPressed(0xA4) &&
                !IsVirtualKeyPressed(0xA5) &&
                !IsVirtualKeyPressed(0x12))
            {
                return false;
            }

            if ((modifiers & ModifierKeys.Shift) != 0 &&
                !IsVirtualKeyPressed(0xA0) &&
                !IsVirtualKeyPressed(0xA1) &&
                !IsVirtualKeyPressed(0x10))
            {
                return false;
            }

            if ((modifiers & ModifierKeys.Windows) != 0 &&
                !IsVirtualKeyPressed(0x5B) &&
                !IsVirtualKeyPressed(0x5C))
            {
                return false;
            }

            return true;
        }

        private static bool IsConfiguredHotkeyCurrentlyPressed(string hotkeyText)
        {
            if (!TryParseHotkey(hotkeyText, out ModifierKeys modifiers, out Key key, out _))
            {
                return false;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (!IsVirtualKeyPressed(virtualKey))
            {
                return false;
            }

            return AreRequiredModifiersPressed(modifiers);
        }

        private async Task RestorePanelsFromForegroundModeAsync(
            IReadOnlyList<DesktopPanel> panels,
            string hotkeyText,
            CancellationTokenSource source)
        {
            bool shouldRestorePanels = false;
            try
            {
                while (true)
                {
                    source.Token.ThrowIfCancellationRequested();
                    if (!IsConfiguredHotkeyCurrentlyPressed(hotkeyText))
                    {
                        shouldRestorePanels = true;
                        break;
                    }

                    await Task.Delay(ForegroundShortcutPollMs, source.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_temporaryForegroundCts, source))
                {
                    _temporaryForegroundCts = null;
                }
                source.Dispose();
            }

            if (!shouldRestorePanels)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var panel in panels)
                {
                    if (!panel.IsLoaded) continue;
                    panel.SetTemporaryForegroundMode(false);
                }
            }, DispatcherPriority.Background);
        }
    }
}
