namespace DesktopPlus
{
    public class GlobalShortcutSettings
    {
        public string HidePanelsHotkey { get; set; } = "Ctrl + Alt + H";
        public string ForegroundPanelsHotkey { get; set; } = "Ctrl + Alt + F";

        // Send a tray notification when a configured hotkey is blocked/used by another
        // app (or, in override mode, when DesktopPlus takes it over). On by default.
        public bool NotifyOnConflict { get; set; } = true;

        // false = yield to the blocking program (cooperative RegisterHotKey, default).
        // true  = take priority over the blocking program via a low-level keyboard hook.
        public bool OverrideBlockingApp { get; set; } = false;
    }
}
