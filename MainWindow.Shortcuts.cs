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
        // Keep the panels in front briefly after the keys are released so the shortcut
        // can actually be used to click an item.  A hold-only mode requires the user to
        // keep modifier keys pressed while operating the panel, which commonly blocks
        // the intended click and immediately sends the panel away on release.
        private const int ForegroundShortcutInteractionGraceMs = 2000;
        private const int ShortcutConflictPollSeconds = 3;

        // Low-level keyboard hook constants (override / "above the blocking app" mode).
        private const int WhKeyboardLL = 13;
        private const int HcAction = 0;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;

        private HwndSource? _mainWindowSource;
        private bool _hidePanelsHotkeyRegistered;
        private bool _foregroundPanelsHotkeyRegistered;
        // "Conflict" = the combination is currently owned/blocked by another app.
        private bool _hidePanelsHotkeyConflict;
        private bool _foregroundPanelsHotkeyConflict;
        // Tracks whether we already raised a notification for the current conflict, so a
        // persistent block only notifies once (on the free -> blocked transition).
        private bool _hideConflictNotified;
        private bool _foregroundConflictNotified;
        private CancellationTokenSource? _temporaryForegroundCts;
        private GlobalShortcutSettings _globalShortcuts = new GlobalShortcutSettings();
        private DispatcherTimer? _shortcutConflictTimer;
        private bool _suppressShortcutOptionEvents;

        // Cached parse of the configured hotkeys so the keyboard hook doesn't re-parse
        // strings on every keystroke. Refreshed via UpdateHotkeyCache().
        private int _hideHotkeyVk;
        private ModifierKeys _hideHotkeyModifiers;
        private int _foregroundHotkeyVk;
        private ModifierKeys _foregroundHotkeyModifiers;
        // Auto-repeat suppression for the hook: WM_KEYDOWN repeats while a key is held.
        private bool _hideHotkeyDown;
        private bool _foregroundHotkeyDown;

        private IntPtr _keyboardHook = IntPtr.Zero;
        private LowLevelKeyboardProc? _keyboardHookProc;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private void InitializeGlobalShortcuts()
        {
            SourceInitialized += MainWindow_SourceInitialized;
            EnsureGlobalShortcutWindowSource();

            // Periodically re-evaluate conflicts: reclaim combos freed by another app
            // (below mode) and detect/notify when a hotkey becomes blocked after startup.
            _shortcutConflictTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(ShortcutConflictPollSeconds)
            };
            _shortcutConflictTimer.Tick += ShortcutConflictTimer_Tick;
            _shortcutConflictTimer.Start();
        }

        private void ShortcutConflictTimer_Tick(object? sender, EventArgs e)
        {
            RefreshHotkeyStates();
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
                // EnsureHandle() creates the HWND and raises SourceInitialized
                // synchronously, which re-enters this method and may already set up
                // _mainWindowSource. Re-check afterwards so the message hook is only
                // ever added once (a double hook would fire WM_HOTKEY handlers twice,
                // turning the toggle shortcut into a no-op).
                handle = helper.EnsureHandle();
            }

            if (handle == IntPtr.Zero || _mainWindowSource != null)
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

            // Setting IsChecked below fires Checked/Unchecked; suppress the handlers so
            // syncing the UI from settings doesn't loop back into a save/re-register.
            _suppressShortcutOptionEvents = true;
            if (ShortcutNotifyConflictCheck != null)
            {
                ShortcutNotifyConflictCheck.IsChecked = _globalShortcuts.NotifyOnConflict;
            }
            if (ShortcutOverrideBlockingCheck != null)
            {
                ShortcutOverrideBlockingCheck.IsChecked = _globalShortcuts.OverrideBlockingApp;
            }
            _suppressShortcutOptionEvents = false;

            UpdateGlobalShortcutWarning();
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

        private void UpdateGlobalShortcutWarning()
        {
            if (GlobalShortcutWarning == null)
            {
                return;
            }

            var failed = new List<string>(2);
            if (!_hidePanelsHotkeyRegistered)
            {
                failed.Add(_globalShortcuts.HidePanelsHotkey);
            }
            if (!_foregroundPanelsHotkeyRegistered)
            {
                failed.Add(_globalShortcuts.ForegroundPanelsHotkey);
            }

            if (failed.Count == 0)
            {
                GlobalShortcutWarning.Visibility = Visibility.Collapsed;
                return;
            }

            if (GlobalShortcutWarningText != null)
            {
                GlobalShortcutWarningText.Text = string.Format(
                    GetString("Loc.ShortcutsRegisterWarning"),
                    string.Join(", ", failed));
            }

            GlobalShortcutWarning.Visibility = Visibility.Visible;
        }

        private void UpdateHotkeyCache()
        {
            if (TryParseHotkey(_globalShortcuts.HidePanelsHotkey, out var hideModifiers, out var hideKey, out _))
            {
                _hideHotkeyModifiers = hideModifiers;
                _hideHotkeyVk = KeyInterop.VirtualKeyFromKey(hideKey);
            }
            else
            {
                _hideHotkeyModifiers = ModifierKeys.None;
                _hideHotkeyVk = 0;
            }

            if (TryParseHotkey(_globalShortcuts.ForegroundPanelsHotkey, out var foregroundModifiers, out var foregroundKey, out _))
            {
                _foregroundHotkeyModifiers = foregroundModifiers;
                _foregroundHotkeyVk = KeyInterop.VirtualKeyFromKey(foregroundKey);
            }
            else
            {
                _foregroundHotkeyModifiers = ModifierKeys.None;
                _foregroundHotkeyVk = 0;
            }
        }

        // Refreshes the active/conflict state for a single hotkey.
        //   below mode: RegisterHotKey owns the combo; a failure means another app holds it.
        //   above mode: the keyboard hook fires the action, so RegisterHotKey is only used
        //               as a probe to detect whether another app also wants the combo.
        private void RefreshSingleHotkey(
            IntPtr handle,
            int id,
            ModifierKeys modifiers,
            int virtualKey,
            bool overrideMode,
            ref bool active,
            ref bool conflict)
        {
            if (virtualKey == 0)
            {
                active = false;
                conflict = false;
                return;
            }

            uint nativeModifiers = BuildNativeModifiers(modifiers);
            uint vk = (uint)virtualKey;

            if (overrideMode)
            {
                // Probe only: register succeeds only when the combo is free. Release it
                // again immediately so the hook stays the single source of firing (a live
                // RegisterHotKey would double-fire via WM_HOTKEY when we own the combo).
                bool free = RegisterHotKey(handle, id, nativeModifiers, vk);
                if (free)
                {
                    UnregisterHotKey(handle, id);
                }
                conflict = !free;
                active = true; // functional via the low-level keyboard hook
            }
            else if (active)
            {
                // Already registered and owned by us; it cannot be stolen via RegisterHotKey.
                conflict = false;
            }
            else
            {
                bool registered = RegisterHotKey(handle, id, nativeModifiers, vk);
                active = registered;
                conflict = !registered;
            }
        }

        private void RefreshHotkeyStates()
        {
            IntPtr handle = _mainWindowSource?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            bool overrideMode = _globalShortcuts.OverrideBlockingApp;

            RefreshSingleHotkey(
                handle,
                HotkeyHidePanelsId,
                _hideHotkeyModifiers,
                _hideHotkeyVk,
                overrideMode,
                ref _hidePanelsHotkeyRegistered,
                ref _hidePanelsHotkeyConflict);

            RefreshSingleHotkey(
                handle,
                HotkeyForegroundPanelsId,
                _foregroundHotkeyModifiers,
                _foregroundHotkeyVk,
                overrideMode,
                ref _foregroundPanelsHotkeyRegistered,
                ref _foregroundPanelsHotkeyConflict);

            NotifyConflictTransitions();
            UpdateGlobalShortcutWarning();
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

            _globalShortcuts.HidePanelsHotkey = normalizedHide;
            _globalShortcuts.ForegroundPanelsHotkey = normalizedForeground;
            UpdateHotkeyCache();

            bool overrideMode = _globalShortcuts.OverrideBlockingApp;

            // Fresh registration: assume nothing is held yet, then (re)acquire/probe each.
            _hidePanelsHotkeyRegistered = false;
            _foregroundPanelsHotkeyRegistered = false;
            RefreshSingleHotkey(
                handle,
                HotkeyHidePanelsId,
                _hideHotkeyModifiers,
                _hideHotkeyVk,
                overrideMode,
                ref _hidePanelsHotkeyRegistered,
                ref _hidePanelsHotkeyConflict);
            RefreshSingleHotkey(
                handle,
                HotkeyForegroundPanelsId,
                _foregroundHotkeyModifiers,
                _foregroundHotkeyVk,
                overrideMode,
                ref _foregroundPanelsHotkeyRegistered,
                ref _foregroundPanelsHotkeyConflict);

            // In override mode both hotkeys work regardless of who else wants the combo.
            return overrideMode || (_hidePanelsHotkeyRegistered && _foregroundPanelsHotkeyRegistered);
        }

        // Central (re)apply used on startup, on Apply, on Reset and when the priority
        // mode is toggled: refresh registrations, (un)install the hook, notify + sync UI.
        private void ReapplyGlobalShortcutRegistration()
        {
            IntPtr handle = _mainWindowSource?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                UnregisterRegisteredGlobalShortcuts(handle);
                TryRegisterConfiguredGlobalShortcuts(handle, showErrors: false);
            }
            else
            {
                // No HWND yet (very early startup); still keep the parsed cache current.
                UpdateHotkeyCache();
            }

            InstallOrRemoveKeyboardHook();
            NotifyConflictTransitions();
            ApplyGlobalShortcutSettingsToUi();
        }

        private void RegisterGlobalShortcuts()
        {
            ReapplyGlobalShortcutRegistration();
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

            _globalShortcuts.HidePanelsHotkey = normalizedHide;
            _globalShortcuts.ForegroundPanelsHotkey = normalizedForeground;

            // Keep the user's chosen combination even if one of them can't be registered
            // right now (e.g. taken by another app); the inline warning explains why and
            // the other hotkey keeps working. Invalid/duplicate input was already rejected
            // above, so this is always treated as a successful apply.
            ReapplyGlobalShortcutRegistration();
            return true;
        }

        private void InstallOrRemoveKeyboardHook()
        {
            if (_globalShortcuts.OverrideBlockingApp)
            {
                InstallKeyboardHook();
            }
            else
            {
                RemoveKeyboardHook();
            }
        }

        private void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                return;
            }

            _keyboardHookProc ??= LowLevelKeyboardHookProc;

            IntPtr moduleHandle = IntPtr.Zero;
            using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var mainModule = currentProcess.MainModule)
            {
                if (mainModule != null)
                {
                    moduleHandle = GetModuleHandle(mainModule.ModuleName);
                }
            }

            _keyboardHook = SetWindowsHookEx(WhKeyboardLL, _keyboardHookProc, moduleHandle, 0);
        }

        private void RemoveKeyboardHook()
        {
            if (_keyboardHook == IntPtr.Zero)
            {
                return;
            }

            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _hideHotkeyDown = false;
            _foregroundHotkeyDown = false;
        }

        private IntPtr LowLevelKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HcAction)
            {
                int message = wParam.ToInt32();
                // KBDLLHOOKSTRUCT.vkCode is the first field of the struct at lParam.
                int vk = Marshal.ReadInt32(lParam);

                if (message == WmKeyDown || message == WmSysKeyDown)
                {
                    if (_hideHotkeyVk != 0 && vk == _hideHotkeyVk &&
                        AreRequiredModifiersPressed(_hideHotkeyModifiers))
                    {
                        if (!_hideHotkeyDown)
                        {
                            _hideHotkeyDown = true;
                            _ = Dispatcher.BeginInvoke(new Action(ToggleAllPanelsVisibilityByShortcut));
                        }
                        return (IntPtr)1; // swallow: put DesktopPlus above the blocking app
                    }

                    if (_foregroundHotkeyVk != 0 && vk == _foregroundHotkeyVk &&
                        AreRequiredModifiersPressed(_foregroundHotkeyModifiers))
                    {
                        if (!_foregroundHotkeyDown)
                        {
                            _foregroundHotkeyDown = true;
                            _ = Dispatcher.BeginInvoke(new Action(BringPanelsToForegroundTemporarily));
                        }
                        return (IntPtr)1;
                    }
                }
                else if (message == WmKeyUp || message == WmSysKeyUp)
                {
                    if (vk == _hideHotkeyVk)
                    {
                        _hideHotkeyDown = false;
                    }
                    if (vk == _foregroundHotkeyVk)
                    {
                        _foregroundHotkeyDown = false;
                    }
                }
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // Raises a tray notification when a hotkey newly becomes blocked by another app.
        // Only fires on the free -> blocked transition so a persistent conflict is not spammed.
        private void NotifyConflictTransitions()
        {
            bool canNotify = _globalShortcuts.NotifyOnConflict && _notifyIcon != null;

            if (_hidePanelsHotkeyConflict)
            {
                if (canNotify && !_hideConflictNotified)
                {
                    ShowShortcutConflictNotification(_globalShortcuts.HidePanelsHotkey);
                    _hideConflictNotified = true;
                }
            }
            else
            {
                _hideConflictNotified = false;
            }

            if (_foregroundPanelsHotkeyConflict)
            {
                if (canNotify && !_foregroundConflictNotified)
                {
                    ShowShortcutConflictNotification(_globalShortcuts.ForegroundPanelsHotkey);
                    _foregroundConflictNotified = true;
                }
            }
            else
            {
                _foregroundConflictNotified = false;
            }
        }

        private void ShowShortcutConflictNotification(string hotkeyText)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => ShowShortcutConflictNotification(hotkeyText)));
                return;
            }

            if (_notifyIcon == null)
            {
                return;
            }

            string title = GetString("Loc.ShortcutConflictTitle");
            string bodyKey = _globalShortcuts.OverrideBlockingApp
                ? "Loc.ShortcutConflictBodyAbove"
                : "Loc.ShortcutConflictBodyBelow";
            string message = string.Format(GetString(bodyKey), hotkeyText);

            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning;
                _notifyIcon.ShowBalloonTip(6000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to show shortcut conflict notification: {ex}");
            }
        }

        private void ShortcutNotifyConflict_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressShortcutOptionEvents)
            {
                return;
            }

            _globalShortcuts.NotifyOnConflict = ShortcutNotifyConflictCheck?.IsChecked == true;
            NotifyConflictTransitions();
            SaveSettingsImmediate();
        }

        private void ShortcutPriority_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressShortcutOptionEvents)
            {
                return;
            }

            bool overrideApp = ShortcutOverrideBlockingCheck?.IsChecked == true;
            if (overrideApp == _globalShortcuts.OverrideBlockingApp)
            {
                return;
            }

            _globalShortcuts.OverrideBlockingApp = overrideApp;
            // Switching modes changes the notification wording and register/hook strategy,
            // so re-evaluate the current conflict state from scratch.
            _hideConflictNotified = false;
            _foregroundConflictNotified = false;
            ReapplyGlobalShortcutRegistration();
            SaveSettingsImmediate();
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

            SaveSettingsImmediate();
        }

        private void ResetGlobalShortcuts_Click(object sender, RoutedEventArgs e)
        {
            _globalShortcuts = new GlobalShortcutSettings();
            NormalizeGlobalShortcutSettings();
            ApplyGlobalShortcutSettingsToUi();
            RegisterGlobalShortcuts();
            SaveSettingsImmediate();
        }

        private void CleanupGlobalShortcuts()
        {
            if (_shortcutConflictTimer != null)
            {
                _shortcutConflictTimer.Stop();
                _shortcutConflictTimer.Tick -= ShortcutConflictTimer_Tick;
                _shortcutConflictTimer = null;
            }

            RemoveKeyboardHook();

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
                BringPanelsToForegroundTemporarily();
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

        private void BringPanelsToForegroundTemporarily()
        {
            // This is a foreground action, not a visibility action.  In particular, do not
            // briefly reveal panels the user deliberately hid: they would disappear again on
            // release and look as if the shortcut had closed them.
            var candidatePanels = Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(panel => IsUserPanel(panel) && panel.IsVisible)
                .ToList();

            if (candidatePanels.Count == 0)
            {
                return;
            }

            var replacement = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _temporaryForegroundCts, replacement);
            previous?.Cancel();
            previous?.Dispose();

            foreach (var panel in candidatePanels)
            {
                panel.SetTemporaryForegroundMode(true);
            }

            string hotkeyText = _globalShortcuts.ForegroundPanelsHotkey;
            _ = RestorePanelsFromForegroundModeAsync(candidatePanels, hotkeyText, replacement);
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

                // The WM_HOTKEY message is delivered while the shortcut keys are still
                // down.  Keep the foreground state a little longer after release so users
                // can let go of Alt/Ctrl and click or open an item normally.
                await Task.Delay(ForegroundShortcutInteractionGraceMs, source.Token);
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
