using System.Collections.Generic;

namespace DesktopPlus
{
    public class WindowData
    {
        public string PanelId { get; set; } = "";
        public string PanelType { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public string DefaultFolderPath { get; set; } = "";
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double ExpandedHeight { get; set; }
        public double Zoom { get; set; }
        public bool IsCollapsed { get; set; }
        public bool IsHidden { get; set; }
        public double CollapsedTop { get; set; }
        public double BaseTop { get; set; }
        public bool IsBottomAnchored { get; set; }
        public string PanelTitle { get; set; } = "";
        public string PresetName { get; set; } = "";
        public bool ShowHidden { get; set; } = true;
        public bool ShowParentNavigationItem { get; set; } = true;
        public string IconViewParentNavigationMode { get; set; } = DesktopPanel.IconParentNavigationModeHeader;
        public bool ShowSettingsButton { get; set; } = true;
        public string SettingsButtonVisibilityMode { get; set; } = DesktopPanel.SettingsButtonVisibilityExpandedOnly;
        public bool ShowCloseButton { get; set; } = true;
        public bool ShowEmptyRecycleBinButton { get; set; } = false;
        public bool ExpandOnHover { get; set; } = true;
        public bool OpenFoldersExternally { get; set; }
        public bool OpenItemsOnSingleClick { get; set; }
        public string CollapseBehavior { get; set; } = DesktopPanel.CollapseBehaviorBoth;
        public bool ShowFileExtensions { get; set; } = false;
        public string ViewMode { get; set; } = "icons";
        public bool ShowMetadataType { get; set; } = true;
        public bool ShowMetadataSize { get; set; } = true;
        public bool ShowMetadataCreated { get; set; }
        public bool ShowMetadataModified { get; set; } = true;
        public bool ShowMetadataDimensions { get; set; } = false;
        public bool ShowMetadataAuthors { get; set; }
        public bool ShowMetadataCategories { get; set; }
        public bool ShowMetadataTags { get; set; }
        public bool ShowMetadataTitle { get; set; }
        public List<string> MetadataOrder { get; set; } = new List<string>
        {
            "type",
            "size",
            "created",
            "modified",
            "dimensions",
            "authors",
            "categories",
            "tags",
            "title"
        };
        public Dictionary<string, double> MetadataWidths { get; set; } = DesktopPanel.NormalizeMetadataWidths(null);
        public string MovementMode { get; set; } = "titlebar";
        public string SearchVisibilityMode { get; set; } = DesktopPanel.SearchVisibilityButton;
        public bool? SearchVisibleOnlyExpanded { get; set; } = null;
        public string HeaderContentAlignment { get; set; } = DesktopPanel.HeaderContentAlignmentLeft;
        public List<string> PinnedItems { get; set; } = new List<string>();
        public List<PanelTabData>? Tabs { get; set; }
        public int ActiveTabIndex { get; set; } = 0;
    }
}
