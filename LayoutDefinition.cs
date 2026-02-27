using System.Collections.Generic;

namespace DesktopPlus
{
    public class LayoutDefinition
    {
        public string Name { get; set; } = "";
        public string ThemePresetName { get; set; } = "";
        public string DefaultPanelPresetName { get; set; } = "";
        public bool PanelDefaultShowHidden { get; set; }
        public bool PanelDefaultShowFileExtensions { get; set; } = true;
        public bool PanelDefaultExpandOnHover { get; set; } = true;
        public bool PanelDefaultOpenFoldersExternally { get; set; }
        public bool PanelDefaultShowSettingsButton { get; set; } = true;
        public string PanelDefaultMovementMode { get; set; } = "titlebar";
        public string PanelDefaultSearchVisibilityMode { get; set; } = "always";
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public List<WindowData> Panels { get; set; } = new List<WindowData>();
    }
}
