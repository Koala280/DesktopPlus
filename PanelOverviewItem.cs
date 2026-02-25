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
        public string ActionLabel { get; set; } = "";
        public string ToggleLabel { get; set; } = "";
        public DesktopPanel? Panel { get; set; }
        public string FolderPath { get; set; } = "";
        public string PanelKey { get; set; } = "";
        public PanelKind PanelType { get; set; } = PanelKind.None;
        public string PresetName { get; set; } = "";
    }
}
