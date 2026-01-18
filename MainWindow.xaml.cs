using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Collections.Generic;
using WinForms = System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Diagnostics;
using Microsoft.Win32;
using System.Globalization;
using MediaColor = System.Windows.Media.Color;
using Application = System.Windows.Application; // Für WPF

namespace DesktopPlus
{
    public enum PanelKind
    {
        None,
        Folder,
        List
    }

    public partial class MainWindow : Window
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
            public double Zoom { get; set; }
            public bool IsCollapsed { get; set; }
            public bool IsHidden { get; set; }
            public double CollapsedTop { get; set; }
            public double BaseTop { get; set; }
            public string PanelTitle { get; set; } = "";
            public string PresetName { get; set; } = "";
            public bool ShowHidden { get; set; }
            public bool ExpandOnHover { get; set; } = true;
            public bool OpenFoldersExternally { get; set; }
            public List<string> PinnedItems { get; set; } = new List<string>();
        }

        public class AppearanceSettings
        {
            public string BackgroundColor { get; set; } = "#242833";
            public double BackgroundOpacity { get; set; } = 0.94;
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
            public string BackgroundMode { get; set; } = "Solid"; // Solid, Image, Pattern
            public string BackgroundImagePath { get; set; } = "";
            public double BackgroundImageOpacity { get; set; } = 0.8;
            public bool GlassEnabled { get; set; } = false;
            public bool ImageStretchFill { get; set; } = true;
            public string Pattern { get; set; } = "None"; // None, Diagonal, Grid, Dots
        }

        public class AppearancePreset
        {
            public string Name { get; set; } = "";
            public AppearanceSettings Settings { get; set; } = new AppearanceSettings();
            public bool IsBuiltIn { get; set; }
        }

        public class LayoutDefinition
        {
            public string Name { get; set; } = "";
            public string ThemePresetName { get; set; } = "";
            public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
            public List<WindowData> Panels { get; set; } = new List<WindowData>();
        }

        public class AppState
        {
            public List<WindowData> Windows { get; set; } = new List<WindowData>();
            public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
            public List<AppearancePreset> Presets { get; set; } = new List<AppearancePreset>();
            public List<LayoutDefinition> Layouts { get; set; } = new List<LayoutDefinition>();
            public string Language { get; set; } = "de";
            public bool StartWithWindows { get; set; }
            public string CloseBehavior { get; set; } = "Minimize";
        }

        public class PanelOverviewItem
        {
            public string Title { get; set; } = "";
            public string Folder { get; set; } = "";
            public string State { get; set; } = "";
            public string Size { get; set; } = "";
            public string Position { get; set; } = "";
            public bool IsOpen { get; set; }
            public bool IsHidden { get; set; }
            public string ActionLabel { get; set; } = "";
            public DesktopPanel? Panel { get; set; }
            public string FolderPath { get; set; } = "";
            public string PanelKey { get; set; } = "";
            public PanelKind PanelType { get; set; } = PanelKind.None;
            public string PresetName { get; set; } = "";
        }

        public class LayoutOverviewItem
        {
            public string Name { get; set; } = "";
            public string Summary { get; set; } = "";
            public LayoutDefinition Layout { get; set; } = new LayoutDefinition();
        }

        private static List<WindowData> savedWindows = new List<WindowData>();
        public static AppearanceSettings Appearance { get; private set; } = new AppearanceSettings();
        public static List<AppearancePreset> Presets { get; private set; } = new List<AppearancePreset>();
        public static List<LayoutDefinition> Layouts { get; private set; } = new List<LayoutDefinition>();
        private static readonly string settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPlus_Settings.json"
        );

        private NotifyIcon? _notifyIcon;
        private bool _isExit = false;
        public static bool IsExiting { get; private set; }
        public static event Action? AppearanceChanged;
        public static event Action? PanelsChanged;
        private bool _isUiReady = false;
        private bool _suspendPresetSelection = false;
        private bool _suspendGeneralHandlers = false;
        private const string DefaultPresetName = "Noir";
        private const string DefaultLanguageCode = "de";
        private const string CloseBehaviorMinimize = "Minimize";
        private const string CloseBehaviorExit = "Exit";
        private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValue = "DesktopPlus";
        public static string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

        private string _languageCode = DefaultLanguageCode;
        private bool _startWithWindows = false;
        private string _closeBehavior = CloseBehaviorMinimize;

        private static readonly Dictionary<string, Dictionary<string, string>> LocalizationData =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["de"] = new Dictionary<string, string>
                {
                    ["Loc.TabGeneral"] = "Allgemein",
                    ["Loc.TabPanels"] = "Panels",
                    ["Loc.TabLayouts"] = "Layouts",
                    ["Loc.TabThemes"] = "Themen",
                    ["Loc.TabShortcuts"] = "Shortcuts",
                    ["Loc.GeneralTitle"] = "Allgemein",
                    ["Loc.GeneralLanguageLabel"] = "Sprache",
                    ["Loc.LanguageGerman"] = "Deutsch",
                    ["Loc.LanguageEnglish"] = "Englisch",
                    ["Loc.GeneralStartupCheckbox"] = "Mit Windows starten",
                    ["Loc.GeneralCloseLabel"] = "Beim Schließen",
                    ["Loc.CloseBehaviorMinimize"] = "Minimieren",
                    ["Loc.CloseBehaviorExit"] = "Programm beenden",
                    ["Loc.PanelsTitle"] = "Panels",
                    ["Loc.PanelsNew"] = "Neues Panel",
                    ["Loc.PanelsDesc"] = "Übersicht und Presets pro Panel.",
                    ["Loc.PanelsHide"] = "Ausblenden",
                    ["Loc.LayoutsTitle"] = "Layouts",
                    ["Loc.LayoutsCreateFromCurrent"] = "Layout aus aktuellem",
                    ["Loc.LayoutsCreateEmpty"] = "Leeres Layout",
                    ["Loc.LayoutsDesc"] = "Layouts speichern sichtbare Panels, Positionen und Theme.",
                    ["Loc.LayoutsApply"] = "Anwenden",
                    ["Loc.LayoutsUpdate"] = "Aktualisieren",
                    ["Loc.LayoutsDelete"] = "Löschen",
                    ["Loc.ThemesTitle"] = "Preset-Auswahl",
                    ["Loc.ThemesDesc"] = "Wähle ein Preset oder speichere dein aktuelles Theme.",
                    ["Loc.ThemesPresetTooltip"] = "Neues Preset",
                    ["Loc.ThemesApplyAll"] = "Auf alle",
                    ["Loc.ThemesSave"] = "Speichern",
                    ["Loc.ThemesDelete"] = "Löschen",
                    ["Loc.ThemesResetDefaults"] = "Standard-Presets",
                    ["Loc.FineTuneTitle"] = "Feintuning",
                    ["Loc.LabelBackgroundColor"] = "Hintergrundfarbe",
                    ["Loc.LabelHeaderColor"] = "Header-Farbe",
                    ["Loc.LabelOpacity"] = "Deckkraft",
                    ["Loc.LabelAccentColor"] = "Akzentfarbe",
                    ["Loc.TypographyTitle"] = "Typografie",
                    ["Loc.LabelFontFamily"] = "Schriftart",
                    ["Loc.LabelTextColor"] = "Textfarbe",
                    ["Loc.LabelFolderColor"] = "Ordnerfarbe",
                    ["Loc.LabelTitleSize"] = "Titelgröße",
                    ["Loc.LabelItemSize"] = "Textgröße",
                    ["Loc.LabelCornerRadius"] = "Eckenradius",
                    ["Loc.LabelShadow"] = "Schatten",
                    ["Loc.LabelMode"] = "Modus",
                    ["Loc.ModeSolid"] = "Farbe",
                    ["Loc.ModeImage"] = "Bild",
                    ["Loc.ModePattern"] = "Muster",
                    ["Loc.LabelPattern"] = "Muster",
                    ["Loc.PatternNone"] = "Keine",
                    ["Loc.PatternDiagonal"] = "Diagonal",
                    ["Loc.PatternGrid"] = "Karos",
                    ["Loc.PatternDots"] = "Punkte",
                    ["Loc.LabelBackgroundImage"] = "Hintergrundbild",
                    ["Loc.ButtonBrowse"] = "...",
                    ["Loc.OptionGlass"] = "Glass-Effekt",
                    ["Loc.OptionImageFill"] = "Bild füllen",
                    ["Loc.LabelImageGlassIntensity"] = "Bild/Glass Intensität",
                    ["Loc.PreviewTitle"] = "Vorschau",
                    ["Loc.PreviewFolderDocs"] = "Dokumente",
                    ["Loc.PreviewFolderPhotos"] = "Bilder",
                    ["Loc.PreviewFolderProjects"] = "Projekte",
                    ["Loc.PreviewFileReadme"] = "readme.md",
                    ["Loc.PreviewFileTodo"] = "todo.txt",
                    ["Loc.PreviewFileBudget"] = "Budget.xlsx",
                    ["Loc.ShortcutsTitle"] = "Shortcuts",
                    ["Loc.ShortcutsDesc"] = "Globale Hotkeys für Panels und Layouts kommen bald.",
                    ["Loc.PanelStateOpen"] = "Offen",
                    ["Loc.PanelStateCollapsed"] = "Eingeklappt",
                    ["Loc.PanelStateHidden"] = "Ausgeblendet",
                    ["Loc.PanelStateClosed"] = "Geschlossen",
                    ["Loc.PanelTypeList"] = "Liste ({0})",
                    ["Loc.PanelActionFocus"] = "Fokussieren",
                    ["Loc.PanelActionShow"] = "Anzeigen",
                    ["Loc.PanelActionReveal"] = "Einblenden",
                    ["Loc.PanelCount"] = "{0} Panels",
                    ["Loc.PanelCountHidden"] = "{0} Panels, {1} ausgeblendet",
                    ["Loc.LayoutCount"] = "{0} Layouts",
                    ["Loc.LayoutSummaryEmpty"] = "Leer",
                    ["Loc.LayoutSummaryVisibleHidden"] = "{0} sichtbar, {1} ausgeblendet",
                    ["Loc.LayoutSummaryTheme"] = "Theme",
                    ["Loc.Untitled"] = "Unbenannt",
                    ["Loc.NoFolder"] = "(kein Ordner)",
                    ["Loc.PanelWindowTitle"] = "Panel",
                    ["Loc.PanelDefaultTitle"] = "Panel",
                    ["Loc.PanelMoveTooltip"] = "Fenster verschieben",
                    ["Loc.PanelSettingsTooltip"] = "Panel-Einstellungen",
                    ["Loc.PanelSearchPlaceholder"] = "Suchen...",
                    ["Loc.PanelCollapseTooltip"] = "Inhalt ein-/ausklappen",
                    ["Loc.PanelCloseTooltip"] = "Schließen",
                    ["Loc.PanelSettingsTitle"] = "Panel-Einstellungen",
                    ["Loc.PanelSettingsName"] = "Name",
                    ["Loc.PanelSettingsPreset"] = "Preset",
                    ["Loc.PanelSettingsDefaultFolder"] = "Standardordner",
                    ["Loc.PanelSettingsFolderUnset"] = "(nicht gesetzt)",
                    ["Loc.PanelSettingsChange"] = "Ändern",
                    ["Loc.PanelSettingsFolderAction"] = "Ordner-Aktion",
                    ["Loc.PanelSettingsOpenInternal"] = "Im Panel navigieren",
                    ["Loc.PanelSettingsOpenExternal"] = "Im Explorer öffnen",
                    ["Loc.PanelSettingsExpandOnHover"] = "Beim Hover aufklappen",
                    ["Loc.PanelSettingsShowHidden"] = "Versteckte Elemente anzeigen",
                    ["Loc.PanelSettingsCancel"] = "Abbrechen",
                    ["Loc.PanelSettingsSave"] = "Speichern",
                    ["Loc.InputDialogTitle"] = "Eingabe",
                    ["Loc.InputDialogOk"] = "OK",
                    ["Loc.InputDialogCancel"] = "Abbrechen",
                    ["Loc.TrayOpen"] = "Öffnen",
                    ["Loc.TrayExit"] = "Beenden",
                    ["Loc.MsgError"] = "Fehler",
                    ["Loc.MsgInfo"] = "Hinweis",
                    ["Loc.MsgPresetNameRequired"] = "Bitte einen Preset-Namen eingeben.",
                    ["Loc.MsgPresetBuiltIn"] = "Ein eingebautes Preset kann nicht überschrieben werden. Wähle einen anderen Namen.",
                    ["Loc.MsgPresetBuiltInDelete"] = "Eingebaute Presets können nicht gelöscht werden.",
                    ["Loc.MsgPresetReset"] = "Standard-Presets wurden zurückgesetzt.",
                    ["Loc.MsgRestoreError"] = "Fehler beim Wiederherstellen der Fenster:\n{0}",
                    ["Loc.MsgSaveError"] = "Fehler beim Speichern der Fenster:\n{0}",
                    ["Loc.MsgMoveFolderError"] = "Ordner konnte nicht verschoben werden:\n{0}",
                    ["Loc.MsgMoveFileError"] = "Datei konnte nicht verschoben werden:\n{0}",
                    ["Loc.MsgOpenFolderError"] = "Fehler beim Öffnen des Ordners:\n{0}",
                    ["Loc.MsgOpenFileError"] = "Fehler beim Öffnen der Datei:\n{0}",
                    ["Loc.MsgStartupError"] = "Autostart konnte nicht aktualisiert werden:\n{0}",
                    ["Loc.PromptLayoutName"] = "Layout-Name",
                    ["Loc.PromptPanelName"] = "Panel-Name"
                },
                ["en"] = new Dictionary<string, string>
                {
                    ["Loc.TabGeneral"] = "General",
                    ["Loc.TabPanels"] = "Panels",
                    ["Loc.TabLayouts"] = "Layouts",
                    ["Loc.TabThemes"] = "Themes",
                    ["Loc.TabShortcuts"] = "Shortcuts",
                    ["Loc.GeneralTitle"] = "General",
                    ["Loc.GeneralLanguageLabel"] = "Language",
                    ["Loc.LanguageGerman"] = "German",
                    ["Loc.LanguageEnglish"] = "English",
                    ["Loc.GeneralStartupCheckbox"] = "Start with Windows",
                    ["Loc.GeneralCloseLabel"] = "On close",
                    ["Loc.CloseBehaviorMinimize"] = "Minimize",
                    ["Loc.CloseBehaviorExit"] = "Exit application",
                    ["Loc.PanelsTitle"] = "Panels",
                    ["Loc.PanelsNew"] = "New panel",
                    ["Loc.PanelsDesc"] = "Overview and presets per panel.",
                    ["Loc.PanelsHide"] = "Hide",
                    ["Loc.LayoutsTitle"] = "Layouts",
                    ["Loc.LayoutsCreateFromCurrent"] = "From current",
                    ["Loc.LayoutsCreateEmpty"] = "Empty layout",
                    ["Loc.LayoutsDesc"] = "Layouts store visible panels, positions, and theme.",
                    ["Loc.LayoutsApply"] = "Apply",
                    ["Loc.LayoutsUpdate"] = "Update",
                    ["Loc.LayoutsDelete"] = "Delete",
                    ["Loc.ThemesTitle"] = "Preset selection",
                    ["Loc.ThemesDesc"] = "Choose a preset or save your current theme.",
                    ["Loc.ThemesPresetTooltip"] = "New preset",
                    ["Loc.ThemesApplyAll"] = "Apply to all",
                    ["Loc.ThemesSave"] = "Save",
                    ["Loc.ThemesDelete"] = "Delete",
                    ["Loc.ThemesResetDefaults"] = "Reset defaults",
                    ["Loc.FineTuneTitle"] = "Fine tune",
                    ["Loc.LabelBackgroundColor"] = "Background color",
                    ["Loc.LabelHeaderColor"] = "Header color",
                    ["Loc.LabelOpacity"] = "Opacity",
                    ["Loc.LabelAccentColor"] = "Accent color",
                    ["Loc.TypographyTitle"] = "Typography",
                    ["Loc.LabelFontFamily"] = "Font",
                    ["Loc.LabelTextColor"] = "Text color",
                    ["Loc.LabelFolderColor"] = "Folder color",
                    ["Loc.LabelTitleSize"] = "Title size",
                    ["Loc.LabelItemSize"] = "Item size",
                    ["Loc.LabelCornerRadius"] = "Corner radius",
                    ["Loc.LabelShadow"] = "Shadow",
                    ["Loc.LabelMode"] = "Mode",
                    ["Loc.ModeSolid"] = "Solid",
                    ["Loc.ModeImage"] = "Image",
                    ["Loc.ModePattern"] = "Pattern",
                    ["Loc.LabelPattern"] = "Pattern",
                    ["Loc.PatternNone"] = "None",
                    ["Loc.PatternDiagonal"] = "Diagonal",
                    ["Loc.PatternGrid"] = "Grid",
                    ["Loc.PatternDots"] = "Dots",
                    ["Loc.LabelBackgroundImage"] = "Background image",
                    ["Loc.ButtonBrowse"] = "...",
                    ["Loc.OptionGlass"] = "Glass effect",
                    ["Loc.OptionImageFill"] = "Fill image",
                    ["Loc.LabelImageGlassIntensity"] = "Image/Glass intensity",
                    ["Loc.PreviewTitle"] = "Preview",
                    ["Loc.PreviewFolderDocs"] = "Documents",
                    ["Loc.PreviewFolderPhotos"] = "Pictures",
                    ["Loc.PreviewFolderProjects"] = "Projects",
                    ["Loc.PreviewFileReadme"] = "readme.md",
                    ["Loc.PreviewFileTodo"] = "todo.txt",
                    ["Loc.PreviewFileBudget"] = "Budget.xlsx",
                    ["Loc.ShortcutsTitle"] = "Shortcuts",
                    ["Loc.ShortcutsDesc"] = "Global hotkeys for panels and layouts are coming soon.",
                    ["Loc.PanelStateOpen"] = "Open",
                    ["Loc.PanelStateCollapsed"] = "Collapsed",
                    ["Loc.PanelStateHidden"] = "Hidden",
                    ["Loc.PanelStateClosed"] = "Closed",
                    ["Loc.PanelTypeList"] = "List ({0})",
                    ["Loc.PanelActionFocus"] = "Focus",
                    ["Loc.PanelActionShow"] = "Show",
                    ["Loc.PanelActionReveal"] = "Reveal",
                    ["Loc.PanelCount"] = "{0} panels",
                    ["Loc.PanelCountHidden"] = "{0} panels, {1} hidden",
                    ["Loc.LayoutCount"] = "{0} layouts",
                    ["Loc.LayoutSummaryEmpty"] = "Empty",
                    ["Loc.LayoutSummaryVisibleHidden"] = "{0} visible, {1} hidden",
                    ["Loc.LayoutSummaryTheme"] = "Theme",
                    ["Loc.Untitled"] = "Untitled",
                    ["Loc.NoFolder"] = "(no folder)",
                    ["Loc.PanelWindowTitle"] = "Panel",
                    ["Loc.PanelDefaultTitle"] = "Panel",
                    ["Loc.PanelMoveTooltip"] = "Move window",
                    ["Loc.PanelSettingsTooltip"] = "Panel settings",
                    ["Loc.PanelSearchPlaceholder"] = "Search...",
                    ["Loc.PanelCollapseTooltip"] = "Collapse/expand content",
                    ["Loc.PanelCloseTooltip"] = "Close",
                    ["Loc.PanelSettingsTitle"] = "Panel settings",
                    ["Loc.PanelSettingsName"] = "Name",
                    ["Loc.PanelSettingsPreset"] = "Preset",
                    ["Loc.PanelSettingsDefaultFolder"] = "Default folder",
                    ["Loc.PanelSettingsFolderUnset"] = "(not set)",
                    ["Loc.PanelSettingsChange"] = "Change",
                    ["Loc.PanelSettingsFolderAction"] = "Folder action",
                    ["Loc.PanelSettingsOpenInternal"] = "Navigate in panel",
                    ["Loc.PanelSettingsOpenExternal"] = "Open in Explorer",
                    ["Loc.PanelSettingsExpandOnHover"] = "Expand on hover",
                    ["Loc.PanelSettingsShowHidden"] = "Show hidden items",
                    ["Loc.PanelSettingsCancel"] = "Cancel",
                    ["Loc.PanelSettingsSave"] = "Save",
                    ["Loc.InputDialogTitle"] = "Input",
                    ["Loc.InputDialogOk"] = "OK",
                    ["Loc.InputDialogCancel"] = "Cancel",
                    ["Loc.TrayOpen"] = "Open",
                    ["Loc.TrayExit"] = "Exit",
                    ["Loc.MsgError"] = "Error",
                    ["Loc.MsgInfo"] = "Info",
                    ["Loc.MsgPresetNameRequired"] = "Please enter a preset name.",
                    ["Loc.MsgPresetBuiltIn"] = "Built-in presets cannot be overwritten. Choose a different name.",
                    ["Loc.MsgPresetBuiltInDelete"] = "Built-in presets cannot be deleted.",
                    ["Loc.MsgPresetReset"] = "Default presets have been reset.",
                    ["Loc.MsgRestoreError"] = "Error restoring windows:\n{0}",
                    ["Loc.MsgSaveError"] = "Error saving windows:\n{0}",
                    ["Loc.MsgMoveFolderError"] = "Folder could not be moved:\n{0}",
                    ["Loc.MsgMoveFileError"] = "File could not be moved:\n{0}",
                    ["Loc.MsgOpenFolderError"] = "Error opening folder:\n{0}",
                    ["Loc.MsgOpenFileError"] = "Error opening file:\n{0}",
                    ["Loc.MsgStartupError"] = "Autostart could not be updated:\n{0}",
                    ["Loc.PromptLayoutName"] = "Layout name",
                    ["Loc.PromptPanelName"] = "Panel name"
                }
            };

        private void ApplyLanguage(string? code)
        {
            string normalized = string.IsNullOrWhiteSpace(code) ? DefaultLanguageCode : code.Trim();
            if (!LocalizationData.ContainsKey(normalized))
            {
                normalized = DefaultLanguageCode;
            }

            CurrentLanguageCode = normalized;
            if (Application.Current?.Resources != null &&
                LocalizationData.TryGetValue(normalized, out var values))
            {
                foreach (var pair in values)
                {
                    Application.Current.Resources[pair.Key] = pair.Value;
                }
            }

            UpdateNotifyIconMenu();
        }

        public static string GetString(string key)
        {
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.Contains(key))
            {
                return Application.Current.Resources[key]?.ToString() ?? key;
            }

            if (LocalizationData.TryGetValue(CurrentLanguageCode, out var values) &&
                values.TryGetValue(key, out var value))
            {
                return value;
            }

            return key;
        }

        private static PanelKind ResolvePanelKind(WindowData data)
        {
            if (!string.IsNullOrWhiteSpace(data.PanelType) &&
                Enum.TryParse(data.PanelType, true, out PanelKind parsed))
            {
                return parsed;
            }

            if (!string.IsNullOrWhiteSpace(data.FolderPath))
            {
                return PanelKind.Folder;
            }

            if (data.PinnedItems != null && data.PinnedItems.Count > 0)
            {
                return PanelKind.List;
            }

            return PanelKind.None;
        }

        private static PanelKind ResolvePanelKind(DesktopPanel panel)
        {
            if (panel == null) return PanelKind.None;
            if (panel.PanelType != PanelKind.None) return panel.PanelType;
            if (!string.IsNullOrWhiteSpace(panel.currentFolderPath)) return PanelKind.Folder;
            if (panel.PinnedItems.Count > 0) return PanelKind.List;
            return PanelKind.None;
        }

        private static string EnsurePanelId(WindowData data)
        {
            if (!string.IsNullOrWhiteSpace(data.PanelId)) return data.PanelId;

            var kind = ResolvePanelKind(data);
            if (kind == PanelKind.Folder && !string.IsNullOrWhiteSpace(data.FolderPath))
            {
                data.PanelId = $"folder:{data.FolderPath}";
            }
            else if (kind == PanelKind.List)
            {
                data.PanelId = $"list:{Guid.NewGuid():N}";
            }
            else
            {
                data.PanelId = $"panel:{Guid.NewGuid():N}";
            }

            return data.PanelId;
        }

        private static string GetPanelKey(WindowData data)
        {
            var kind = ResolvePanelKind(data);
            if (kind == PanelKind.Folder && !string.IsNullOrWhiteSpace(data.FolderPath))
            {
                return $"folder:{data.FolderPath}";
            }

            return EnsurePanelId(data);
        }

        private static string GetPanelKey(DesktopPanel panel)
        {
            if (!string.IsNullOrWhiteSpace(panel.currentFolderPath))
            {
                return $"folder:{panel.currentFolderPath}";
            }

            if (string.IsNullOrWhiteSpace(panel.PanelId))
            {
                panel.PanelId = $"panel:{Guid.NewGuid():N}";
            }
            return panel.PanelId;
        }

        private static WindowData? FindSavedWindow(string panelKey)
        {
            return savedWindows.FirstOrDefault(x =>
                string.Equals(GetPanelKey(x), panelKey, StringComparison.OrdinalIgnoreCase));
        }

        private static void NormalizeWindowData(WindowData data)
        {
            if (data == null) return;
            data.PinnedItems ??= new List<string>();
            var kind = ResolvePanelKind(data);
            if (string.IsNullOrWhiteSpace(data.PanelType))
            {
                data.PanelType = kind.ToString();
            }
            EnsurePanelId(data);
        }

        private static WindowData BuildWindowDataFromPanel(DesktopPanel panel)
        {
            var kind = ResolvePanelKind(panel);
            var folderPath = kind == PanelKind.Folder ? panel.currentFolderPath : "";
            var pinnedItems = kind == PanelKind.List
                ? panel.PinnedItems.ToList()
                : new List<string>();

            return new WindowData
            {
                PanelId = panel.PanelId,
                PanelType = kind.ToString(),
                FolderPath = folderPath ?? "",
                DefaultFolderPath = panel.defaultFolderPath ?? "",
                Left = panel.Left,
                Top = panel.Top,
                Width = panel.Width,
                Height = panel.Height,
                Zoom = panel.zoomFactor,
                IsCollapsed = !panel.isContentVisible,
                IsHidden = false,
                BaseTop = panel.baseTopPosition,
                PanelTitle = panel.PanelTitle.Text,
                PresetName = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName,
                ShowHidden = panel.showHiddenItems,
                ExpandOnHover = panel.expandOnHover,
                OpenFoldersExternally = panel.openFoldersExternally,
                PinnedItems = pinnedItems
            };
        }

        public MainWindow()
        {
            InitializeComponent();
            TrySetWindowIcon();
            LoadSettings();
            ApplyLanguage(_languageCode);
            if (!Presets.Any())
            {
                Presets = GetDefaultPresets();
            }
            InitNotifyIcon();

            this.Closing += OnWindowClosing;
            this.StateChanged += OnWindowStateChanged;
            this.Loaded += (s, e) => RefreshPanelOverview();
            PanelsChanged += RefreshPanelOverview;
            this.Closed += (s, e) => PanelsChanged -= RefreshPanelOverview;

        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon();
            _notifyIcon.Icon = new System.Drawing.Icon("Resources/desktopplus_icon.ico");
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "DesktopPlus";
            UpdateNotifyIconMenu();
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void UpdateNotifyIconMenu()
        {
            if (_notifyIcon == null) return;

            var contextMenu = new WinForms.ContextMenuStrip();
            contextMenu.Items.Add(GetString("Loc.TrayOpen"), null, (s, e) => ShowMainWindow());
            contextMenu.Items.Add(GetString("Loc.TrayExit"), null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void TrySetWindowIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "desktopplus_icon.ico");
                if (!File.Exists(iconPath)) return;

                using var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Icon.Freeze();
            }
            catch
            {
                // Ignore invalid or missing icon assets to keep the window usable.
            }
        }

        private static bool IsStartWithWindowsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
                if (key == null) return false;
                var value = key.GetValue(StartupRegistryValue) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        private void SetStartWithWindows(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
                if (key == null) return;

                if (enabled)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName
                        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrWhiteSpace(exePath)) return;
                    key.SetValue(StartupRegistryValue, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(StartupRegistryValue, false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgStartupError"), ex.Message),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            _isExit = true;
            IsExiting = true;
            _notifyIcon?.Dispose();
            SaveSettings();
            Application.Current.Shutdown();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExit) return;

            if (string.Equals(_closeBehavior, CloseBehaviorExit, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                ExitApplication();
                return;
            }

            e.Cancel = true;
            WindowState = WindowState.Minimized;
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            // Früher wurde das Fenster bei Minimierung ausgeblendet, was zu
            // Verwirrung führte. Jetzt bleibt es normal minimiert sichtbar.
        }

        private void LoadSettings()
        {
            if (!File.Exists(settingsFilePath)) return;

            try
            {
                string json = File.ReadAllText(settingsFilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                AppState state;

                try
                {
                    state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
                }
                catch
                {
                    var legacy = JsonSerializer.Deserialize<List<WindowData>>(json) ?? new List<WindowData>();
                    state = new AppState { Windows = legacy };
                }

                savedWindows = state.Windows ?? new List<WindowData>();
                foreach (var window in savedWindows)
                {
                    NormalizeWindowData(window);
                }
                Appearance = state.Appearance ?? new AppearanceSettings();
                Layouts = state.Layouts ?? new List<LayoutDefinition>();
                _languageCode = string.IsNullOrWhiteSpace(state.Language) ? DefaultLanguageCode : state.Language;
                _closeBehavior = string.IsNullOrWhiteSpace(state.CloseBehavior) ? CloseBehaviorMinimize : state.CloseBehavior;
                if (!string.Equals(_closeBehavior, CloseBehaviorMinimize, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_closeBehavior, CloseBehaviorExit, StringComparison.OrdinalIgnoreCase))
                {
                    _closeBehavior = CloseBehaviorMinimize;
                }
                bool registryStartup = IsStartWithWindowsEnabled();
                _startWithWindows = state.StartWithWindows || registryStartup;
                foreach (var layout in Layouts)
                {
                    layout.Panels ??= new List<WindowData>();
                    layout.Appearance ??= new AppearanceSettings();
                    foreach (var panel in layout.Panels)
                    {
                        NormalizeWindowData(panel);
                    }
                }

                var defaults = GetDefaultPresets();
                var loadedPresets = state.Presets ?? new List<AppearancePreset>();
                var presetDict = defaults.Concat(loadedPresets)
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
                Presets = presetDict.Values.OrderBy(p => p.IsBuiltIn ? 0 : 1).ThenBy(p => p.Name).ToList();

                foreach (var winData in savedWindows)
                {
                    if (winData.IsHidden) continue;
                    var kind = ResolvePanelKind(winData);
                    if (kind == PanelKind.Folder)
                    {
                        if (!Directory.Exists(winData.FolderPath)) continue;
                    }
                    else if (kind == PanelKind.None)
                    {
                        continue;
                    }

                    Dispatcher.Invoke(() => OpenPanelFromData(winData));
                }

                AppearanceChanged?.Invoke();
                NotifyPanelsChanged();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgRestoreError"), ex.Message),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public static void SaveSettings()
        {
            try
            {
                var open = Application.Current.Windows.OfType<DesktopPanel>()
                    .Where(win => win.PanelType != PanelKind.None ||
                                  !string.IsNullOrWhiteSpace(win.currentFolderPath) ||
                                  win.PinnedItems.Count > 0)
                    .Select(BuildWindowDataFromPanel)
                    .ToList();

                var dict = savedWindows.ToDictionary(
                    x => GetPanelKey(x),
                    x => x,
                    StringComparer.OrdinalIgnoreCase);

                foreach (var item in open)
                {
                    dict[GetPanelKey(item)] = item;
                }

                savedWindows = dict.Values
                    .OrderBy(x => string.IsNullOrWhiteSpace(x.PanelTitle) ? x.FolderPath : x.PanelTitle)
                    .ThenBy(x => x.FolderPath)
                    .ToList();

                var mainWindow = Application.Current?.MainWindow as MainWindow;
                string language = mainWindow?._languageCode ?? CurrentLanguageCode;
                bool startWithWindows = mainWindow?._startWithWindows ?? IsStartWithWindowsEnabled();
                string closeBehavior = mainWindow?._closeBehavior ?? CloseBehaviorMinimize;

                var state = new AppState
                {
                    Windows = savedWindows,
                    Appearance = Appearance ?? new AppearanceSettings(),
                    Presets = Presets,
                    Layouts = Layouts,
                    Language = language,
                    StartWithWindows = startWithWindows,
                    CloseBehavior = closeBehavior
                };

                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                });
                File.WriteAllText(settingsFilePath, json);
                NotifyPanelsChanged();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgSaveError"), ex.Message),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public static void UpdateAppearance(AppearanceSettings newAppearance)
        {
            Appearance = newAppearance ?? new AppearanceSettings();
            AppearanceChanged?.Invoke();
            SaveSettings();
        }

        public static AppearanceSettings GetPresetSettings(string presetName)
        {
            if (!string.IsNullOrWhiteSpace(presetName))
            {
                var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
                if (preset != null) return preset.Settings;
            }
            return Presets.FirstOrDefault(p => p.Name == DefaultPresetName)?.Settings ?? Appearance;
        }

        public static void NotifyPanelsChanged()
        {
            PanelsChanged?.Invoke();
        }

        private List<AppearancePreset> GetDefaultPresets()
        {
            return new List<AppearancePreset>
            {
                new AppearancePreset { Name = "Noir", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#242833", BackgroundOpacity=0.94, HeaderColor="#2A303B", AccentColor="#6E8BFF", FolderTextColor="#6E8BFF", CornerRadius=14, ShadowOpacity=0.3, ShadowBlur=20 } },
                new AppearancePreset { Name = "Slate", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#1C2430", BackgroundOpacity=0.9, HeaderColor="#192029", AccentColor="#6BD5C1", FolderTextColor="#6BD5C1", CornerRadius=12, ShadowOpacity=0.3, ShadowBlur=16 } },
                new AppearancePreset { Name = "Frost", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#0F141C", BackgroundOpacity=0.88, HeaderColor="#101723", AccentColor="#64A9FF", FolderTextColor="#64A9FF", CornerRadius=16, ShadowOpacity=0.28, ShadowBlur=20 } },
                new AppearancePreset { Name = "Carbon", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#161616", BackgroundOpacity=0.94, HeaderColor="#1E1E1E", AccentColor="#F5A524", FolderTextColor="#F5A524", CornerRadius=10, ShadowOpacity=0.32, ShadowBlur=14 } },
                new AppearancePreset { Name = "Emerald", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#0F1714", BackgroundOpacity=0.9, HeaderColor="#12211B", AccentColor="#4ADE80", FolderTextColor="#4ADE80", CornerRadius=13, ShadowOpacity=0.33, ShadowBlur=18 } },
                new AppearancePreset { Name = "Rosé", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#1B1418", BackgroundOpacity=0.9, HeaderColor="#221820", AccentColor="#FF7EB6", FolderTextColor="#FF7EB6", CornerRadius=14, ShadowOpacity=0.3, ShadowBlur=16 } },
                new AppearancePreset { Name = "Cobalt", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#0B1624", BackgroundOpacity=0.9, HeaderColor="#0E1C2E", AccentColor="#3B82F6", FolderTextColor="#3B82F6", CornerRadius=12, ShadowOpacity=0.34, ShadowBlur=18 } },
                new AppearancePreset { Name = "Graphite", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#202327", BackgroundOpacity=0.9, HeaderColor="#252A30", AccentColor="#A3B1C2", FolderTextColor="#A3B1C2", CornerRadius=11, ShadowOpacity=0.27, ShadowBlur=14 } },
                new AppearancePreset { Name = "Sand", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#1B1916", BackgroundOpacity=0.9, HeaderColor="#25221D", AccentColor="#E8B76B", FolderTextColor="#E8B76B", CornerRadius=12, ShadowOpacity=0.3, ShadowBlur=16 } },
                new AppearancePreset { Name = "Mint", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#101817", BackgroundOpacity=0.9, HeaderColor="#13201D", AccentColor="#70E4C6", FolderTextColor="#70E4C6", CornerRadius=14, ShadowOpacity=0.32, ShadowBlur=18 } },
            };
        }

        private void OpenDesktopPanel_Click(object sender, RoutedEventArgs e)
        {
            var selectedPreset = (PresetComboTop?.SelectedItem as AppearancePreset)?.Name ?? DefaultPresetName;
            var panel = CreatePanelWithPreset(selectedPreset);
            panel.Show();
            PanelsChanged?.Invoke();
        }

        private void ApplyGeneralSettingsToUi()
        {
            _suspendGeneralHandlers = true;
            if (LanguageCombo != null)
            {
                LanguageCombo.SelectedValue = _languageCode;
            }
            if (StartupToggle != null)
            {
                StartupToggle.IsChecked = _startWithWindows;
            }
            if (CloseBehaviorCombo != null)
            {
                CloseBehaviorCombo.SelectedValue = _closeBehavior;
            }
            _suspendGeneralHandlers = false;
        }

        private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendGeneralHandlers) return;
            if (LanguageCombo?.SelectedValue is string code)
            {
                _languageCode = code;
                ApplyLanguage(_languageCode);
                RefreshPanelOverview();
                RefreshLayoutList();
                RefreshPresetSelectors();
                SaveSettings();
            }
        }

        private void StartupToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendGeneralHandlers) return;
            _startWithWindows = StartupToggle?.IsChecked == true;
            SetStartWithWindows(_startWithWindows);
            SaveSettings();
        }

        private void CloseBehaviorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendGeneralHandlers) return;
            if (CloseBehaviorCombo?.SelectedValue is string value)
            {
                _closeBehavior = value;
                SaveSettings();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateAppearanceInputs(Appearance);
            UpdatePreview(Appearance);
            RefreshPresetSelectors();
            RefreshLayoutList();
            ApplyGeneralSettingsToUi();
            _isUiReady = true;
        }

        private void PopulateAppearanceInputs(AppearanceSettings appearance)
        {
            if (appearance == null) return;

            BackgroundColorInput.Text = appearance.BackgroundColor;
            HeaderColorInput.Text = appearance.HeaderColor;
            AccentColorInput.Text = appearance.AccentColor;
            if (TextColorInput != null) TextColorInput.Text = appearance.TextColor;
            if (FolderColorInput != null)
            {
                FolderColorInput.Text = string.IsNullOrWhiteSpace(appearance.FolderTextColor)
                    ? appearance.AccentColor
                    : appearance.FolderTextColor;
            }
            if (FontFamilyInput != null) FontFamilyInput.Text = appearance.FontFamily;
            if (TitleFontSizeInput != null) TitleFontSizeInput.Text = appearance.TitleFontSize.ToString(CultureInfo.CurrentCulture);
            if (ItemFontSizeInput != null) ItemFontSizeInput.Text = appearance.ItemFontSize.ToString(CultureInfo.CurrentCulture);
            OpacitySlider.Value = appearance.BackgroundOpacity;
            CornerRadiusSlider.Value = appearance.CornerRadius;
            ShadowOpacitySlider.Value = appearance.ShadowOpacity;
            ShadowBlurSlider.Value = appearance.ShadowBlur;
            if (BackgroundModeCombo != null)
            {
                foreach (ComboBoxItem item in BackgroundModeCombo.Items)
                {
                    if ((item.Tag as string)?.Equals(appearance.BackgroundMode, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        BackgroundModeCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            if (PatternCombo != null)
            {
                foreach (ComboBoxItem item in PatternCombo.Items)
                {
                    if ((item.Tag as string)?.Equals(appearance.Pattern, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        PatternCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            if (ImageOpacitySlider != null) ImageOpacitySlider.Value = appearance.BackgroundImageOpacity;
            if (GlassToggle != null) GlassToggle.IsChecked = appearance.GlassEnabled;
            if (ImageFitToggle != null) ImageFitToggle.IsChecked = appearance.ImageStretchFill;
            if (ImagePathInput != null) ImagePathInput.Text = appearance.BackgroundImagePath ?? "";
        }

        private void AppearanceInputChanged(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady) return;

            var appearance = BuildAppearanceFromUi();
            UpdatePreview(appearance);
            UpdateAppearance(appearance);
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = (PresetNameInput.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgPresetNameRequired"),
                    GetString("Loc.MsgInfo"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var appearance = BuildAppearanceFromUi();
            var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.IsBuiltIn)
            {
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgPresetBuiltIn"),
                    GetString("Loc.MsgInfo"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (existing != null)
            {
                existing.Settings = appearance;
                existing.IsBuiltIn = false;
            }
            else
            {
                Presets.Add(new AppearancePreset { Name = name, Settings = appearance, IsBuiltIn = false });
            }

            RefreshPresetSelectors(name);
            SaveSettings();
        }

        private void GlobalPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendPresetSelection) return;
            if (!_isUiReady) return;
            if (PresetComboTop.SelectedItem is AppearancePreset preset)
            {
                PopulateAppearanceInputs(preset.Settings);
                UpdatePreview(preset.Settings);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboTop.SelectedItem is AppearancePreset preset)
            {
                if (preset.IsBuiltIn)
                {
                    System.Windows.MessageBox.Show(
                        GetString("Loc.MsgPresetBuiltInDelete"),
                        GetString("Loc.MsgInfo"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                Presets.Remove(preset);
                RefreshPresetSelectors();
                SaveSettings();
            }
        }

        private void ResetStandardPresets_Click(object sender, RoutedEventArgs e)
        {
            var defaults = GetDefaultPresets();
            var defaultNames = new HashSet<string>(defaults.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var custom = Presets.Where(p => !p.IsBuiltIn && !defaultNames.Contains(p.Name)).ToList();

            var merged = new List<AppearancePreset>();
            foreach (var preset in defaults)
            {
                merged.Add(new AppearancePreset
                {
                    Name = preset.Name,
                    Settings = CloneAppearance(preset.Settings),
                    IsBuiltIn = true
                });
            }

            foreach (var preset in custom)
            {
                merged.Add(new AppearancePreset
                {
                    Name = preset.Name,
                    Settings = CloneAppearance(preset.Settings),
                    IsBuiltIn = false
                });
            }

            Presets = merged;
            string selectedName = GetSelectedPresetName();
            _suspendPresetSelection = true;
            RefreshPresetSelectors(selectedName);
            _suspendPresetSelection = false;
            SaveSettings();
            System.Windows.MessageBox.Show(
                GetString("Loc.MsgPresetReset"),
                GetString("Loc.MsgInfo"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ApplyPresetAll_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboTop.SelectedItem is AppearancePreset preset)
            {
                UpdateAppearance(preset.Settings);

                foreach (var panel in Application.Current.Windows.OfType<DesktopPanel>())
                {
                    panel.assignedPresetName = preset.Name;
                    panel.ApplyAppearance(preset.Settings);
                }

                // offene und gespeicherte Presetnamen setzen
                foreach (var w in savedWindows)
                {
                    w.PresetName = preset.Name;
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private void RefreshPresetSelectors(string? preferredName = null)
        {
            var ordered = Presets.OrderBy(p => p.IsBuiltIn ? 0 : 1).ThenBy(p => p.Name).ToList();
            if (PresetComboTop != null)
            {
                string? selectName = preferredName;
                if (string.IsNullOrWhiteSpace(selectName) && PresetComboTop.SelectedItem is AppearancePreset current)
                {
                    selectName = current.Name;
                }

                PresetComboTop.ItemsSource = ordered;
                var selected = !string.IsNullOrWhiteSpace(selectName)
                    ? ordered.FirstOrDefault(p => string.Equals(p.Name, selectName, StringComparison.OrdinalIgnoreCase))
                    : null;
                PresetComboTop.SelectedItem = selected
                    ?? ordered.FirstOrDefault(p => p.Name == DefaultPresetName)
                    ?? ordered.FirstOrDefault();
            }

            // Update panel preset dropdowns
            if (PanelOverviewList != null)
            {
                foreach (var itemContainer in PanelOverviewList.Items)
                {
                    if (PanelOverviewList.ItemContainerGenerator.ContainerFromItem(itemContainer) is FrameworkElement fe)
                    {
                        var combo = FindChild<System.Windows.Controls.ComboBox>(fe, "PanelPresetCombo");
                        if (combo != null)
                        {
                            combo.ItemsSource = ordered;
                        }
                    }
                }
            }
        }

        private static T? FindChild<T>(DependencyObject parent, string childName) where T : FrameworkElement
        {
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild && (string.IsNullOrEmpty(childName) || tChild.Name == childName))
                {
                    return tChild;
                }

                var result = FindChild<T>(child, childName);
                if (result != null) return result;
            }
            return null;
        }

        private void RefreshPanelOverview()
        {
            if (PanelOverviewList == null || PanelOverviewCount == null) return;

            var openPanelList = Application.Current.Windows.OfType<DesktopPanel>().ToList();
            var openPanels = openPanelList
                .ToDictionary(p => GetPanelKey(p), p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var panel in openPanelList)
            {
                var kind = ResolvePanelKind(panel);
                if (kind == PanelKind.None &&
                    string.IsNullOrWhiteSpace(panel.currentFolderPath) &&
                    panel.PinnedItems.Count == 0)
                {
                    continue;
                }

                string key = GetPanelKey(panel);
                if (!savedWindows.Any(w => string.Equals(GetPanelKey(w), key, StringComparison.OrdinalIgnoreCase)))
                {
                    savedWindows.Add(BuildWindowDataFromPanel(panel));
                }
            }

            var items = savedWindows.Select(s =>
            {
                string panelKey = GetPanelKey(s);
                openPanels.TryGetValue(panelKey, out var panel);
                var kind = ResolvePanelKind(s);
                string title = !string.IsNullOrWhiteSpace(panel?.Title) ? panel.Title :
                    !string.IsNullOrWhiteSpace(s.PanelTitle) ? s.PanelTitle :
                    (!string.IsNullOrWhiteSpace(s.FolderPath) ? System.IO.Path.GetFileName(s.FolderPath) : GetString("Loc.Untitled"));

                bool isOpen = panel != null;
                bool isHidden = !isOpen && s.IsHidden;
                if (isOpen && s.IsHidden)
                {
                    s.IsHidden = false;
                }

                string state = isOpen
                    ? (panel!.isContentVisible ? GetString("Loc.PanelStateOpen") : GetString("Loc.PanelStateCollapsed"))
                    : (isHidden ? GetString("Loc.PanelStateHidden") : GetString("Loc.PanelStateClosed"));
                string actionLabel = isOpen ? GetString("Loc.PanelActionFocus") : (isHidden ? GetString("Loc.PanelActionReveal") : GetString("Loc.PanelActionShow"));

                string folderLabel = kind == PanelKind.List
                    ? string.Format(GetString("Loc.PanelTypeList"), s.PinnedItems?.Count ?? 0)
                    : string.IsNullOrWhiteSpace(s.FolderPath) ? GetString("Loc.NoFolder") : s.FolderPath;

                return new PanelOverviewItem
                {
                    Title = string.IsNullOrWhiteSpace(title) ? GetString("Loc.Untitled") : title,
                    Folder = folderLabel,
                    FolderPath = s.FolderPath ?? "",
                    PanelKey = panelKey,
                    PanelType = kind,
                    State = state,
                    Size = panel != null ? $"{(int)panel.Width} x {(int)panel.Height}" : $"{(int)s.Width} x {(int)s.Height}",
                    Position = panel != null ? $"{(int)panel.Left}, {(int)panel.Top}" : $"{(int)s.Left}, {(int)s.Top}",
                    IsOpen = isOpen,
                    IsHidden = isHidden,
                    ActionLabel = actionLabel,
                    Panel = panel,
                    PresetName = string.IsNullOrWhiteSpace(s.PresetName) ? DefaultPresetName : s.PresetName
                };
            })
            .ToList();

            var openWithoutType = openPanelList
                .Where(p => ResolvePanelKind(p) == PanelKind.None &&
                            string.IsNullOrWhiteSpace(p.currentFolderPath) &&
                            p.PinnedItems.Count == 0)
                .Select(panel =>
                {
                    string title = !string.IsNullOrWhiteSpace(panel.Title) ? panel.Title : GetString("Loc.Untitled");
                    string preset = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName;
                    return new PanelOverviewItem
                    {
                        Title = title,
                        Folder = GetString("Loc.NoFolder"),
                        FolderPath = "",
                        PanelKey = GetPanelKey(panel),
                        PanelType = PanelKind.None,
                        State = panel.isContentVisible ? GetString("Loc.PanelStateOpen") : GetString("Loc.PanelStateCollapsed"),
                        Size = $"{(int)panel.Width} x {(int)panel.Height}",
                        Position = $"{(int)panel.Left}, {(int)panel.Top}",
                        IsOpen = true,
                        IsHidden = false,
                        ActionLabel = GetString("Loc.PanelActionFocus"),
                        Panel = panel,
                        PresetName = preset
                    };
                })
                .ToList();

            items.AddRange(openWithoutType);
            items = items.OrderBy(p => p.Title).ToList();

            PanelOverviewList.ItemsSource = items;
            int hiddenCount = items.Count(p => p.IsHidden);
            PanelOverviewCount.Text = hiddenCount > 0
                ? string.Format(GetString("Loc.PanelCountHidden"), items.Count, hiddenCount)
                : string.Format(GetString("Loc.PanelCount"), items.Count);
            RefreshPresetSelectors();
        }

        private void RefreshLayoutList()
        {
            if (LayoutOverviewList == null || LayoutOverviewCount == null) return;

            var ordered = Layouts.OrderBy(l => l.Name).ToList();
            var items = ordered.Select(BuildLayoutOverviewItem).ToList();
            LayoutOverviewList.ItemsSource = items;
            LayoutOverviewCount.Text = string.Format(GetString("Loc.LayoutCount"), items.Count);
        }

        private static LayoutOverviewItem BuildLayoutOverviewItem(LayoutDefinition layout)
        {
            int total = layout.Panels?.Count ?? 0;
            int hidden = layout.Panels?.Count(p => p.IsHidden) ?? 0;
            int visible = Math.Max(0, total - hidden);

            string summary = total == 0
                ? GetString("Loc.LayoutSummaryEmpty")
                : string.Format(GetString("Loc.LayoutSummaryVisibleHidden"), visible, hidden);
            if (!string.IsNullOrWhiteSpace(layout.ThemePresetName))
            {
                summary = $"{summary}, {GetString("Loc.LayoutSummaryTheme")}: {layout.ThemePresetName}";
            }

            string name = string.IsNullOrWhiteSpace(layout.Name) ? GetString("Loc.Untitled") : layout.Name;

            return new LayoutOverviewItem
            {
                Name = name,
                Summary = summary,
                Layout = layout
            };
        }

        private void CreateLayoutFromCurrent_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            string defaultName = $"Layout {Layouts.Count + 1}";
            string? name = PromptName(GetString("Loc.PromptLayoutName"), defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            name = EnsureUniqueLayoutName(name);
            var layout = new LayoutDefinition
            {
                Name = name,
                ThemePresetName = GetSelectedPresetName(),
                Appearance = CloneAppearance(Appearance),
                Panels = savedWindows.Select(CloneWindowData).ToList()
            };
            Layouts.Add(layout);
            SaveSettings();
            RefreshLayoutList();
        }

        private void CreateEmptyLayout_Click(object sender, RoutedEventArgs e)
        {
            string defaultName = $"Layout {Layouts.Count + 1}";
            string? name = PromptName(GetString("Loc.PromptLayoutName"), defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            name = EnsureUniqueLayoutName(name);
            var layout = new LayoutDefinition
            {
                Name = name,
                ThemePresetName = GetSelectedPresetName(),
                Appearance = CloneAppearance(Appearance),
                Panels = new List<WindowData>()
            };
            Layouts.Add(layout);
            SaveSettings();
            RefreshLayoutList();
        }

        private void ApplyLayout_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                ApplyLayout(layout);
            }
        }

        private void UpdateLayoutFromCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                SaveSettings();
                layout.Panels = savedWindows.Select(CloneWindowData).ToList();
                layout.ThemePresetName = GetSelectedPresetName();
                layout.Appearance = CloneAppearance(Appearance);
                SaveSettings();
                RefreshLayoutList();
            }
        }

        private void DeleteLayout_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                Layouts.Remove(layout);
                SaveSettings();
                RefreshLayoutList();
            }
        }

        private void ApplyLayout(LayoutDefinition layout)
        {
            if (layout == null) return;

            var layoutPanels = layout.Panels ?? new List<WindowData>();
            foreach (var panel in layoutPanels)
            {
                NormalizeWindowData(panel);
            }
            var layoutDict = layoutPanels.ToDictionary(
                p => GetPanelKey(p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in layoutDict.Values)
            {
                var existing = FindSavedWindow(GetPanelKey(entry));
                if (existing != null)
                {
                    CopyWindowData(entry, existing);
                }
                else
                {
                    savedWindows.Add(CloneWindowData(entry));
                }
            }

            var openPanels = Application.Current.Windows.OfType<DesktopPanel>().ToDictionary(
                p => GetPanelKey(p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

            foreach (var saved in savedWindows.ToList())
            {
                var savedKey = GetPanelKey(saved);
                if (!layoutDict.TryGetValue(savedKey, out var layoutData))
                {
                    saved.IsHidden = true;
                    if (openPanels.TryGetValue(savedKey, out var openPanel))
                    {
                        openPanel.Close();
                    }
                    continue;
                }

                CopyWindowData(layoutData, saved);

                if (layoutData.IsHidden)
                {
                    saved.IsHidden = true;
                    if (openPanels.TryGetValue(savedKey, out var openPanel))
                    {
                        openPanel.Close();
                    }
                    continue;
                }

                saved.IsHidden = false;
                if (openPanels.TryGetValue(savedKey, out var panel))
                {
                    ApplyWindowDataToPanel(panel, saved);
                    panel.Show();
                    panel.WindowState = WindowState.Normal;
                    panel.Activate();
                }
                else
                {
                    var kind = ResolvePanelKind(saved);
                    if (kind == PanelKind.Folder && !Directory.Exists(saved.FolderPath))
                    {
                        continue;
                    }
                    OpenPanelFromData(saved);
                }
            }

            ApplyLayoutAppearance(layout);
            SaveSettings();
            NotifyPanelsChanged();
        }

        private void ApplyLayoutAppearance(LayoutDefinition layout)
        {
            if (!string.IsNullOrWhiteSpace(layout.ThemePresetName))
            {
                var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, layout.ThemePresetName, StringComparison.OrdinalIgnoreCase));
                if (preset != null)
                {
                    UpdateAppearance(CloneAppearance(preset.Settings));
                    if (PresetComboTop != null)
                    {
                        PresetComboTop.SelectedItem = preset;
                    }
                    if (_isUiReady)
                    {
                        PopulateAppearanceInputs(Appearance);
                        UpdatePreview(Appearance);
                    }
                    return;
                }
            }

            if (layout.Appearance != null)
            {
                UpdateAppearance(CloneAppearance(layout.Appearance));
                if (_isUiReady)
                {
                    PopulateAppearanceInputs(Appearance);
                    UpdatePreview(Appearance);
                }
            }
        }

        private string GetSelectedPresetName()
        {
            return (PresetComboTop?.SelectedItem as AppearancePreset)?.Name ?? "";
        }

        private string? PromptName(string message, string defaultName)
        {
            var input = new InputBox(message, defaultName) { Owner = this };
            if (input.ShowDialog() == true)
            {
                string name = (input.ResultText ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            return null;
        }

        private string EnsureUniqueLayoutName(string name)
        {
            if (!Layouts.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }

            int index = 2;
            string candidate = $"{name} {index}";
            while (Layouts.Any(l => string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                index++;
                candidate = $"{name} {index}";
            }
            return candidate;
        }

        private static WindowData CloneWindowData(WindowData source)
        {
            return new WindowData
            {
                PanelId = source.PanelId ?? "",
                PanelType = source.PanelType ?? "",
                FolderPath = source.FolderPath ?? "",
                DefaultFolderPath = source.DefaultFolderPath ?? "",
                Left = source.Left,
                Top = source.Top,
                Width = source.Width,
                Height = source.Height,
                Zoom = source.Zoom,
                IsCollapsed = source.IsCollapsed,
                IsHidden = source.IsHidden,
                CollapsedTop = source.CollapsedTop,
                BaseTop = source.BaseTop,
                PanelTitle = source.PanelTitle ?? "",
                PresetName = source.PresetName ?? "",
                ShowHidden = source.ShowHidden,
                ExpandOnHover = source.ExpandOnHover,
                OpenFoldersExternally = source.OpenFoldersExternally,
                PinnedItems = source.PinnedItems?.ToList() ?? new List<string>()
            };
        }

        private static void CopyWindowData(WindowData source, WindowData target)
        {
            target.PanelId = source.PanelId ?? "";
            target.PanelType = source.PanelType ?? "";
            target.FolderPath = source.FolderPath ?? "";
            target.DefaultFolderPath = source.DefaultFolderPath ?? "";
            target.Left = source.Left;
            target.Top = source.Top;
            target.Width = source.Width;
            target.Height = source.Height;
            target.Zoom = source.Zoom;
            target.IsCollapsed = source.IsCollapsed;
            target.IsHidden = source.IsHidden;
            target.CollapsedTop = source.CollapsedTop;
            target.BaseTop = source.BaseTop;
            target.PanelTitle = source.PanelTitle ?? "";
            target.PresetName = source.PresetName ?? "";
            target.ShowHidden = source.ShowHidden;
            target.ExpandOnHover = source.ExpandOnHover;
            target.OpenFoldersExternally = source.OpenFoldersExternally;
            target.PinnedItems = source.PinnedItems?.ToList() ?? new List<string>();
        }

        private static AppearanceSettings CloneAppearance(AppearanceSettings source)
        {
            return new AppearanceSettings
            {
                BackgroundColor = source.BackgroundColor,
                BackgroundOpacity = source.BackgroundOpacity,
                HeaderColor = source.HeaderColor,
                AccentColor = source.AccentColor,
                TextColor = source.TextColor,
                MutedTextColor = source.MutedTextColor,
                FolderTextColor = source.FolderTextColor,
                FontFamily = source.FontFamily,
                TitleFontSize = source.TitleFontSize,
                ItemFontSize = source.ItemFontSize,
                CornerRadius = source.CornerRadius,
                ShadowOpacity = source.ShadowOpacity,
                ShadowBlur = source.ShadowBlur,
                BackgroundMode = source.BackgroundMode,
                BackgroundImagePath = source.BackgroundImagePath,
                BackgroundImageOpacity = source.BackgroundImageOpacity,
                GlassEnabled = source.GlassEnabled,
                ImageStretchFill = source.ImageStretchFill,
                Pattern = source.Pattern
            };
        }

        private DesktopPanel OpenPanelFromData(WindowData data)
        {
            var panel = new DesktopPanel();
            ApplyWindowDataToPanel(panel, data);
            panel.Show();
            return panel;
        }

        private void ApplyWindowDataToPanel(DesktopPanel panel, WindowData data)
        {
            ApplyPanelContent(panel, data);
            if (!string.IsNullOrWhiteSpace(data.PanelTitle))
            {
                panel.Title = data.PanelTitle;
                panel.PanelTitle.Text = data.PanelTitle;
            }

            panel.assignedPresetName = string.IsNullOrWhiteSpace(data.PresetName) ? DefaultPresetName : data.PresetName;
            panel.showHiddenItems = data.ShowHidden;
            panel.expandOnHover = data.ExpandOnHover;
            panel.openFoldersExternally = data.OpenFoldersExternally;
            panel.defaultFolderPath = data.DefaultFolderPath;

            panel.baseTopPosition = data.BaseTop > 0 ? data.BaseTop : data.Top;
            panel.Left = data.Left;
            panel.Width = data.Width;
            panel.Height = data.Height;
            panel.expandedHeight = data.Height;
            panel.SetZoom(data.Zoom);
            var assigned = GetPresetSettings(data.PresetName);
            panel.ApplyAppearance(assigned);
            panel.ForceCollapseState(data.IsCollapsed);
        }

        private void ApplyPanelContent(DesktopPanel panel, WindowData data)
        {
            NormalizeWindowData(data);
            panel.PanelId = data.PanelId;
            var kind = ResolvePanelKind(data);
            panel.PanelType = kind;

            if (kind == PanelKind.Folder)
            {
                if (!string.IsNullOrWhiteSpace(data.FolderPath))
                {
                    panel.LoadFolder(data.FolderPath, false);
                }
            }
            else if (kind == PanelKind.List)
            {
                panel.LoadList(data.PinnedItems, false);
            }
            else
            {
                panel.ClearPanelItems();
            }
        }

        public static void MarkPanelHidden(DesktopPanel panel)
        {
            if (panel == null) return;
            var snapshot = BuildWindowDataFromPanel(panel);
            snapshot.IsHidden = true;
            var panelKey = GetPanelKey(snapshot);

            var existing = FindSavedWindow(panelKey);
            if (existing != null)
            {
                CopyWindowData(snapshot, existing);
                existing.IsHidden = true;
                return;
            }

            savedWindows.Add(snapshot);
        }

        public static void MarkPanelHidden(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            var panelKey = $"folder:{folderPath}";
            var existing = FindSavedWindow(panelKey);
            if (existing != null)
            {
                existing.IsHidden = true;
                return;
            }

            savedWindows.Add(new WindowData
            {
                PanelId = panelKey,
                PanelType = PanelKind.Folder.ToString(),
                FolderPath = folderPath,
                IsHidden = true
            });
        }

        private void FocusPanel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (!string.IsNullOrWhiteSpace(item.FolderPath) ? $"folder:{item.FolderPath}" : item.Panel?.PanelId ?? "");
                var existing = FindSavedWindow(panelKey);
                if (item.Panel != null)
                {
                    item.Panel.Show();
                    item.Panel.WindowState = WindowState.Normal;
                    item.Panel.Activate();
                    if (existing != null && existing.IsHidden)
                    {
                        existing.IsHidden = false;
                        SaveSettings();
                    }
                }
                else
                {
                    if (existing != null)
                    {
                        existing.IsHidden = false;
                        OpenPanelFromData(existing);
                        SaveSettings();
                        NotifyPanelsChanged();
                        return;
                    }

                    if (item.PanelType == PanelKind.Folder && Directory.Exists(item.FolderPath))
                    {
                        var panel = new DesktopPanel();
                        panel.LoadFolder(item.FolderPath);
                        panel.Show();
                        SaveSettings();
                        NotifyPanelsChanged();
                    }
                }
            }
        }

        private void PanelTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;

            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                string defaultName = string.IsNullOrWhiteSpace(item.Title) ? "Unbenannt" : item.Title;
                string? name = PromptName(GetString("Loc.PromptPanelName"), defaultName);
                if (string.IsNullOrWhiteSpace(name)) return;

                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (!string.IsNullOrWhiteSpace(item.FolderPath) ? $"folder:{item.FolderPath}" : item.Panel?.PanelId ?? "");
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    existing.PanelTitle = name;
                }
                else if (item.Panel != null)
                {
                    var snapshot = BuildWindowDataFromPanel(item.Panel);
                    snapshot.PanelTitle = name;
                    snapshot.IsHidden = item.IsHidden;
                    savedWindows.Add(snapshot);
                }
                else if (!string.IsNullOrWhiteSpace(item.FolderPath))
                {
                    savedWindows.Add(new WindowData
                    {
                        PanelId = panelKey,
                        PanelType = PanelKind.Folder.ToString(),
                        FolderPath = item.FolderPath,
                        PanelTitle = name,
                        IsHidden = item.IsHidden,
                        PresetName = string.IsNullOrWhiteSpace(item.PresetName) ? DefaultPresetName : item.PresetName
                    });
                }

                if (item.Panel != null)
                {
                    item.Panel.Title = name;
                    item.Panel.PanelTitle.Text = name;
                }

                SaveSettings();
                NotifyPanelsChanged();
                e.Handled = true;
            }
        }

        private void HidePanelFromList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                if (item.Panel != null)
                {
                    item.Panel.Close();
                }
                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (!string.IsNullOrWhiteSpace(item.FolderPath) ? $"folder:{item.FolderPath}" : item.Panel?.PanelId ?? "");
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    existing.IsHidden = true;
                }
                else if (item.Panel != null)
                {
                    var snapshot = BuildWindowDataFromPanel(item.Panel);
                    snapshot.IsHidden = true;
                    savedWindows.Add(snapshot);
                }
                else if (!string.IsNullOrWhiteSpace(item.FolderPath))
                {
                    savedWindows.Add(new WindowData
                    {
                        PanelId = panelKey,
                        PanelType = PanelKind.Folder.ToString(),
                        FolderPath = item.FolderPath,
                        IsHidden = true,
                        PresetName = string.IsNullOrWhiteSpace(item.PresetName) ? DefaultPresetName : item.PresetName
                    });
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private void PanelPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox cb && cb.Tag is PanelOverviewItem item && cb.SelectedItem is AppearancePreset preset)
            {
                item.PresetName = preset.Name;

                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (!string.IsNullOrWhiteSpace(item.FolderPath) ? $"folder:{item.FolderPath}" : item.Panel?.PanelId ?? "");
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    existing.PresetName = preset.Name;
                }
                else if (item.Panel != null)
                {
                    var snapshot = BuildWindowDataFromPanel(item.Panel);
                    snapshot.PresetName = preset.Name;
                    snapshot.IsHidden = item.IsHidden;
                    savedWindows.Add(snapshot);
                }
                else if (!string.IsNullOrWhiteSpace(item.FolderPath))
                {
                    savedWindows.Add(new WindowData
                    {
                        PanelId = panelKey,
                        PanelType = PanelKind.Folder.ToString(),
                        FolderPath = item.FolderPath,
                        PresetName = preset.Name,
                        IsHidden = item.IsHidden
                    });
                }

                if (item.Panel != null)
                {
                    item.Panel.assignedPresetName = preset.Name;
                    item.Panel.ApplyAppearance(preset.Settings);
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private AppearanceSettings BuildAppearanceFromUi()
        {
            var current = Appearance ?? new AppearanceSettings();
            string mode = (BackgroundModeCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? current.BackgroundMode ?? "Solid";
            string pattern = (PatternCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? current.Pattern ?? "None";
            double imageOpacity = ImageOpacitySlider != null ? Math.Round(ImageOpacitySlider.Value, 2) : current.BackgroundImageOpacity;
            bool glass = GlassToggle?.IsChecked == true;
            bool fit = ImageFitToggle?.IsChecked != false;
            string imagePath = (ImagePathInput?.Text ?? current.BackgroundImagePath ?? "").Trim();
            string fontFamily = (FontFamilyInput?.Text ?? current.FontFamily ?? "").Trim();
            double titleSize = SanitizeDouble(TitleFontSizeInput?.Text, current.TitleFontSize, 10, 28);
            double itemSize = SanitizeDouble(ItemFontSizeInput?.Text, current.ItemFontSize, 9, 24);
            string textColor = SanitizeColor(TextColorInput?.Text ?? "", current.TextColor);
            string folderColorFallback = string.IsNullOrWhiteSpace(current.FolderTextColor)
                ? current.AccentColor
                : current.FolderTextColor;
            string folderColor = SanitizeColor(FolderColorInput?.Text ?? "", folderColorFallback);

            return new AppearanceSettings
            {
                BackgroundColor = SanitizeColor(BackgroundColorInput.Text, current.BackgroundColor),
                HeaderColor = SanitizeColor(HeaderColorInput.Text, current.HeaderColor),
                AccentColor = SanitizeColor(AccentColorInput.Text, current.AccentColor),
                TextColor = textColor,
                MutedTextColor = current.MutedTextColor,
                FolderTextColor = folderColor,
                FontFamily = string.IsNullOrWhiteSpace(fontFamily) ? current.FontFamily : fontFamily,
                TitleFontSize = Math.Round(titleSize, 0),
                ItemFontSize = Math.Round(itemSize, 0),
                BackgroundOpacity = Math.Round(OpacitySlider.Value, 2),
                CornerRadius = Math.Round(CornerRadiusSlider.Value, 0),
                ShadowOpacity = Math.Round(ShadowOpacitySlider.Value, 2),
                ShadowBlur = Math.Round(ShadowBlurSlider.Value, 1),
                BackgroundMode = mode,
                BackgroundImagePath = imagePath,
                BackgroundImageOpacity = imageOpacity,
                GlassEnabled = glass,
                ImageStretchFill = fit,
                Pattern = pattern
            };
        }

        private string SanitizeColor(string input, string fallback)
        {
            string value = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            try
            {
                System.Windows.Media.ColorConverter.ConvertFromString(value);
                return value;
            }
            catch
            {
                return fallback;
            }
        }

        private double SanitizeDouble(string? input, double fallback, double min, double max)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;

            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
                !double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return fallback;
            }

            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;

            return Math.Max(min, Math.Min(max, value));
        }

        private void UpdatePreview(AppearanceSettings appearance)
        {
            if (appearance == null) return;

            var backgroundBrush = BuildBackgroundBrush(appearance, true);
            var headerBrush = BuildBrush(appearance.HeaderColor, 1.0, MediaColor.FromRgb(34, 37, 42));
            var accentBrush = BuildBrush(appearance.AccentColor, 1.0, MediaColor.FromRgb(90, 200, 250));
            var textBrush = BuildBrush(appearance.TextColor, 1.0, MediaColor.FromRgb(242, 245, 250));
            var mutedBrush = BuildBrush(appearance.MutedTextColor, 1.0, MediaColor.FromRgb(167, 176, 192));
            string folderColor = string.IsNullOrWhiteSpace(appearance.FolderTextColor)
                ? appearance.AccentColor
                : appearance.FolderTextColor;
            var folderBrush = BuildBrush(folderColor, 1.0, MediaColor.FromRgb(110, 139, 255));

            PreviewPanel.Background = backgroundBrush;
            PreviewPanel.CornerRadius = new CornerRadius(appearance.CornerRadius);
            PreviewHeader.Background = headerBrush;
            double innerRadius = Math.Max(4, appearance.CornerRadius - 4);
            PreviewHeader.CornerRadius = new CornerRadius(innerRadius);
            if (PreviewContent != null)
            {
                PreviewContent.CornerRadius = new CornerRadius(innerRadius);
            }
            PreviewTitleText.Foreground = accentBrush;

            if (PreviewPanel.Resources != null)
            {
                PreviewPanel.Resources["PreviewTextBrush"] = textBrush;
                PreviewPanel.Resources["PreviewMutedBrush"] = mutedBrush;
                PreviewPanel.Resources["PreviewFolderBrush"] = folderBrush;
                PreviewPanel.Resources["PreviewTitleFontSize"] = appearance.TitleFontSize;
                PreviewPanel.Resources["PreviewItemFontSize"] = appearance.ItemFontSize;
            }

            if (!string.IsNullOrWhiteSpace(appearance.FontFamily))
            {
                try
                {
                    PreviewPanel.SetValue(TextElement.FontFamilyProperty, new FontFamily(appearance.FontFamily));
                }
                catch
                {
                    PreviewPanel.SetValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
                }
            }

            if (PreviewShadow != null)
            {
                PreviewShadow.BlurRadius = Math.Max(0, appearance.ShadowBlur);
                PreviewShadow.Opacity = Math.Max(0, Math.Min(1, appearance.ShadowOpacity));
            }

            BackgroundSwatch.Background = BuildBrush(appearance.BackgroundColor, 1.0, MediaColor.FromRgb(30, 30, 30));
            HeaderSwatch.Background = headerBrush;
            AccentSwatch.Background = accentBrush;
            if (TextColorSwatch != null) TextColorSwatch.Background = textBrush;
            if (FolderColorSwatch != null) FolderColorSwatch.Background = folderBrush;
        }

        private DesktopPanel CreatePanelWithPreset(string presetName)
        {
            var panel = new DesktopPanel();
            var appearance = GetPresetSettings(presetName);
            panel.ApplyAppearance(appearance);
            panel.assignedPresetName = string.IsNullOrWhiteSpace(presetName) ? DefaultPresetName : presetName;
            return panel;
        }

        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WinForms.OpenFileDialog
            {
                Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Alle Dateien|*.*"
            };
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                if (ImagePathInput != null)
                {
                    ImagePathInput.Text = dialog.FileName;
                }
                var appearance = BuildAppearanceFromUi();
                UpdatePreview(appearance);
                UpdateAppearance(appearance);
            }
        }

        public static System.Windows.Media.Brush BuildBackgroundBrush(AppearanceSettings appearance, bool allowGlass)
        {
            if (appearance == null) return new SolidColorBrush(MediaColor.FromRgb(30, 30, 30));

            var baseColorBrush = BuildBrush(appearance.BackgroundColor, appearance.BackgroundOpacity, MediaColor.FromRgb(30, 30, 30));

            if (string.Equals(appearance.BackgroundMode, "Image", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(appearance.BackgroundImagePath) &&
                File.Exists(appearance.BackgroundImagePath))
            {
                var img = new ImageBrush(new BitmapImage(new Uri(appearance.BackgroundImagePath, UriKind.Absolute)))
                {
                    Stretch = appearance.ImageStretchFill ? Stretch.UniformToFill : Stretch.Uniform,
                    Opacity = Math.Max(0.05, Math.Min(1, appearance.BackgroundImageOpacity))
                };
                return img;
            }

            if (string.Equals(appearance.BackgroundMode, "Pattern", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(appearance.Pattern, "None", StringComparison.OrdinalIgnoreCase))
            {
                return BuildPatternBrush(appearance);
            }

            if (allowGlass && appearance.GlassEnabled)
            {
                var glass = baseColorBrush.Clone();
                glass.Opacity = Math.Max(0.1, Math.Min(1.0, appearance.BackgroundOpacity));
                return glass;
            }

            return baseColorBrush;
        }

        private static System.Windows.Media.Brush BuildPatternBrush(AppearanceSettings appearance)
        {
            var baseColor = BuildBrush(appearance.BackgroundColor, appearance.BackgroundOpacity, MediaColor.FromRgb(30, 30, 30)).Color;
            var accent = BuildBrush(appearance.AccentColor, 0.25, MediaColor.FromRgb(90, 200, 250)).Color;

            DrawingGroup group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));

            switch (appearance.Pattern?.ToLowerInvariant())
            {
                case "diagonal":
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 1), new LineGeometry(new System.Windows.Point(0, 8), new System.Windows.Point(8, 0))));
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 1), new LineGeometry(new System.Windows.Point(-4, 8), new System.Windows.Point(4, 0))));
                    break;
                case "grid":
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 0.8), new RectangleGeometry(new Rect(0, 0, 8, 8))));
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 0.8), new RectangleGeometry(new Rect(0, 0, 4, 4))));
                    break;
                case "dots":
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new EllipseGeometry(new System.Windows.Point(2, 2), 1, 1)));
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new EllipseGeometry(new System.Windows.Point(6, 6), 1, 1)));
                    break;
                default:
                    break;
            }

            return new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 8, 8),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
        }

        public static SolidColorBrush BuildBrush(string value, double opacity, MediaColor fallback)
        {
            byte alpha = (byte)Math.Max(0, Math.Min(255, opacity * 255));

            try
            {
                var color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value);
                return new SolidColorBrush(MediaColor.FromArgb(alpha, color.R, color.G, color.B));
            }
            catch
            {
                return new SolidColorBrush(MediaColor.FromArgb(alpha, fallback.R, fallback.G, fallback.B));
            }
        }
    }
}
