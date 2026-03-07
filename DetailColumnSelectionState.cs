using System.Collections.Generic;

namespace DesktopPlus
{
    public class DetailColumnSelectionState
    {
        public bool ShowType { get; set; } = true;
        public bool ShowSize { get; set; } = true;
        public bool ShowCreated { get; set; }
        public bool ShowModified { get; set; } = true;
        public bool ShowDimensions { get; set; }
        public bool ShowAuthors { get; set; }
        public bool ShowCategories { get; set; }
        public bool ShowTags { get; set; }
        public bool ShowTitle { get; set; }
        public List<string> MetadataOrder { get; set; } = DesktopPanel.NormalizeMetadataOrder(null);
        public Dictionary<string, double> MetadataWidths { get; set; } = DesktopPanel.NormalizeMetadataWidths(null);
    }
}
