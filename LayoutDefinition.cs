using System.Collections.Generic;

namespace DesktopPlus
{
    public class LayoutDefinition
    {
        public string Name { get; set; } = "";
        public string ThemePresetName { get; set; } = "";
        public string DefaultPanelPresetName { get; set; } = "";
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public List<WindowData> Panels { get; set; } = new List<WindowData>();
    }
}
