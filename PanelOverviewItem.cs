namespace DesktopPlus
{
    public class PanelOverviewItem
    {
        public string Title { get; set; } = "";
        public string Folder { get; set; } = "";
        public string State { get; set; } = "";
        public string Size { get; set; } = "";
        public string Position { get; set; } = "";
        public bool IsOpen { get; set; }
        public bool IsHidden { get; set; }
        public bool ToggleIsShow { get; set; }
        public string ToggleLabel { get; set; } = "";
        public DesktopPanel? Panel { get; set; }
        public int HostTabIndex { get; set; } = -1;
        public bool RepresentsTab { get; set; }
        public string OwnerPanelKey { get; set; } = "";
        public string TabId { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public string PanelKey { get; set; } = "";
        public PanelKind PanelType { get; set; } = PanelKind.None;
        public string PresetName { get; set; } = "";
        public string OverviewSignature { get; set; } = "";
    }
}
