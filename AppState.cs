using System.Collections.Generic;

namespace DesktopPlus
{
    public class AppState
    {
        public List<WindowData> Windows { get; set; } = new List<WindowData>();
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public List<AppearancePreset> Presets { get; set; } = new List<AppearancePreset>();
        public List<LayoutDefinition> Layouts { get; set; } = new List<LayoutDefinition>();
        public string LayoutDefaultPresetName { get; set; } = "Graphite";
        public string ActiveLayoutName { get; set; } = "";
        public string Language { get; set; } = "de";
        public bool StartWithWindows { get; set; }
        public string CloseBehavior { get; set; } = "Minimize";
        public DesktopAutoSortSettings DesktopAutoSort { get; set; } = new DesktopAutoSortSettings();
        public GlobalShortcutSettings GlobalShortcuts { get; set; } = new GlobalShortcutSettings();
    }
}
