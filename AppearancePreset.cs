namespace DesktopPlus
{
    public class AppearancePreset
    {
        public string Name { get; set; } = "";
        public AppearanceSettings Settings { get; set; } = new AppearanceSettings();
        public bool IsBuiltIn { get; set; }

        public override string ToString() => Name;
    }
}
