using System.Collections.Generic;

namespace DesktopPlus
{
    public class PanelTabData
    {
        public string PanelId { get; set; } = "";
        public string TabId { get; set; } = "";
        public string TabName { get; set; } = "";
        public bool IsHidden { get; set; }
        public string PanelType { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public string DefaultFolderPath { get; set; } = "";
        public bool ShowHidden { get; set; }
        public bool ShowParentNavigationItem { get; set; } = true;
        public bool ShowFileExtensions { get; set; } = true;
        public bool OpenFoldersExternally { get; set; }
        public bool OpenItemsOnSingleClick { get; set; }
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
        public List<string> PinnedItems { get; set; } = new List<string>();
    }
}
