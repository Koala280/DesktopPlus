namespace DesktopPlus
{
    public class AppearanceSettings
    {
        public string BackgroundColor { get; set; } = "#242833";
        public double BackgroundOpacity { get; set; } = 0.84;
        public string HeaderColor { get; set; } = "#2A303B";
        public string AccentColor { get; set; } = "#6E8BFF";
        public string TextColor { get; set; } = "#F2F5FA";
        public string MutedTextColor { get; set; } = "#A7B0C0";
        public string FolderTextColor { get; set; } = "#6E8BFF";
        public string FontFamily { get; set; } = "Segoe UI";
        public double TitleFontSize { get; set; } = 16;
        public double ItemFontSize { get; set; } = 14;
        public double CornerRadius { get; set; } = 14;
        public double ShadowOpacity { get; set; } = 0.3;
        public double ShadowBlur { get; set; } = 20;
        public double HeaderShadowOpacity { get; set; } = -1;
        public double HeaderShadowBlur { get; set; } = -1;
        public double BodyShadowOpacity { get; set; } = -1;
        public double BodyShadowBlur { get; set; } = -1;
        public string PatternColor { get; set; } = "#6E8BFF";
        public double PatternOpacity { get; set; } = 0.25;
        public double PatternTileSize { get; set; } = 8;
        public double PatternStrokeThickness { get; set; } = 1;
        public string PatternCustomData { get; set; } = "";
        public string BackgroundMode { get; set; } = "Solid"; // Solid, Image, Pattern
        public string BackgroundImagePath { get; set; } = "";
        public double BackgroundImageOpacity { get; set; } = 0.8;
        public bool GlassEnabled { get; set; } = true;
        public bool ImageStretchFill { get; set; } = true;
        public string Pattern { get; set; } = "None"; // None, Diagonal, Grid, Dots
    }
}
