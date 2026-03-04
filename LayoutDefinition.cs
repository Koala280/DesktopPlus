using System.Collections.Generic;

namespace DesktopPlus
{
    public class LayoutDefinition
    {
        public string Name { get; set; } = "";
        public string ThemePresetName { get; set; } = "";
        public string DefaultPanelPresetName { get; set; } = "";
        public bool PanelDefaultShowHidden { get; set; }
        public bool PanelDefaultShowParentNavigationItem { get; set; } = true;
        public bool PanelDefaultShowFileExtensions { get; set; } = true;
        public bool PanelDefaultExpandOnHover { get; set; } = false;
        public bool PanelDefaultOpenFoldersExternally { get; set; }
        public bool PanelDefaultOpenItemsOnSingleClick { get; set; }
        public bool PanelDefaultShowSettingsButton { get; set; } = true;
        public string PanelDefaultMovementMode { get; set; } = "titlebar";
        public string PanelDefaultSearchVisibilityMode { get; set; } = "always";
        public string PanelDefaultViewMode { get; set; } = "icons";
        public bool PanelDefaultShowMetadataType { get; set; } = true;
        public bool PanelDefaultShowMetadataSize { get; set; } = true;
        public bool PanelDefaultShowMetadataCreated { get; set; }
        public bool PanelDefaultShowMetadataModified { get; set; } = true;
        public bool PanelDefaultShowMetadataDimensions { get; set; }
        public List<string> PanelDefaultMetadataOrder { get; set; } = new List<string>
        {
            "type",
            "size",
            "created",
            "modified",
            "dimensions"
        };
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public List<WindowData> Panels { get; set; } = new List<WindowData>();
    }
}
