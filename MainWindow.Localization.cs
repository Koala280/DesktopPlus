using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private static readonly Dictionary<string, Dictionary<string, string>> LocalizationData =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["de"] = new Dictionary<string, string>
                {
                    ["Loc.TabGeneral"] = "Allgemein",
                    ["Loc.TabPanels"] = "Panels",
                    ["Loc.TabLayouts"] = "Layouts",
                    ["Loc.TabThemes"] = "Themen",
                    ["Loc.TabAutoSort"] = "Auto-Sort",
                    ["Loc.TabShortcuts"] = "Shortcuts",
                    ["Loc.GeneralTitle"] = "Allgemein",
                    ["Loc.GeneralLanguageLabel"] = "Sprache",
                    ["Loc.LanguageGerman"] = "Deutsch",
                    ["Loc.LanguageEnglish"] = "Englisch",
                    ["Loc.LanguageLatvian"] = "Lettisch",
                    ["Loc.GeneralStartupCheckbox"] = "Mit Windows starten",
                    ["Loc.GeneralCloseLabel"] = "Beim Schließen",
                    ["Loc.GeneralUpdatesLabel"] = "Updates",
                    ["Loc.GeneralAutoUpdateCheckbox"] = "Updates automatisch installieren",
                    ["Loc.GeneralCheckUpdatesButton"] = "Jetzt nach Updates suchen",
                    ["Loc.GeneralImportLanguageButton"] = "Sprache aus JSON importieren",
                    ["Loc.GeneralCurrentVersion"] = "Installierte Version: {0}",
                    ["Loc.CloseBehaviorMinimize"] = "Minimieren",
                    ["Loc.CloseBehaviorExit"] = "Programm beenden",
                    ["Loc.PanelsTitle"] = "Panels",
                    ["Loc.PanelsNew"] = "Neues Panel",
                    ["Loc.PanelsDesc"] = "Übersicht pro Panel.",
                    ["Loc.PanelsHide"] = "Ausblenden",
                    ["Loc.PanelsShow"] = "Einblenden",
                    ["Loc.PanelsShowAll"] = "Alle zeigen",
                    ["Loc.PanelsHideAll"] = "Alle ausblenden",
                    ["Loc.PanelsDelete"] = "Löschen",
                    ["Loc.PanelsSettings"] = "Einstellungen",
                    ["Loc.ContextRevealInExplorer"] = "Im Explorer anzeigen",
                    ["Loc.ContextRemoveFromPanel"] = "Aus Panel entfernen",
                    ["Loc.ContextRename"] = "Umbenennen",
                    ["Loc.ContextCut"] = "Ausschneiden",
                    ["Loc.ContextCopy"] = "Kopieren",
                    ["Loc.ContextPaste"] = "Einfügen",
                    ["Loc.ContextMoreOptions"] = "Weitere Optionen anzeigen",
                    ["Loc.PanelDropHint"] = "Ordner hierher ziehen um Standardordner zu setzen\noder Dateien ablegen",
                    ["Loc.PanelDropHintShort"] = "Dateien oder Ordner hierher ziehen",
                    ["Loc.LayoutsTitle"] = "Layouts",
                    ["Loc.LayoutsCreateFromCurrent"] = "Layout hinzufügen",
                    ["Loc.LayoutsCreateEmpty"] = "Leeres Layout",
                    ["Loc.LayoutsDesc"] = "Layouts speichern sichtbare Panels, Positionen und Theme.",
                    ["Loc.LayoutsApply"] = "Laden",
                    ["Loc.LayoutsUpdate"] = "Aktuelles Layout speichern",
                    ["Loc.LayoutsDelete"] = "Löschen",
                    ["Loc.LayoutsDefaultPreset"] = "Standardpreset",
                    ["Loc.LayoutsGlobalPanelSettings"] = "Globale Panel-Settings",
                    ["Loc.ThemesTitle"] = "Preset-Auswahl",
                    ["Loc.ThemesDesc"] = "Wähle ein Preset oder erstelle ein eigenes.",
                    ["Loc.ThemesPresetTooltip"] = "Neues Preset",
                    ["Loc.ThemesCreate"] = "Erstellen",
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
                    ["Loc.LabelTabActiveColor"] = "Tabfarbe aktiv",
                    ["Loc.LabelTabInactiveColor"] = "Tabfarbe inaktiv",
                    ["Loc.LabelTabHoverColor"] = "Tabfarbe Hover",
                    ["Loc.LabelTitleSize"] = "Titelgröße",
                    ["Loc.LabelItemSize"] = "Textgröße",
                    ["Loc.LabelCornerRadius"] = "Eckenradius",
                    ["Loc.LabelShadow"] = "Schatten",
                    ["Loc.LabelHeaderShadow"] = "Header",
                    ["Loc.LabelBodyShadow"] = "Body",
                    ["Loc.LabelMode"] = "Modus",
                    ["Loc.ModeSolid"] = "Farbe",
                    ["Loc.ModeImage"] = "Bild",
                    ["Loc.ModePattern"] = "Muster",
                    ["Loc.LabelPattern"] = "Muster",
                    ["Loc.PatternNone"] = "Keine",
                    ["Loc.PatternDiagonal"] = "Diagonal",
                    ["Loc.PatternGrid"] = "Karos",
                    ["Loc.PatternDots"] = "Punkte",
                    ["Loc.PatternCustom"] = "Custom",
                    ["Loc.LabelPatternColor"] = "Pattern-Farbe",
                    ["Loc.LabelPatternOpacity"] = "Pattern-Deckkraft",
                    ["Loc.LabelPatternTileSize"] = "Kachelgröße",
                    ["Loc.LabelPatternStroke"] = "Strichstärke",
                    ["Loc.PatternEditorHint"] = "Links zeichnen, rechts löschen",
                    ["Loc.ButtonPatternClear"] = "Leeren",
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
                    ["Loc.ShortcutsDesc"] = "Übersicht der aktuell verfügbaren Tastatur- und Maus-Shortcuts.",
                    ["Loc.ShortcutsGlobalTitle"] = "Globale Hotkeys",
                    ["Loc.ShortcutsGlobalHint"] = "In Feld klicken und Tastenkombination drücken. Danach mit Anwenden speichern.",
                    ["Loc.ShortcutsExistingTitle"] = "Panel-Shortcuts",
                    ["Loc.ShortcutsInputHide"] = "Panels ein-/ausblenden",
                    ["Loc.ShortcutsInputForeground"] = "Panels in Vordergrund",
                    ["Loc.ShortcutsApply"] = "Anwenden",
                    ["Loc.ShortcutsReset"] = "Standard",
                    ["Loc.ShortcutHideAllPanels"] = "Alle Panels ein-/ausblenden (umschalten).",
                    ["Loc.ShortcutTemporaryForeground"] = "Alle offenen Panels für kurze Zeit vor alle Apps holen.",
                    ["Loc.ShortcutDeleteSelection"] = "Auswahl löschen (Dateien in Papierkorb oder aus Liste entfernen).",
                    ["Loc.ShortcutCopySelection"] = "Auswahl in die Zwischenablage kopieren.",
                    ["Loc.ShortcutCutSelection"] = "Auswahl ausschneiden.",
                    ["Loc.ShortcutPasteSelection"] = "Dateien aus der Zwischenablage einfügen.",
                    ["Loc.ShortcutPanelZoom"] = "Inhalt im Panel vergrößern/verkleinern.",
                    ["Loc.ShortcutAdditiveSelection"] = "Mehrfachauswahl umschalten (additiv).",
                    ["Loc.ShortcutRangeSelection"] = "Bereichsauswahl in der Dateiliste.",
                    ["Loc.ShortcutClassicContextMenu"] = "Klassisches Windows-Kontextmenü im Panel.",
                    ["Loc.ShortcutDragCopy"] = "Beim Drag&Drop Kopieren erzwingen.",
                    ["Loc.ShortcutDragMove"] = "Beim Drag&Drop Verschieben erzwingen.",
                    ["Loc.ShortcutDragLink"] = "Beim Drag&Drop Verknüpfung erstellen.",
                    ["Loc.ShortcutTrayClose"] = "Tray-Menü schließen.",
                    ["Loc.ShortcutFitContent"] = "Panelgröße auf Inhalt anpassen.",
                    ["Loc.MsgShortcutInvalid"] = "Ungültiger Shortcut für \"{0}\". Bitte Kombination mit Ctrl, Alt, Shift oder Win verwenden.",
                    ["Loc.MsgShortcutDuplicate"] = "Beide globalen Shortcuts dürfen nicht identisch sein.",
                    ["Loc.MsgShortcutRegisterFailed"] = "Shortcut \"{0}\" konnte nicht registriert werden. Eventuell wird er bereits von einer anderen App verwendet.",
                    ["Loc.AutoSortTitle"] = "Desktop automatisch aufräumen",
                    ["Loc.AutoSortDesc"] = "Sortiert Desktop-Dateien und Ordner nach Regeln in eigene Ziel-Panels.",
                    ["Loc.AutoSortToggle"] = "Neue Desktop-Elemente immer automatisch einsortieren",
                    ["Loc.AutoSortRunNow"] = "Jetzt sortieren",
                    ["Loc.AutoSortResetRules"] = "Regeln zurücksetzen",
                    ["Loc.AutoSortBuiltInTitle"] = "Standardregeln",
                    ["Loc.AutoSortCustomTitle"] = "Eigene Dateiendungen",
                    ["Loc.AutoSortCustomHint"] = "Beispiel Endungen: .blend .psd .sketch",
                    ["Loc.AutoSortRuleType"] = "Typ",
                    ["Loc.AutoSortRuleExtensions"] = "Dateiendungen",
                    ["Loc.AutoSortRuleTarget"] = "Ziel-Panel",
                    ["Loc.AutoSortCustomExtensionsLabel"] = "Neue Endungen",
                    ["Loc.AutoSortCustomTargetLabel"] = "Ziel-Panel",
                    ["Loc.AutoSortCustomAdd"] = "Regel hinzufügen",
                    ["Loc.AutoSortRemove"] = "Entfernen",
                    ["Loc.AutoSortExtensionsFolders"] = "(Ordner)",
                    ["Loc.AutoSortExtensionsCatchAll"] = "(Alle übrigen Dateien)",
                    ["Loc.AutoSortRuleFolders"] = "Ordner",
                    ["Loc.AutoSortRuleImages"] = "Bilder",
                    ["Loc.AutoSortRuleVideos"] = "Videos",
                    ["Loc.AutoSortRuleAudio"] = "Audio",
                    ["Loc.AutoSortRuleDocuments"] = "Dokumente",
                    ["Loc.AutoSortRuleArchives"] = "Archive",
                    ["Loc.AutoSortRuleInstallers"] = "Installer & Skripte",
                    ["Loc.AutoSortRuleShortcuts"] = "Verknüpfungen",
                    ["Loc.AutoSortRuleCode"] = "Code-Dateien",
                    ["Loc.AutoSortRuleOthers"] = "Sonstiges",
                    ["Loc.AutoSortPanelFolders"] = "Ordner",
                    ["Loc.AutoSortPanelImages"] = "Bilder",
                    ["Loc.AutoSortPanelVideos"] = "Videos",
                    ["Loc.AutoSortPanelAudio"] = "Audio",
                    ["Loc.AutoSortPanelDocuments"] = "Dokumente",
                    ["Loc.AutoSortPanelArchives"] = "Archive",
                    ["Loc.AutoSortPanelInstallers"] = "Installer",
                    ["Loc.AutoSortPanelShortcuts"] = "Verknüpfungen",
                    ["Loc.AutoSortPanelCode"] = "Code",
                    ["Loc.AutoSortPanelOthers"] = "Sonstiges",
                    ["Loc.AutoSortStatusDisabled"] = "Auto-Sort ist deaktiviert.",
                    ["Loc.AutoSortStatusEnabled"] = "Auto-Sort ist aktiv und beobachtet den Desktop.",
                    ["Loc.AutoSortCustomRuleNameFormat"] = "Custom ({0})",
                    ["Loc.AutoSortMsgInvalidExtensions"] = "Bitte mindestens eine gültige Dateiendung angeben (z.B. .psd .blend).",
                    ["Loc.AutoSortMsgRuleAdded"] = "Neue Regel wurde hinzugefügt.",
                    ["Loc.AutoSortMsgRuleRemoved"] = "Regel wurde entfernt.",
                    ["Loc.AutoSortMsgSortDone"] = "{0} Desktop-Element(e) wurden einsortiert.",
                    ["Loc.AutoSortMsgSortDoneWithErrors"] = "{0} Element(e) einsortiert, {1} konnten nicht verschoben werden.",
                    ["Loc.AutoSortMsgSortNoItems"] = "Keine neuen Desktop-Elemente zum Einsortieren gefunden.",
                    ["Loc.AutoSortMsgSortFailed"] = "Automatisches Sortieren fehlgeschlagen:\n{0}",
                    ["Loc.PanelStateOpen"] = "Offen",
                    ["Loc.PanelStateCollapsed"] = "Eingeklappt",
                    ["Loc.PanelStateHidden"] = "Ausgeblendet",
                    ["Loc.PanelStateClosed"] = "Geschlossen",
                    ["Loc.PanelTypeList"] = "Liste ({0})",
                    ["Loc.PanelTypeFolder"] = "Ordner",
                    ["Loc.PanelTypeFile"] = "Datei",
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
                    ["Loc.PanelSettingsOpenBehavior"] = "Öffnen per",
                    ["Loc.PanelSettingsOpenByDoubleClick"] = "Doppelklick (Standard)",
                    ["Loc.PanelSettingsOpenBySingleClick"] = "Einfachklick",
                    ["Loc.PanelSettingsExpandOnHover"] = "Beim Hover aufklappen",
                    ["Loc.PanelSettingsShowHidden"] = "Versteckte Elemente anzeigen",
                    ["Loc.PanelSettingsShowParentFolder"] = "Überordner anzeigen",
                    ["Loc.PanelSettingsShowFileExtensions"] = "Dateiendungen anzeigen",
                    ["Loc.PanelSettingsShowSettingsButton"] = "Einstellungsbutton anzeigen",
                    ["Loc.PanelSettingsActions"] = "Aktionen",
                    ["Loc.PanelSettingsViewMode"] = "Ansicht",
                    ["Loc.PanelSettingsViewIcons"] = "Kachelansicht",
                    ["Loc.PanelSettingsViewDetails"] = "Listenansicht",
                    ["Loc.PanelSettingsViewPhotos"] = "Fotoalbum",
                    ["Loc.PanelSettingsMetadata"] = "Metadaten",
                    ["Loc.PanelSettingsMetadataDragHint"] = "Reihenfolge per Drag and Drop ändern.",
                    ["Loc.PanelSettingsMetaType"] = "Dateityp",
                    ["Loc.PanelSettingsMetaSize"] = "Dateigröße",
                    ["Loc.PanelSettingsMetaCreated"] = "Erstellt am",
                    ["Loc.PanelSettingsMetaModified"] = "Geändert am",
                    ["Loc.PanelSettingsMetaDimensions"] = "Bildgröße",
                    ["Loc.PanelSettingsMovement"] = "Bewegung",
                    ["Loc.PanelSettingsSearchVisibility"] = "Suche",
                    ["Loc.PanelSettingsSearchAlways"] = "Immer anzeigen",
                    ["Loc.PanelSettingsSearchExpandedOnly"] = "Nur ausgeklappt",
                    ["Loc.PanelSettingsSearchHidden"] = "Ausblenden",
                    ["Loc.PanelSettingsGlobalHint"] = "Gilt als Standard im Layout. Bereits abweichende Panel-Werte bleiben unverändert.",
                    ["Loc.MovementTitlebar"] = "Titelleiste ziehen",
                    ["Loc.MovementButton"] = "Move-Button",
                    ["Loc.MovementLocked"] = "Gesperrt",
                    ["Loc.PanelSettingsFitContent"] = "An Inhalt anpassen",
                    ["Loc.PanelSettingsCancel"] = "Abbrechen",
                    ["Loc.PanelSettingsResetToStandard"] = "Auf Standard zurücksetzen",
                    ["Loc.PanelSettingsSave"] = "Speichern",
                    ["Loc.TabNewTab"] = "Neuer Tab",
                    ["Loc.TabClose"] = "Tab schließen",
                    ["Loc.TabCloseOthers"] = "Andere Tabs schließen",
                    ["Loc.TabRename"] = "Tab umbenennen",
                    ["Loc.TabDropPreview"] = "+ Neuer Tab",
                    ["Loc.InputDialogTitle"] = "Eingabe",
                    ["Loc.InputDialogOk"] = "OK",
                    ["Loc.InputDialogCancel"] = "Abbrechen",
                    ["Loc.TrayOpen"] = "Öffnen",
                    ["Loc.TrayExit"] = "Beenden",
                    ["Loc.TrayStatusRunning"] = "Im Hintergrund aktiv",
                    ["Loc.TrayHintEsc"] = "Esc schließt dieses Menü",
                    ["Loc.MsgError"] = "Fehler",
                    ["Loc.MsgInfo"] = "Hinweis",
                    ["Loc.MsgPresetNameRequired"] = "Bitte einen Preset-Namen eingeben.",
                    ["Loc.MsgPresetBuiltIn"] = "Ein eingebautes Preset kann nicht überschrieben werden. Wähle einen anderen Namen.",
                    ["Loc.MsgPresetBuiltInDelete"] = "Eingebaute Presets können nicht gelöscht werden.",
                    ["Loc.MsgPresetNameExists"] = "Ein Preset mit diesem Namen existiert bereits.",
                    ["Loc.MsgPresetReset"] = "Standard-Presets wurden zurückgesetzt.",
                    ["Loc.MsgResetConfirmTitle"] = "Zurücksetzen bestätigen",
                    ["Loc.MsgResetConfirmMessage"] = "Möchten Sie wirklich alle Standard-Presets zurücksetzen?\n\nDiese Aktion kann nicht rückgängig gemacht werden.",
                    ["Loc.ThemesResetDefaultsFull"] = "Standard-Presets zurücksetzen",
                    ["Loc.MsgRestoreError"] = "Fehler beim Wiederherstellen der Fenster:\n{0}",
                    ["Loc.MsgSaveError"] = "Fehler beim Speichern der Fenster:\n{0}",
                    ["Loc.MsgMoveFolderError"] = "Ordner konnte nicht verschoben werden:\n{0}",
                    ["Loc.MsgMoveFileError"] = "Datei konnte nicht verschoben werden:\n{0}",
                    ["Loc.MsgRenameError"] = "Umbenennen fehlgeschlagen:\n{0}",
                    ["Loc.MsgOpenFolderError"] = "Fehler beim Öffnen des Ordners:\n{0}",
                    ["Loc.MsgOpenFileError"] = "Fehler beim Öffnen der Datei:\n{0}",
                    ["Loc.MsgDeleteConfirmTitle"] = "Löschen bestätigen",
                    ["Loc.MsgDeleteRecycleSingle"] = "Soll \"{0}\" in den Papierkorb verschoben werden?",
                    ["Loc.MsgDeleteRecycleMulti"] = "Sollen {0} Elemente in den Papierkorb verschoben werden?",
                    ["Loc.MsgDeletePanelOnlySingle"] = "Soll \"{0}\" aus diesem Panel entfernt werden?",
                    ["Loc.MsgDeletePanelOnlyMulti"] = "Sollen {0} Elemente aus diesem Panel entfernt werden?",
                    ["Loc.MsgDeletePathError"] = "Element konnte nicht in den Papierkorb verschoben werden:\n{0}",
                    ["Loc.MsgStartupError"] = "Autostart konnte nicht aktualisiert werden:\n{0}",
                    ["Loc.MsgUpdateAvailable"] = "Version {0} ist verfügbar (installiert: {1}). Release-Seite jetzt öffnen?",
                    ["Loc.MsgUpdateAvailableActions"] = "Version {0} ist verfügbar (installiert: {1}).\n\nJa: Release-Seite öffnen\nNein: Direkt silent installieren\nAbbrechen: Nichts tun",
                    ["Loc.UpdateDialogTitle"] = "Update verfügbar",
                    ["Loc.UpdateDialogInstalledLabel"] = "Installiert",
                    ["Loc.UpdateDialogLatestLabel"] = "Neu verfügbar",
                    ["Loc.UpdateDialogChooseAction"] = "Aktion auswählen",
                    ["Loc.UpdateDialogOpenReleaseButton"] = "Release-Seite öffnen",
                    ["Loc.UpdateDialogInstallNowButton"] = "Jetzt installieren",
                    ["Loc.UpdateDialogLaterButton"] = "Später",
                    ["Loc.MsgUpdateUpToDate"] = "Du bist auf dem neuesten Stand (Version {0}).",
                    ["Loc.MsgUpdateCheckFailed"] = "Update-Prüfung fehlgeschlagen:\n{0}",
                    ["Loc.MsgUpdateOpenPageFailed"] = "Release-Seite konnte nicht geöffnet werden.",
                    ["Loc.MsgUpdateNoInstallerAsset"] = "Für diese Version wurde kein Installer-Asset gefunden.",
                    ["Loc.MsgUpdateDownloadFailedForInstall"] = "Installer konnte nicht heruntergeladen werden. Bitte später erneut versuchen.",
                    ["Loc.MsgUpdateInstallStarting"] = "Silent-Installation für Version {0} wird gestartet.\nDesktopPlus wird jetzt beendet und nach dem Update automatisch neu gestartet.",
                    ["Loc.MsgUpdateInstallStartFailed"] = "Silent-Installer konnte nicht gestartet werden.",
                    ["Loc.UpdateStatusDownloading"] = "Update {0} wird heruntergeladen...",
                    ["Loc.UpdateStatusInstalling"] = "Update {0} wird installiert...",
                    ["Loc.UpdateInstallerReadyTitle"] = "Update bereit",
                    ["Loc.UpdateInstallerReadyBody"] = "Update {0} wurde heruntergeladen. Installation ist beim naechsten Start bereit.",
                    ["Loc.MsgLanguageImportSuccess"] = "Sprache \"{0}\" ({1}) wurde importiert.",
                    ["Loc.MsgLanguageImportInvalid"] = "Die JSON-Datei ist kein gültiges Sprachpaket.",
                    ["Loc.MsgLanguageImportFailed"] = "Sprach-Import fehlgeschlagen:\n{0}",
                    ["Loc.MsgLanguageImportReservedCode"] = "Der Sprachcode \"{0}\" ist für eingebaute Sprachen reserviert.",
                    ["Loc.PromptLayoutName"] = "Layout-Name",
                    ["Loc.PromptPanelName"] = "Panel-Name",
                    ["Loc.PromptPresetName"] = "Preset-Name",
                    ["Loc.ThemesPresetDefaultName"] = "Neues Preset"
                },
                ["en"] = new Dictionary<string, string>
                {
                    ["Loc.TabGeneral"] = "General",
                    ["Loc.TabPanels"] = "Panels",
                    ["Loc.TabLayouts"] = "Layouts",
                    ["Loc.TabThemes"] = "Themes",
                    ["Loc.TabAutoSort"] = "Auto sort",
                    ["Loc.TabShortcuts"] = "Shortcuts",
                    ["Loc.GeneralTitle"] = "General",
                    ["Loc.GeneralLanguageLabel"] = "Language",
                    ["Loc.LanguageGerman"] = "German",
                    ["Loc.LanguageEnglish"] = "English",
                    ["Loc.LanguageLatvian"] = "Latvian",
                    ["Loc.GeneralStartupCheckbox"] = "Start with Windows",
                    ["Loc.GeneralCloseLabel"] = "On close",
                    ["Loc.GeneralUpdatesLabel"] = "Updates",
                    ["Loc.GeneralAutoUpdateCheckbox"] = "Automatically install updates",
                    ["Loc.GeneralCheckUpdatesButton"] = "Check for updates now",
                    ["Loc.GeneralImportLanguageButton"] = "Import language JSON",
                    ["Loc.GeneralCurrentVersion"] = "Installed version: {0}",
                    ["Loc.CloseBehaviorMinimize"] = "Minimize",
                    ["Loc.CloseBehaviorExit"] = "Exit application",
                    ["Loc.PanelsTitle"] = "Panels",
                    ["Loc.PanelsNew"] = "New panel",
                    ["Loc.PanelsDesc"] = "Overview per panel.",
                    ["Loc.PanelsHide"] = "Hide",
                    ["Loc.PanelsShow"] = "Show",
                    ["Loc.PanelsShowAll"] = "Show all",
                    ["Loc.PanelsHideAll"] = "Hide all",
                    ["Loc.PanelsDelete"] = "Delete",
                    ["Loc.PanelsSettings"] = "Settings",
                    ["Loc.ContextRevealInExplorer"] = "Reveal in Explorer",
                    ["Loc.ContextRemoveFromPanel"] = "Remove from panel",
                    ["Loc.ContextRename"] = "Rename",
                    ["Loc.ContextCut"] = "Cut",
                    ["Loc.ContextCopy"] = "Copy",
                    ["Loc.ContextPaste"] = "Paste",
                    ["Loc.ContextMoreOptions"] = "Show more options",
                    ["Loc.PanelDropHint"] = "Drop a folder here to set as default\nor drop individual files",
                    ["Loc.PanelDropHintShort"] = "Drop files or folders here",
                    ["Loc.LayoutsTitle"] = "Layouts",
                    ["Loc.LayoutsCreateFromCurrent"] = "Add layout",
                    ["Loc.LayoutsCreateEmpty"] = "Add empty",
                    ["Loc.LayoutsDesc"] = "Layouts store visible panels, positions, and theme.",
                    ["Loc.LayoutsApply"] = "Load",
                    ["Loc.LayoutsUpdate"] = "Save current layout",
                    ["Loc.LayoutsDelete"] = "Delete",
                    ["Loc.LayoutsDefaultPreset"] = "Standard preset",
                    ["Loc.LayoutsGlobalPanelSettings"] = "Global panel settings",
                    ["Loc.ThemesTitle"] = "Preset selection",
                    ["Loc.ThemesDesc"] = "Choose a preset or create your own.",
                    ["Loc.ThemesPresetTooltip"] = "New preset",
                    ["Loc.ThemesCreate"] = "Create",
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
                    ["Loc.LabelTabActiveColor"] = "Tab color active",
                    ["Loc.LabelTabInactiveColor"] = "Tab color inactive",
                    ["Loc.LabelTabHoverColor"] = "Tab color hover",
                    ["Loc.LabelTitleSize"] = "Title size",
                    ["Loc.LabelItemSize"] = "Item size",
                    ["Loc.LabelCornerRadius"] = "Corner radius",
                    ["Loc.LabelShadow"] = "Shadow",
                    ["Loc.LabelHeaderShadow"] = "Header",
                    ["Loc.LabelBodyShadow"] = "Body",
                    ["Loc.LabelMode"] = "Mode",
                    ["Loc.ModeSolid"] = "Solid",
                    ["Loc.ModeImage"] = "Image",
                    ["Loc.ModePattern"] = "Pattern",
                    ["Loc.LabelPattern"] = "Pattern",
                    ["Loc.PatternNone"] = "None",
                    ["Loc.PatternDiagonal"] = "Diagonal",
                    ["Loc.PatternGrid"] = "Grid",
                    ["Loc.PatternDots"] = "Dots",
                    ["Loc.PatternCustom"] = "Custom",
                    ["Loc.LabelPatternColor"] = "Pattern color",
                    ["Loc.LabelPatternOpacity"] = "Pattern opacity",
                    ["Loc.LabelPatternTileSize"] = "Tile size",
                    ["Loc.LabelPatternStroke"] = "Stroke width",
                    ["Loc.PatternEditorHint"] = "Left draw, right erase",
                    ["Loc.ButtonPatternClear"] = "Clear",
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
                    ["Loc.ShortcutsDesc"] = "Overview of currently available keyboard and mouse shortcuts.",
                    ["Loc.ShortcutsGlobalTitle"] = "Global hotkeys",
                    ["Loc.ShortcutsGlobalHint"] = "Click a field and press a key combination, then save with Apply.",
                    ["Loc.ShortcutsExistingTitle"] = "Panel shortcuts",
                    ["Loc.ShortcutsInputHide"] = "Toggle panels",
                    ["Loc.ShortcutsInputForeground"] = "Bring panels forward",
                    ["Loc.ShortcutsApply"] = "Apply",
                    ["Loc.ShortcutsReset"] = "Default",
                    ["Loc.ShortcutHideAllPanels"] = "Toggle all panels (show/hide).",
                    ["Loc.ShortcutTemporaryForeground"] = "Bring all open panels to the front temporarily.",
                    ["Loc.ShortcutDeleteSelection"] = "Delete selection (recycle files or remove from list).",
                    ["Loc.ShortcutCopySelection"] = "Copy selection to the clipboard.",
                    ["Loc.ShortcutCutSelection"] = "Cut selection.",
                    ["Loc.ShortcutPasteSelection"] = "Paste files from the clipboard.",
                    ["Loc.ShortcutPanelZoom"] = "Zoom panel content in or out.",
                    ["Loc.ShortcutAdditiveSelection"] = "Toggle additive multi-selection.",
                    ["Loc.ShortcutRangeSelection"] = "Range selection in the file list.",
                    ["Loc.ShortcutClassicContextMenu"] = "Open the classic Windows context menu in a panel.",
                    ["Loc.ShortcutDragCopy"] = "Force copy while drag and drop.",
                    ["Loc.ShortcutDragMove"] = "Force move while drag and drop.",
                    ["Loc.ShortcutDragLink"] = "Create link while drag and drop.",
                    ["Loc.ShortcutTrayClose"] = "Close the tray menu.",
                    ["Loc.ShortcutFitContent"] = "Fit panel size to content.",
                    ["Loc.MsgShortcutInvalid"] = "Invalid shortcut for \"{0}\". Use a combination with Ctrl, Alt, Shift, or Win.",
                    ["Loc.MsgShortcutDuplicate"] = "The two global shortcuts cannot be identical.",
                    ["Loc.MsgShortcutRegisterFailed"] = "Shortcut \"{0}\" could not be registered. It may already be used by another app.",
                    ["Loc.AutoSortTitle"] = "Automatic desktop cleanup",
                    ["Loc.AutoSortDesc"] = "Sort desktop files and folders by rules into dedicated target panels.",
                    ["Loc.AutoSortToggle"] = "Always auto-sort new desktop items",
                    ["Loc.AutoSortRunNow"] = "Sort now",
                    ["Loc.AutoSortResetRules"] = "Reset rules",
                    ["Loc.AutoSortBuiltInTitle"] = "Built-in rules",
                    ["Loc.AutoSortCustomTitle"] = "Custom extensions",
                    ["Loc.AutoSortCustomHint"] = "Example extensions: .blend .psd .sketch",
                    ["Loc.AutoSortRuleType"] = "Type",
                    ["Loc.AutoSortRuleExtensions"] = "Extensions",
                    ["Loc.AutoSortRuleTarget"] = "Target panel",
                    ["Loc.AutoSortCustomExtensionsLabel"] = "New extensions",
                    ["Loc.AutoSortCustomTargetLabel"] = "Target panel",
                    ["Loc.AutoSortCustomAdd"] = "Add rule",
                    ["Loc.AutoSortRemove"] = "Remove",
                    ["Loc.AutoSortExtensionsFolders"] = "(Folders)",
                    ["Loc.AutoSortExtensionsCatchAll"] = "(All remaining files)",
                    ["Loc.AutoSortRuleFolders"] = "Folders",
                    ["Loc.AutoSortRuleImages"] = "Images",
                    ["Loc.AutoSortRuleVideos"] = "Videos",
                    ["Loc.AutoSortRuleAudio"] = "Audio",
                    ["Loc.AutoSortRuleDocuments"] = "Documents",
                    ["Loc.AutoSortRuleArchives"] = "Archives",
                    ["Loc.AutoSortRuleInstallers"] = "Installers & scripts",
                    ["Loc.AutoSortRuleShortcuts"] = "Shortcuts",
                    ["Loc.AutoSortRuleCode"] = "Code files",
                    ["Loc.AutoSortRuleOthers"] = "Others",
                    ["Loc.AutoSortPanelFolders"] = "Folders",
                    ["Loc.AutoSortPanelImages"] = "Images",
                    ["Loc.AutoSortPanelVideos"] = "Videos",
                    ["Loc.AutoSortPanelAudio"] = "Audio",
                    ["Loc.AutoSortPanelDocuments"] = "Documents",
                    ["Loc.AutoSortPanelArchives"] = "Archives",
                    ["Loc.AutoSortPanelInstallers"] = "Installers",
                    ["Loc.AutoSortPanelShortcuts"] = "Shortcuts",
                    ["Loc.AutoSortPanelCode"] = "Code",
                    ["Loc.AutoSortPanelOthers"] = "Others",
                    ["Loc.AutoSortStatusDisabled"] = "Auto sort is disabled.",
                    ["Loc.AutoSortStatusEnabled"] = "Auto sort is active and watching your desktop.",
                    ["Loc.AutoSortCustomRuleNameFormat"] = "Custom ({0})",
                    ["Loc.AutoSortMsgInvalidExtensions"] = "Please provide at least one valid extension (for example .psd .blend).",
                    ["Loc.AutoSortMsgRuleAdded"] = "New rule added.",
                    ["Loc.AutoSortMsgRuleRemoved"] = "Rule removed.",
                    ["Loc.AutoSortMsgSortDone"] = "{0} desktop item(s) were sorted.",
                    ["Loc.AutoSortMsgSortDoneWithErrors"] = "{0} item(s) sorted, {1} could not be moved.",
                    ["Loc.AutoSortMsgSortNoItems"] = "No new desktop items found to sort.",
                    ["Loc.AutoSortMsgSortFailed"] = "Automatic sorting failed:\n{0}",
                    ["Loc.PanelStateOpen"] = "Open",
                    ["Loc.PanelStateCollapsed"] = "Collapsed",
                    ["Loc.PanelStateHidden"] = "Hidden",
                    ["Loc.PanelStateClosed"] = "Closed",
                    ["Loc.PanelTypeList"] = "List ({0})",
                    ["Loc.PanelTypeFolder"] = "Folder",
                    ["Loc.PanelTypeFile"] = "File",
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
                    ["Loc.PanelSettingsOpenBehavior"] = "Open with",
                    ["Loc.PanelSettingsOpenByDoubleClick"] = "Double-click (default)",
                    ["Loc.PanelSettingsOpenBySingleClick"] = "Single-click",
                    ["Loc.PanelSettingsExpandOnHover"] = "Expand on hover",
                    ["Loc.PanelSettingsShowHidden"] = "Show hidden items",
                    ["Loc.PanelSettingsShowParentFolder"] = "Show parent folder item",
                    ["Loc.PanelSettingsShowFileExtensions"] = "Show file extensions",
                    ["Loc.PanelSettingsShowSettingsButton"] = "Show settings button",
                    ["Loc.PanelSettingsActions"] = "Actions",
                    ["Loc.PanelSettingsViewMode"] = "View mode",
                    ["Loc.PanelSettingsViewIcons"] = "Tiles",
                    ["Loc.PanelSettingsViewDetails"] = "List details",
                    ["Loc.PanelSettingsViewPhotos"] = "Photo album",
                    ["Loc.PanelSettingsMetadata"] = "Metadata",
                    ["Loc.PanelSettingsMetadataDragHint"] = "Change order via drag and drop.",
                    ["Loc.PanelSettingsMetaType"] = "File type",
                    ["Loc.PanelSettingsMetaSize"] = "File size",
                    ["Loc.PanelSettingsMetaCreated"] = "Created",
                    ["Loc.PanelSettingsMetaModified"] = "Modified",
                    ["Loc.PanelSettingsMetaDimensions"] = "Image size",
                    ["Loc.PanelSettingsMovement"] = "Movement",
                    ["Loc.PanelSettingsSearchVisibility"] = "Search",
                    ["Loc.PanelSettingsSearchAlways"] = "Always show",
                    ["Loc.PanelSettingsSearchExpandedOnly"] = "Only when expanded",
                    ["Loc.PanelSettingsSearchHidden"] = "Hide",
                    ["Loc.PanelSettingsGlobalHint"] = "Used as layout default. Panels with custom values are kept unchanged.",
                    ["Loc.MovementTitlebar"] = "Drag title bar",
                    ["Loc.MovementButton"] = "Move button",
                    ["Loc.MovementLocked"] = "Locked",
                    ["Loc.PanelSettingsFitContent"] = "Fit to content",
                    ["Loc.PanelSettingsCancel"] = "Cancel",
                    ["Loc.PanelSettingsResetToStandard"] = "Reset to standard",
                    ["Loc.PanelSettingsSave"] = "Save",
                    ["Loc.TabNewTab"] = "New Tab",
                    ["Loc.TabClose"] = "Close tab",
                    ["Loc.TabCloseOthers"] = "Close other tabs",
                    ["Loc.TabRename"] = "Rename tab",
                    ["Loc.TabDropPreview"] = "+ New Tab",
                    ["Loc.InputDialogTitle"] = "Input",
                    ["Loc.InputDialogOk"] = "OK",
                    ["Loc.InputDialogCancel"] = "Cancel",
                    ["Loc.TrayOpen"] = "Open",
                    ["Loc.TrayExit"] = "Exit",
                    ["Loc.TrayStatusRunning"] = "Running in the background",
                    ["Loc.TrayHintEsc"] = "Press Esc to close this menu",
                    ["Loc.MsgError"] = "Error",
                    ["Loc.MsgInfo"] = "Info",
                    ["Loc.MsgPresetNameRequired"] = "Please enter a preset name.",
                    ["Loc.MsgPresetBuiltIn"] = "Built-in presets cannot be overwritten. Choose a different name.",
                    ["Loc.MsgPresetBuiltInDelete"] = "Built-in presets cannot be deleted.",
                    ["Loc.MsgPresetNameExists"] = "A preset with this name already exists.",
                    ["Loc.MsgPresetReset"] = "Default presets have been reset.",
                    ["Loc.MsgResetConfirmTitle"] = "Confirm reset",
                    ["Loc.MsgResetConfirmMessage"] = "Are you sure you want to reset all default presets?\n\nThis action cannot be undone.",
                    ["Loc.ThemesResetDefaultsFull"] = "Reset default presets",
                    ["Loc.MsgRestoreError"] = "Error restoring windows:\n{0}",
                    ["Loc.MsgSaveError"] = "Error saving windows:\n{0}",
                    ["Loc.MsgMoveFolderError"] = "Folder could not be moved:\n{0}",
                    ["Loc.MsgMoveFileError"] = "File could not be moved:\n{0}",
                    ["Loc.MsgRenameError"] = "Rename failed:\n{0}",
                    ["Loc.MsgOpenFolderError"] = "Error opening folder:\n{0}",
                    ["Loc.MsgOpenFileError"] = "Error opening file:\n{0}",
                    ["Loc.MsgDeleteConfirmTitle"] = "Confirm delete",
                    ["Loc.MsgDeleteRecycleSingle"] = "Move \"{0}\" to the Recycle Bin?",
                    ["Loc.MsgDeleteRecycleMulti"] = "Move {0} items to the Recycle Bin?",
                    ["Loc.MsgDeletePanelOnlySingle"] = "Remove \"{0}\" from this panel only?",
                    ["Loc.MsgDeletePanelOnlyMulti"] = "Remove {0} items from this panel only?",
                    ["Loc.MsgDeletePathError"] = "Could not move item to the Recycle Bin:\n{0}",
                    ["Loc.MsgStartupError"] = "Autostart could not be updated:\n{0}",
                    ["Loc.MsgUpdateAvailable"] = "Version {0} is available (installed: {1}). Open the release page now?",
                    ["Loc.MsgUpdateAvailableActions"] = "Version {0} is available (installed: {1}).\n\nYes: Open release page\nNo: Install now (silent)\nCancel: Abort",
                    ["Loc.UpdateDialogTitle"] = "Update available",
                    ["Loc.UpdateDialogInstalledLabel"] = "Installed",
                    ["Loc.UpdateDialogLatestLabel"] = "New version",
                    ["Loc.UpdateDialogChooseAction"] = "Choose an action",
                    ["Loc.UpdateDialogOpenReleaseButton"] = "Open release page",
                    ["Loc.UpdateDialogInstallNowButton"] = "Install now",
                    ["Loc.UpdateDialogLaterButton"] = "Later",
                    ["Loc.MsgUpdateUpToDate"] = "You're up to date (version {0}).",
                    ["Loc.MsgUpdateCheckFailed"] = "Update check failed:\n{0}",
                    ["Loc.MsgUpdateOpenPageFailed"] = "Could not open the release page.",
                    ["Loc.MsgUpdateNoInstallerAsset"] = "No installer asset was found for this version.",
                    ["Loc.MsgUpdateDownloadFailedForInstall"] = "Installer could not be downloaded. Please try again later.",
                    ["Loc.MsgUpdateInstallStarting"] = "Silent installation for version {0} is starting.\nDesktopPlus will close now and restart automatically after the update.",
                    ["Loc.MsgUpdateInstallStartFailed"] = "Silent installer could not be started.",
                    ["Loc.UpdateStatusDownloading"] = "Downloading update {0}...",
                    ["Loc.UpdateStatusInstalling"] = "Installing update {0}...",
                    ["Loc.UpdateInstallerReadyTitle"] = "Update ready",
                    ["Loc.UpdateInstallerReadyBody"] = "Update {0} has been downloaded. Installation is ready on the next launch.",
                    ["Loc.MsgLanguageImportSuccess"] = "Language \"{0}\" ({1}) was imported.",
                    ["Loc.MsgLanguageImportInvalid"] = "The JSON file is not a valid language pack.",
                    ["Loc.MsgLanguageImportFailed"] = "Language import failed:\n{0}",
                    ["Loc.MsgLanguageImportReservedCode"] = "Language code \"{0}\" is reserved for built-in languages.",
                    ["Loc.PromptLayoutName"] = "Layout name",
                    ["Loc.PromptPanelName"] = "Panel name",
                    ["Loc.PromptPresetName"] = "Preset name",
                    ["Loc.ThemesPresetDefaultName"] = "New preset"
                }
            };

        private static readonly HashSet<string> BuiltInLanguageCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "de",
            "en",
            "lv"
        };

        private static readonly HashSet<string> LoadedCustomLanguageCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> CustomLanguageDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private sealed class ImportedLanguagePack
        {
            public string Code { get; init; } = "";
            public string Name { get; init; } = "";
            public Dictionary<string, string> Translations { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        static MainWindow()
        {
            EnsureBuiltInLanguages();
        }

        private static string CustomLanguageFolderPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPlus",
            "Languages");

        private static void EnsureBuiltInLanguages()
        {
            if (LocalizationData.ContainsKey("lv"))
            {
                return;
            }

            if (!LocalizationData.TryGetValue("en", out var english))
            {
                return;
            }

            var latvian = new Dictionary<string, string>(english, StringComparer.OrdinalIgnoreCase)
            {
                ["Loc.TabGeneral"] = "Vispārīgi",
                ["Loc.TabPanels"] = "Paneļi",
                ["Loc.TabLayouts"] = "Izkārtojumi",
                ["Loc.TabThemes"] = "Tēmas",
                ["Loc.TabAutoSort"] = "Auto kārtošana",
                ["Loc.TabShortcuts"] = "Saīsnes",
                ["Loc.GeneralTitle"] = "Vispārīgi",
                ["Loc.GeneralLanguageLabel"] = "Valoda",
                ["Loc.LanguageGerman"] = "Vācu",
                ["Loc.LanguageEnglish"] = "Angļu",
                ["Loc.LanguageLatvian"] = "Latviešu",
                ["Loc.GeneralImportLanguageButton"] = "Importēt valodas JSON",
                ["Loc.GeneralStartupCheckbox"] = "Palaist ar Windows",
                ["Loc.GeneralCloseLabel"] = "Aizverot",
                ["Loc.GeneralUpdatesLabel"] = "Atjauninājumi",
                ["Loc.GeneralAutoUpdateCheckbox"] = "Automātiski instalēt atjauninājumus",
                ["Loc.GeneralCheckUpdatesButton"] = "Pārbaudīt atjauninājumus",
                ["Loc.CloseBehaviorMinimize"] = "Minimizēt",
                ["Loc.CloseBehaviorExit"] = "Iziet no lietotnes",
                ["Loc.MsgUpdateAvailable"] = "Ir pieejama versija {0} (instalēta: {1}). Vai atvērt relīzes lapu?",
                ["Loc.MsgUpdateAvailableActions"] = "Ir pieejama versija {0} (instalēta: {1}).\n\nJā: Atvērt relīzes lapu\nNē: Instalēt tagad (klusi)\nAtcelt: Pārtraukt",
                ["Loc.UpdateDialogTitle"] = "Pieejams atjauninajums",
                ["Loc.UpdateDialogInstalledLabel"] = "Instaleta",
                ["Loc.UpdateDialogLatestLabel"] = "Jauna versija",
                ["Loc.UpdateDialogChooseAction"] = "Izvelies darbibu",
                ["Loc.UpdateDialogOpenReleaseButton"] = "Atvert relizes lapu",
                ["Loc.UpdateDialogInstallNowButton"] = "Instalet tagad",
                ["Loc.UpdateDialogLaterButton"] = "Velak",
                ["Loc.MsgUpdateUpToDate"] = "Jums ir jaunākā versija ({0}).",
                ["Loc.MsgUpdateCheckFailed"] = "Atjauninājumu pārbaude neizdevās:\n{0}",
                ["Loc.MsgUpdateOpenPageFailed"] = "Neizdevās atvērt relīzes lapu.",
                ["Loc.MsgUpdateNoInstallerAsset"] = "Šai versijai netika atrasts instalatora fails.",
                ["Loc.MsgUpdateDownloadFailedForInstall"] = "Instalatoru neizdevās lejupielādēt. Lūdzu, mēģiniet vēlāk.",
                ["Loc.MsgUpdateInstallStarting"] = "Klusā instalēšana versijai {0} tiek sākta.\nDesktopPlus tagad tiks aizvērts un pēc atjaunināšanas automātiski palaists no jauna.",
                ["Loc.MsgUpdateInstallStartFailed"] = "Klusais instalators netika palaists.",
                ["Loc.UpdateStatusDownloading"] = "Lejupielādē atjauninājumu {0}...",
                ["Loc.UpdateStatusInstalling"] = "Instalē atjauninājumu {0}...",
                ["Loc.MsgError"] = "Kļūda",
                ["Loc.MsgInfo"] = "Informācija",
                ["Loc.MsgLanguageImportSuccess"] = "Valoda \"{0}\" ({1}) tika importēta.",
                ["Loc.MsgLanguageImportInvalid"] = "JSON fails nav derīga valodas pakotne.",
                ["Loc.MsgLanguageImportFailed"] = "Valodas imports neizdevās:\n{0}",
                ["Loc.MsgLanguageImportReservedCode"] = "Valodas kods \"{0}\" ir rezervēts iebūvētajām valodām."
            };

            LocalizationData["lv"] = latvian;
        }

        private static void LoadCustomLanguagesFromDisk()
        {
            EnsureBuiltInLanguages();

            foreach (string code in LoadedCustomLanguageCodes.ToList())
            {
                LocalizationData.Remove(code);
            }

            LoadedCustomLanguageCodes.Clear();
            CustomLanguageDisplayNames.Clear();

            if (!Directory.Exists(CustomLanguageFolderPath))
            {
                return;
            }

            foreach (string filePath in Directory.EnumerateFiles(CustomLanguageFolderPath, "*.json"))
            {
                if (!TryLoadLanguagePackFromFile(filePath, out ImportedLanguagePack pack, out _))
                {
                    continue;
                }

                if (BuiltInLanguageCodes.Contains(pack.Code))
                {
                    continue;
                }

                RegisterCustomLanguage(pack);
            }
        }

        private static void RegisterCustomLanguage(ImportedLanguagePack pack)
        {
            if (string.IsNullOrWhiteSpace(pack.Code) || pack.Translations.Count == 0)
            {
                return;
            }

            var merged = LocalizationData.TryGetValue(DefaultLanguageCode, out var defaults)
                ? new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in pack.Translations)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                merged[pair.Key] = pair.Value;
            }

            LocalizationData[pack.Code] = merged;
            LoadedCustomLanguageCodes.Add(pack.Code);
            CustomLanguageDisplayNames[pack.Code] = string.IsNullOrWhiteSpace(pack.Name)
                ? pack.Code.ToUpperInvariant()
                : pack.Name.Trim();
        }

        private static bool ImportCustomLanguageFromFile(
            string filePath,
            out string importedCode,
            out string importedName,
            out string errorMessage)
        {
            importedCode = string.Empty;
            importedName = string.Empty;
            errorMessage = string.Empty;

            if (!TryLoadLanguagePackFromFile(filePath, out ImportedLanguagePack pack, out string parseError))
            {
                errorMessage = parseError;
                return false;
            }

            if (BuiltInLanguageCodes.Contains(pack.Code))
            {
                errorMessage = string.Format(GetString("Loc.MsgLanguageImportReservedCode"), pack.Code);
                return false;
            }

            try
            {
                Directory.CreateDirectory(CustomLanguageFolderPath);
                string targetFilePath = Path.Combine(CustomLanguageFolderPath, $"{pack.Code}.json");
                var normalizedPack = new
                {
                    code = pack.Code,
                    name = pack.Name,
                    translations = pack.Translations
                        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                };

                string normalizedJson = JsonSerializer.Serialize(
                    normalizedPack,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                File.WriteAllText(targetFilePath, normalizedJson, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errorMessage = string.Format(GetString("Loc.MsgLanguageImportFailed"), ex.Message);
                return false;
            }

            RegisterCustomLanguage(pack);
            importedCode = pack.Code;
            importedName = pack.Name;
            return true;
        }

        private static bool TryLoadLanguagePackFromFile(string filePath, out ImportedLanguagePack pack, out string errorMessage)
        {
            pack = new ImportedLanguagePack();
            errorMessage = GetString("Loc.MsgLanguageImportInvalid");

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                string fallbackCode = NormalizeLanguageCode(Path.GetFileNameWithoutExtension(filePath));
                string fallbackName = Path.GetFileNameWithoutExtension(filePath);
                return TryParseLanguagePack(json, fallbackCode, fallbackName, out pack, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = string.Format(GetString("Loc.MsgLanguageImportFailed"), ex.Message);
                return false;
            }
        }

        private static bool TryParseLanguagePack(
            string json,
            string fallbackCode,
            string fallbackName,
            out ImportedLanguagePack pack,
            out string errorMessage)
        {
            pack = new ImportedLanguagePack();
            errorMessage = GetString("Loc.MsgLanguageImportInvalid");

            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                JsonElement root = document.RootElement;
                string code = NormalizeLanguageCode(ReadFirstStringProperty(root, "code", "languageCode", "language", "lang", "locale"));
                if (string.IsNullOrWhiteSpace(code))
                {
                    code = DetectLanguageCode(root);
                }
                if (string.IsNullOrWhiteSpace(code))
                {
                    code = fallbackCode;
                }
                if (string.IsNullOrWhiteSpace(code))
                {
                    return false;
                }

                string name = ReadFirstStringProperty(root, "name", "displayName", "nativeName", "languageName");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = fallbackName;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = code.ToUpperInvariant();
                }

                var translations = ExtractTranslationValues(root, code);
                if (translations.Count == 0)
                {
                    return false;
                }

                pack = new ImportedLanguagePack
                {
                    Code = code,
                    Name = name.Trim(),
                    Translations = translations
                };

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = string.Format(GetString("Loc.MsgLanguageImportFailed"), ex.Message);
                return false;
            }
        }

        private static Dictionary<string, string> ExtractTranslationValues(JsonElement root, string languageCode)
        {
            (JsonElement translationRoot, bool usedNestedRoot) = ResolveTranslationRoot(root, languageCode);
            var rawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FlattenTranslationObject(translationRoot, null, rawValues);
            if (rawValues.Count == 0 && usedNestedRoot)
            {
                FlattenTranslationObject(root, null, rawValues);
            }

            var knownDefaultKeys = LocalizationData.TryGetValue(DefaultLanguageCode, out var defaults)
                ? new HashSet<string>(defaults.Keys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in rawValues)
            {
                string key = pair.Key?.Trim() ?? string.Empty;
                string value = pair.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (key.StartsWith("Loc.", StringComparison.OrdinalIgnoreCase))
                {
                    normalized[key] = value;
                    continue;
                }

                string prefixed = $"Loc.{key}";
                if (knownDefaultKeys.Contains(prefixed))
                {
                    normalized[prefixed] = value;
                }
            }

            return normalized;
        }

        private static (JsonElement TranslationRoot, bool UsedNestedRoot) ResolveTranslationRoot(JsonElement root, string languageCode)
        {
            if (TryGetPropertyIgnoreCase(root, "translations", out JsonElement translations))
            {
                return (UnwrapLanguageContainer(translations, languageCode), true);
            }

            if (TryGetPropertyIgnoreCase(root, "messages", out JsonElement messages))
            {
                return (UnwrapLanguageContainer(messages, languageCode), true);
            }

            if (TryGetPropertyIgnoreCase(root, "strings", out JsonElement strings))
            {
                return (UnwrapLanguageContainer(strings, languageCode), true);
            }

            if (TryGetPropertyIgnoreCase(root, "dictionary", out JsonElement dictionary))
            {
                return (UnwrapLanguageContainer(dictionary, languageCode), true);
            }

            if (TryGetPropertyIgnoreCase(root, "translation", out JsonElement translation))
            {
                return (translation, true);
            }

            if (TryGetPropertyIgnoreCase(root, "resources", out JsonElement resources) &&
                resources.ValueKind == JsonValueKind.Object)
            {
                string primaryLanguageCode = GetPrimaryLanguageCode(languageCode);
                if (!string.IsNullOrWhiteSpace(languageCode) &&
                    (TryGetPropertyIgnoreCase(resources, languageCode, out JsonElement languageNode) ||
                     (!string.IsNullOrWhiteSpace(primaryLanguageCode) &&
                      TryGetPropertyIgnoreCase(resources, primaryLanguageCode, out languageNode))))
                {
                    if (TryGetPropertyIgnoreCase(languageNode, "translation", out JsonElement resourceTranslation))
                    {
                        return (resourceTranslation, true);
                    }

                    return (languageNode, true);
                }

                foreach (var resource in resources.EnumerateObject())
                {
                    if (resource.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (TryGetPropertyIgnoreCase(resource.Value, "translation", out JsonElement firstTranslation))
                    {
                        return (firstTranslation, true);
                    }

                    return (resource.Value, true);
                }
            }

            string primaryCode = GetPrimaryLanguageCode(languageCode);
            if (!string.IsNullOrWhiteSpace(languageCode) &&
                (TryGetPropertyIgnoreCase(root, languageCode, out JsonElement rootLanguageNode) ||
                 (!string.IsNullOrWhiteSpace(primaryCode) &&
                  TryGetPropertyIgnoreCase(root, primaryCode, out rootLanguageNode))))
            {
                if (TryGetPropertyIgnoreCase(rootLanguageNode, "translation", out JsonElement rootLanguageTranslation))
                {
                    return (rootLanguageTranslation, true);
                }

                return (rootLanguageNode, true);
            }

            return (root, false);
        }

        private static JsonElement UnwrapLanguageContainer(JsonElement container, string languageCode)
        {
            if (container.ValueKind != JsonValueKind.Object)
            {
                return container;
            }

            string primaryCode = GetPrimaryLanguageCode(languageCode);
            if (!string.IsNullOrWhiteSpace(languageCode) &&
                (TryGetPropertyIgnoreCase(container, languageCode, out JsonElement scoped) ||
                 (!string.IsNullOrWhiteSpace(primaryCode) &&
                  TryGetPropertyIgnoreCase(container, primaryCode, out scoped))))
            {
                if (TryGetPropertyIgnoreCase(scoped, "translation", out JsonElement scopedTranslation))
                {
                    return scopedTranslation;
                }

                return scoped;
            }

            return container;
        }

        private static string GetPrimaryLanguageCode(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return string.Empty;
            }

            string[] parts = languageCode.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return string.Empty;
            }

            return parts[0];
        }

        private static void FlattenTranslationObject(JsonElement element, string? prefix, Dictionary<string, string> output)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        string childPrefix = string.IsNullOrWhiteSpace(prefix)
                            ? property.Name
                            : $"{prefix}.{property.Name}";
                        FlattenTranslationObject(property.Value, childPrefix, output);
                    }
                    break;

                case JsonValueKind.String:
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        output[prefix] = element.GetString() ?? string.Empty;
                    }
                    break;

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        output[prefix] = element.ToString();
                    }
                    break;
            }
        }

        private static string DetectLanguageCode(JsonElement root)
        {
            foreach (string containerName in new[] { "translations", "messages", "strings", "dictionary" })
            {
                if (!TryGetPropertyIgnoreCase(root, containerName, out JsonElement container) ||
                    container.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var languageProperty in container.EnumerateObject())
                {
                    string code = NormalizeLanguageCode(languageProperty.Name);
                    if (IsLikelyLanguageCode(code))
                    {
                        return code;
                    }
                }
            }

            if (TryGetPropertyIgnoreCase(root, "resources", out JsonElement resources) &&
                resources.ValueKind == JsonValueKind.Object)
            {
                foreach (var resource in resources.EnumerateObject())
                {
                    string code = NormalizeLanguageCode(resource.Name);
                    if (IsLikelyLanguageCode(code))
                    {
                        return code;
                    }
                }
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string code = NormalizeLanguageCode(property.Name);
                if (!IsLikelyLanguageCode(code))
                {
                    continue;
                }

                if (TryGetPropertyIgnoreCase(property.Value, "translation", out _) ||
                    TryGetPropertyIgnoreCase(property.Value, "translations", out _))
                {
                    return code;
                }
            }

            return string.Empty;
        }

        private static bool IsLikelyLanguageCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 3)
            {
                return false;
            }

            foreach (string part in parts)
            {
                if (part.Length < 2 || part.Length > 8)
                {
                    return false;
                }

                if (!part.All(char.IsLetter))
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeLanguageCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (char ch in value.Trim())
            {
                if (char.IsLetter(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
                else if (ch == '-' || ch == '_')
                {
                    builder.Append('-');
                }
            }

            string normalized = builder.ToString().Trim('-');
            while (normalized.Contains("--", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            }

            if (!IsLikelyLanguageCode(normalized))
            {
                return string.Empty;
            }

            return normalized;
        }

        private static string ReadFirstStringProperty(JsonElement element, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                if (!TryGetPropertyIgnoreCase(element, propertyName, out JsonElement value))
                {
                    continue;
                }

                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        return value.GetString() ?? string.Empty;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return value.ToString();
                }
            }

            return string.Empty;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private void RefreshLanguageComboItems()
        {
            if (LanguageCombo == null)
            {
                return;
            }

            var dynamicItems = LanguageCombo.Items
                .OfType<System.Windows.Controls.ComboBoxItem>()
                .Where(item => item.Tag is string code && !BuiltInLanguageCodes.Contains(code))
                .ToList();

            foreach (var item in dynamicItems)
            {
                LanguageCombo.Items.Remove(item);
            }

            foreach (var language in CustomLanguageDisplayNames
                .OrderBy(entry => entry.Value, StringComparer.CurrentCultureIgnoreCase))
            {
                LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Tag = language.Key,
                    Content = language.Value
                });
            }
        }

        private void ImportLanguageButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                Title = GetString("Loc.GeneralImportLanguageButton"),
                Multiselect = false,
                CheckFileExists = true
            };

            if (openFileDialog.ShowDialog(this) != true)
            {
                return;
            }

            if (!ImportCustomLanguageFromFile(
                    openFileDialog.FileName,
                    out string importedCode,
                    out string importedName,
                    out string errorMessage))
            {
                System.Windows.MessageBox.Show(
                    string.IsNullOrWhiteSpace(errorMessage)
                        ? GetString("Loc.MsgLanguageImportInvalid")
                        : errorMessage,
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApplyLanguageSelection(importedCode);
            System.Windows.MessageBox.Show(
                string.Format(GetString("Loc.MsgLanguageImportSuccess"), importedName, importedCode),
                GetString("Loc.MsgInfo"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ApplyLanguage(string? code)
        {
            EnsureBuiltInLanguages();

            string normalized = NormalizeLanguageCode(code);
            if (string.IsNullOrWhiteSpace(normalized) || !LocalizationData.ContainsKey(normalized))
            {
                normalized = DefaultLanguageCode;
            }

            CurrentLanguageCode = normalized;
            if (System.Windows.Application.Current?.Resources != null)
            {
                if (LocalizationData.TryGetValue(DefaultLanguageCode, out var defaultValues))
                {
                    foreach (var pair in defaultValues)
                    {
                        System.Windows.Application.Current.Resources[pair.Key] = pair.Value;
                    }
                }

                if (LocalizationData.TryGetValue(normalized, out var values))
                {
                    foreach (var pair in values)
                    {
                        System.Windows.Application.Current.Resources[pair.Key] = pair.Value;
                    }
                }
            }

            UpdateNotifyIconMenu();
        }

        public static string GetString(string key)
        {
            if (System.Windows.Application.Current?.Resources != null &&
                System.Windows.Application.Current.Resources.Contains(key))
            {
                return System.Windows.Application.Current.Resources[key]?.ToString() ?? key;
            }

            if (LocalizationData.TryGetValue(CurrentLanguageCode, out var values) &&
                values.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (LocalizationData.TryGetValue(DefaultLanguageCode, out var defaultValues) &&
                defaultValues.TryGetValue(key, out var fallbackValue) &&
                !string.IsNullOrWhiteSpace(fallbackValue))
            {
                return fallbackValue;
            }

            return key;
        }
    }
}
