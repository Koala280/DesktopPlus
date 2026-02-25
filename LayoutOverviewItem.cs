using System.Collections.Generic;

namespace DesktopPlus
{
    public class LayoutOverviewItem
    {
        public string Name { get; set; } = "";
        public string Summary { get; set; } = "";
        public string DefaultPresetName { get; set; } = "";
        public List<AppearancePreset> Presets { get; set; } = new List<AppearancePreset>();
        public LayoutDefinition Layout { get; set; } = new LayoutDefinition();
    }
}
