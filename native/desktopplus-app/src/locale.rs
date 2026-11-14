use serde_json::Value;
use std::{cell::RefCell, collections::HashMap, path::Path, sync::OnceLock};

fn bundled() -> &'static Value {
    static BUNDLED: OnceLock<Value> = OnceLock::new();
    BUNDLED.get_or_init(|| {
        serde_json::from_str(include_str!("../assets/languages.json"))
            .expect("Bundled languages are valid")
    })
}
thread_local! { static CURRENT: RefCell<HashMap<String, String>> = RefCell::new(HashMap::new()); }
pub fn configure(code: &str, directory: &Path) {
    let english = bundled()["en"].as_object().unwrap();
    let mut translations = bundled()
        .get(code)
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    for folder in [
        crate::config::app_data().join("DesktopPlus/Languages"),
        directory.to_owned(),
    ] {
        if let Ok(files) = std::fs::read_dir(folder) {
            for file in files
                .filter_map(Result::ok)
                .filter(|file| file.path().extension().is_some_and(|ext| ext == "json"))
            {
                if let Ok((language, _, values)) = read(&file.path()) {
                    if language == code {
                        translations.extend(values);
                    }
                }
            }
        }
    }
    let mut phrases = HashMap::new();
    for (key, source) in english {
        if let (Some(source), Some(target)) = (
            source.as_str(),
            translations.get(key).and_then(Value::as_str),
        ) {
            phrases.insert(source.to_owned(), target.to_owned());
        }
    }
    if matches!(code, "de" | "lv") {
        for (source, german, latvian) in [
            (
                "Updates are disabled in test and debug builds.",
                "Updates sind in Test- und Debug-Versionen deaktiviert.",
                "Atjauninājumi testa un atkļūdošanas versijās ir atspējoti.",
            ),
            (
                "Checking for updates and preparing the download…",
                "Updates werden gesucht und heruntergeladen …",
                "Tiek meklēti un lejupielādēti atjauninājumi…",
            ),
            (
                "DesktopPlus is up to date.",
                "DesktopPlus ist aktuell.",
                "DesktopPlus ir atjaunināts.",
            ),
            (
                "Version {version} is ready. Install now and restart DesktopPlus?",
                "Version {version} ist bereit. Jetzt installieren und DesktopPlus neu starten?",
                "Versija {version} ir gatava. Instalēt un restartēt DesktopPlus?",
            ),
            (
                "Version {version} will be installed at the next start.",
                "Version {version} wird beim nächsten Start installiert.",
                "Versija {version} tiks instalēta nākamajā palaišanas reizē.",
            ),
            (
                "Update installation cancelled.",
                "Update-Installation abgebrochen.",
                "Atjauninājuma instalēšana atcelta.",
            ),
            (
                "Update failed:",
                "Update fehlgeschlagen:",
                "Atjaunināšana neizdevās:",
            ),
            ("Appearance", "Darstellung", "Izskats"),
            ("Theme", "Theme", "Tēma"),
            ("Panels", "Panels", "Paneļi"),
            ("Layout", "Layout", "Izkārtojums"),
            ("Glass", "Glas", "Stikls"),
            (
                "Strong frosted glass",
                "Stark mattiertes Glas",
                "Spēcīgi matēts stikls",
            ),
            ("Colors", "Farben", "Krāsas"),
            ("Typography", "Schrift", "Tipogrāfija"),
            ("Header buttons", "Leisten-Schaltflächen", "Joslas pogas"),
            ("Panel corners", "Panel-Ecken", "Paneļa stūri"),
            ("Square", "Eckig", "Taisni"),
            ("Rounded", "Abgerundet", "Noapaļoti"),
            ("Visible", "Sichtbar", "Redzams"),
            ("Glass effect", "Glaseffekt", "Stikla efekts"),
            ("Frosted glass", "Mattes Glas", "Matēts stikls"),
            (
                "Clear glass (no blur)",
                "Klares Glas (ohne Unschärfe)",
                "Caurspīdīgs stikls (bez izplūšanas)",
            ),
            (
                "Solid (no transparency)",
                "Deckend (ohne Transparenz)",
                "Necaurspīdīgs",
            ),
            (
                "Tint opacity (10–100%)",
                "Deckkraft der Tönung (10–100 %)",
                "Tonējuma necaurredzamība (10–100%)",
            ),
            ("Background color", "Hintergrundfarbe", "Fona krāsa"),
            ("Header color", "Leistenfarbe", "Joslas krāsa"),
            ("Text color", "Textfarbe", "Teksta krāsa"),
            (
                "Secondary text color",
                "Sekundäre Textfarbe",
                "Sekundārā teksta krāsa",
            ),
            ("Accent color", "Akzentfarbe", "Akcenta krāsa"),
            ("Font family", "Schriftart", "Fonts"),
            (
                "Item text size (9–24)",
                "Dateinamen: Schriftgröße (9–24)",
                "Vienumu teksta lielums (9–24)",
            ),
            (
                "Title text size (10–30)",
                "Titel: Schriftgröße (10–30)",
                "Virsraksta lielums (10–30)",
            ),
            (
                "Search visibility",
                "Sichtbarkeit der Suche",
                "Meklēšanas redzamība",
            ),
            (
                "Also when collapsed",
                "Auch eingeklappt anzeigen",
                "Arī sakļautā veidā",
            ),
            ("blank = inherit", "leer = übernehmen", "tukšs = mantot"),
            ("Customize…", "Anpassen …", "Pielāgot…"),
            (
                "Panel appearance…",
                "Panel-Darstellung …",
                "Paneļa izskats…",
            ),
            ("Panel appearance", "Panel-Darstellung", "Paneļa izskats"),
            (
                "Reset this panel to the theme",
                "Dieses Panel auf das Theme zurücksetzen",
                "Atjaunot šī paneļa tēmas iestatījumus",
            ),
            (
                "Reset all panel overrides to the theme",
                "Alle Panels auf das Theme zurücksetzen",
                "Atjaunot tēmas iestatījumus visiem paneļiem",
            ),
            (
                "Theme defaults and individual panels. Changes apply when you save.",
                "Theme und einzelne Panels anpassen. Änderungen werden beim Speichern übernommen.",
                "Pielāgojiet tēmu un paneļus. Izmaiņas stājas spēkā pēc saglabāšanas.",
            ),
            (
                "Inherit from theme, or customize this panel. Blank fields follow the theme.",
                "Vom Theme übernehmen oder selbst anpassen. Leere Felder folgen Theme oder Vorlage.",
                "Mantojiet no tēmas vai pielāgojiet paneli. Tukši lauki izmanto tēmu.",
            ),
            (
                "Tint opacity must be a whole number between 10 and 100.",
                "Die Deckkraft muss eine ganze Zahl zwischen 10 und 100 sein.",
                "Necaurredzamībai jābūt veselam skaitlim no 10 līdz 100.",
            ),
            (
                "Title size must be between 10 and 30.",
                "Die Titelgröße muss zwischen 10 und 30 liegen.",
                "Virsraksta lielumam jābūt no 10 līdz 30.",
            ),
            ("Layout builder", "Layout-Builder", "Izkārtojuma veidotājs"),
            (
                "Layout builder…",
                "Layout-Builder …",
                "Izkārtojuma veidotājs…",
            ),
            ("Panel bar style", "Leisten-Stil", "Paneļa joslas stils"),
            (
                "Panel bar style (theme)",
                "Leisten-Stil für das Theme",
                "Tēmas paneļu joslas stils",
            ),
            (
                "Inherit from theme",
                "Vom Theme übernehmen",
                "Mantot no tēmas",
            ),
            ("Clear line", "Klare Linie", "Skaidra līnija"),
            ("Compact pills", "Kompakte Kapseln", "Kompaktas cilnes"),
            (
                "Centered switch",
                "Zentrierter Umschalter",
                "Centrēts pārslēdzējs",
            ),
            ("Two rows", "Zwei Ebenen", "Divas rindas"),
            ("Floating center", "Schwebende Mitte", "Peldošs centrs"),
            (
                "Reset panel overrides to theme",
                "Alle Panels wieder vom Theme übernehmen lassen",
                "Atjaunot tēmas stilu visiem paneļiem",
            ),
            (
                "Choose a theme style or a different style for each panel.",
                "Einen Stil für das Theme oder eigene Stile pro Panel wählen.",
                "Izvēlieties tēmas stilu vai atsevišķu stilu katram panelim.",
            ),
            ("Keyboard shortcuts", "Tastenkürzel", "Īsinājumtaustiņi"),
            (
                "Show / hide panels",
                "Panels ein- / ausblenden",
                "Rādīt / slēpt paneļus",
            ),
            (
                "Bring panels forward temporarily",
                "Panels vorübergehend in den Vordergrund holen",
                "Īslaicīgi parādīt paneļus priekšplānā",
            ),
            (
                "Background (#RRGGBB)",
                "Hintergrund (#RRGGBB)",
                "Fons (#RRGGBB)",
            ),
            (
                "Header and buttons (#RRGGBB)",
                "Kopfzeile und Schaltflächen (#RRGGBB)",
                "Galvene un pogas (#RRGGBB)",
            ),
            ("Text (#RRGGBB)", "Textfarbe (#RRGGBB)", "Teksts (#RRGGBB)"),
            (
                "Accent (#RRGGBB)",
                "Akzentfarbe (#RRGGBB)",
                "Akcents (#RRGGBB)",
            ),
            ("Font family", "Schriftart", "Fonta saime"),
            (
                "Text size (9–24)",
                "Schriftgröße (9–24)",
                "Teksta lielums (9–24)",
            ),
            (
                "Colors must use #RRGGBB format.",
                "Bitte Farben im Format #RRGGBB eingeben.",
                "Krāsas jānorāda formātā #RRGGBB.",
            ),
            (
                "Text size must be between 9 and 24.",
                "Die Schriftgröße muss zwischen 9 und 24 liegen.",
                "Teksta lielumam jābūt no 9 līdz 24.",
            ),
            (
                "Use a shortcut such as Ctrl + Alt + H.",
                "Bitte ein Tastenkürzel wie Ctrl + Alt + H verwenden.",
                "Izmantojiet īsinājumtaustiņu, piemēram, Ctrl + Alt + H.",
            ),
            ("Language", "Sprache", "Valoda"),
            (
                "Start with Windows",
                "Mit Windows starten",
                "Palaist kopā ar Windows",
            ),
            (
                "Project / help",
                "Projekt und Hilfe",
                "Projekts un palīdzība",
            ),
            (
                "Exit DesktopPlus",
                "DesktopPlus beenden",
                "Aizvērt DesktopPlus",
            ),
            ("Toggle panels", "Panels umschalten", "Pārslēgt paneļus"),
            ("Properties", "Eigenschaften", "Rekvizīti"),
            ("New folder", "Neuer Ordner", "Jauna mape"),
            ("Detach tab", "Tab lösen", "Atdalīt cilni"),
            ("Details columns…", "Detailspalten …", "Detaļu kolonnas…"),
            (
                "Column settings",
                "Spalteneinstellungen",
                "Kolonnu iestatījumi",
            ),
            ("Your workspace", "Dein Arbeitsbereich", "Tava darbvieta"),
            ("WORKSPACE", "ARBEITSBEREICH", "DARBVIETA"),
            ("Overview", "Übersicht", "Pārskats"),
            ("Folder panel", "Ordner-Panel", "Mapes panelis"),
            ("Pinned list", "Angeheftete Liste", "Piesprausts saraksts"),
            (
                "Bring a folder to your desktop",
                "Ordner direkt auf dem Desktop",
                "Mape tieši darbvirsmā",
            ),
            (
                "Keep your favorites together",
                "Alles Wichtige an einem Ort",
                "Viss svarīgais vienuviet",
            ),
            (
                "Make it work your way.",
                "So, wie du am liebsten arbeitest.",
                "Pielāgo savam darba stilam.",
            ),
            ("More", "Weitere Optionen", "Vairāk"),
            (
                "Enter a font family.",
                "Bitte eine Schriftart eingeben.",
                "Ievadiet fonta saimi.",
            ),
            ("Panel title", "Panel-Name", "Paneļa nosaukums"),
            ("Tab name", "Tab-Name", "Cilnes nosaukums"),
            ("General", "Allgemein", "Vispārīgi"),
            ("Saved.", "Gespeichert.", "Saglabāts."),
            ("Header", "Kopfzeile", "Galvene"),
            ("Behavior", "Verhalten", "Uzvedība"),
            ("Columns", "Spalten", "Kolonnas"),
            ("Browse…", "Auswählen …", "Pārlūkot…"),
            ("Open folders", "Ordner öffnen", "Atvērt mapes"),
            ("Open files", "Dateien öffnen", "Atvērt failus"),
            ("Inside the panel", "Innerhalb des Panels", "Panelī"),
            ("In File Explorer", "Im Datei-Explorer", "Failu pārlūkā"),
            ("Double-click", "Mit Doppelklick", "Ar dubultklikšķi"),
            ("Single-click", "Mit einfachem Klick", "Ar vienu klikšķi"),
            ("View mode", "Ansichtsmodus", "Skata režīms"),
            ("Appearance preset", "Design-Vorlage", "Izskata iestatījums"),
            (
                "Use global appearance",
                "Globales Design verwenden",
                "Lietot kopējo izskatu",
            ),
            (
                "Fit width to contents",
                "Breite an Inhalt anpassen",
                "Pielāgot platumu saturam",
            ),
            (
                "Fit height to contents",
                "Höhe an Inhalt anpassen",
                "Pielāgot augstumu saturam",
            ),
            (
                "Search in the header",
                "Suche in der Kopfzeile",
                "Meklēšana galvenē",
            ),
            ("Search button", "Suchsymbol", "Meklēšanas poga"),
            ("Search field", "Suchfeld", "Meklēšanas lauks"),
            (
                "Search only when expanded",
                "Suche nur im ausgeklappten Panel",
                "Meklēšana tikai izvērstā panelī",
            ),
            ("Settings button", "Einstellungssymbol", "Iestatījumu poga"),
            ("Always visible", "Immer sichtbar", "Vienmēr redzams"),
            (
                "Only when expanded",
                "Nur ausgeklappt sichtbar",
                "Tikai izvērstā veidā",
            ),
            (
                "Title and tab alignment",
                "Ausrichtung von Titel und Tabs",
                "Nosaukuma un ciļņu izlīdzināšana",
            ),
            ("Left", "Links", "Pa kreisi"),
            ("Center", "Zentriert", "Centrā"),
            ("Right", "Rechts", "Pa labi"),
            (
                "Show close button",
                "Schließen-Symbol anzeigen",
                "Rādīt aizvēršanas pogu",
            ),
            (
                "Show Empty Recycle Bin button",
                "Schaltfläche zum Leeren des Papierkorbs",
                "Rādīt atkritnes iztukšošanas pogu",
            ),
            (
                "Show clear button",
                "Leeren-Symbol anzeigen",
                "Rādīt iztukšošanas pogu",
            ),
            ("Move panel", "Panel verschieben", "Pārvietot paneli"),
            (
                "Drag to resize; double-click to fit width, then height.",
                "Ziehen zum Vergrößern; Doppelklick passt erst die Breite, dann die Höhe an.",
                "Velciet izmēra maiņai; dubultklikšķis pielāgo platumu, pēc tam augstumu.",
            ),
            (
                "Drag the header",
                "An der Kopfzeile ziehen",
                "Vilkt aiz galvenes",
            ),
            (
                "Drag handle only",
                "Nur am Verschiebegriff ziehen",
                "Vilkt tikai aiz roktura",
            ),
            (
                "Lock position and size",
                "Position und Größe sperren",
                "Bloķēt pozīciju un izmēru",
            ),
            ("Collapse panel", "Panel einklappen", "Sakļaut paneli"),
            (
                "Button and double-click",
                "Schaltfläche und Doppelklick",
                "Poga un dubultklikšķis",
            ),
            ("Button only", "Nur Schaltfläche", "Tikai poga"),
            (
                "Double-click only",
                "Nur Doppelklick",
                "Tikai dubultklikšķis",
            ),
            (
                "Expand on mouse hover",
                "Bei Mausberührung aufklappen",
                "Izvērst, novietojot peles rādītāju",
            ),
            (
                "Parent folder navigation",
                "Zum übergeordneten Ordner",
                "Vecākmapes navigācija",
            ),
            (
                "Back button in header",
                "Zurück-Pfeil in der Kopfzeile",
                "Atpakaļpoga galvenē",
            ),
            (
                "Parent folder in file list",
                "Übergeordneter Ordner in der Dateiliste",
                "Vecākmape failu sarakstā",
            ),
            (
                "Enter a name for the panel and tab.",
                "Bitte Namen für Panel und Tab eingeben.",
                "Ievadiet paneļa un cilnes nosaukumu.",
            ),
            (
                "Choose an existing home folder.",
                "Bitte einen vorhandenen Startordner wählen.",
                "Izvēlieties esošu sākuma mapi.",
            ),
            ("Your panels", "Deine Panels", "Tavi paneļi"),
            ("Parent folder", "Übergeordneter Ordner", "Vecākmape"),
            ("Home folder", "Startordner", "Sākuma mape"),
            ("Add tab", "Tab hinzufügen", "Pievienot cilni"),
            (
                "Collapse / expand",
                "Einklappen / ausklappen",
                "Sakļaut / izvērst",
            ),
            ("panels", "Panels", "paneļi"),
            (
                "Search files and folders…",
                "Dateien und Ordner suchen …",
                "Meklēt failus un mapes…",
            ),
            ("{count} items", "{count} Elemente", "{count} vienumi"),
            (
                "{count} pinned files",
                "{count} angeheftete Dateien",
                "{count} piesprausti faili",
            ),
            (
                "{count} panels · Double-click to open",
                "{count} Panels · Mit Doppelklick öffnen",
                "{count} paneļi · Atvērt ar dubultklikšķi",
            ),
            (
                "Updating search…",
                "Suche wird vorbereitet …",
                "Meklēšana tiek sagatavota…",
            ),
            ("Search ready", "Suche bereit", "Meklēšana gatava"),
            ("Pinned files", "Angeheftete Dateien", "Piesprausti faili"),
            ("No results", "Keine Treffer", "Nav rezultātu"),
            (
                "Try a different search term.",
                "Versuche einen anderen Suchbegriff.",
                "Mēģini citu meklēšanas vārdu.",
            ),
            (
                "Make room for what matters.",
                "Platz für das Wesentliche.",
                "Vieta svarīgākajam.",
            ),
            (
                "Create a panel to keep your files close.",
                "Erstelle ein Panel für deine wichtigsten Dateien.",
                "Izveido paneli saviem failiem.",
            ),
            (
                "Recycle Bin is empty",
                "Der Papierkorb ist leer",
                "Atkritne ir tukša",
            ),
            (
                "Deleted files will appear here.",
                "Gelöschte Dateien erscheinen hier.",
                "Šeit parādīsies dzēstie faili.",
            ),
            ("No photos yet", "Noch keine Bilder", "Vēl nav attēlu"),
            (
                "Add images to this folder to see them here.",
                "Füge Bilder hinzu, um sie hier zu sehen.",
                "Pievieno attēlus šai mapei.",
            ),
            (
                "Your favorites, together",
                "Deine Favoriten an einem Ort",
                "Tavi favorīti vienuviet",
            ),
            (
                "Drop files or folders here to pin them.",
                "Dateien oder Ordner zum Anheften hierher ziehen.",
                "Ievelc šeit failus vai mapes.",
            ),
            (
                "This folder is empty",
                "Dieser Ordner ist leer",
                "Šī mape ir tukša",
            ),
            (
                "Drop files or folders here to add them.",
                "Dateien oder Ordner hierher ziehen.",
                "Ievelc šeit failus vai mapes.",
            ),
            ("Folder", "Ordner", "Mape"),
            ("File", "Datei", "Fails"),
            ("Deleted item", "Gelöschtes Element", "Dzēsts vienums"),
            ("Dimensions", "Abmessungen", "Izmēri"),
            ("Recycle Bin", "Papierkorb", "Atkritne"),
            ("Empty folder…", "Ordner leeren…", "Iztukšot mapi…"),
            ("Empty folder?", "Ordner leeren?", "Iztukšot mapi?"),
            (
                "Clear pinned list…",
                "Dateiliste leeren…",
                "Notīrīt piesprausto sarakstu…",
            ),
            (
                "Clear pinned list?",
                "Dateiliste leeren?",
                "Notīrīt piesprausto sarakstu?",
            ),
            ("Clear", "Leeren", "Notīrīt"),
            (
                "No folder to empty.",
                "Kein Ordner zum Leeren vorhanden.",
                "Nav mapes, ko iztukšot.",
            ),
            (
                "Move all {count} items, including hidden items and subfolders, to the Recycle Bin.",
                "Alle {count} Elemente, einschließlich versteckter Elemente und Unterordner, in den Papierkorb verschieben.",
                "Pārvietot visus {count} vienumus, tostarp slēptos vienumus un apakšmapes, uz atkritni.",
            ),
            (
                "Remove {count} references from this list. The original files are kept.",
                "{count} Verweise aus dieser Liste entfernen. Die Originaldateien bleiben erhalten.",
                "Noņemt {count} atsauces no šī saraksta. Sākotnējie faili tiks saglabāti.",
            ),
            ("Auto-sort", "Auto-Sortierung", "Automātiskā kārtošana"),
            (
                "Your desktop, organized.",
                "Dein Desktop, organisiert.",
                "Sakārtota darbvirsma.",
            ),
            ("+ Folder panel", "+ Ordner-Panel", "+ Mapes panelis"),
            ("+ Pinned list", "+ Dateiliste", "+ Piesprausta liste"),
            ("Show / hide", "Zeigen / verbergen", "Rādīt / paslēpt"),
            ("Settings", "Einstellungen", "Iestatījumi"),
            ("Folder / source", "Ordner / Quelle", "Mape / avots"),
            ("Visible", "Sichtbar", "Redzams"),
            ("Hidden", "Verborgen", "Paslēpts"),
            ("View", "Ansicht", "Skats"),
            ("Options", "Optionen", "Opcijas"),
            ("Home", "Start", "Sākums"),
            (
                "Search this folder and subfolders…",
                "Diesen Ordner und Unterordner durchsuchen…",
                "Meklēt mapē un apakšmapēs…",
            ),
            ("Loading…", "Wird geladen…", "Notiek ielāde…"),
            ("Save", "Speichern", "Saglabāt"),
            ("Cancel", "Abbrechen", "Atcelt"),
            ("Show in Explorer", "Im Explorer anzeigen", "Rādīt pārlūkā"),
            (
                "New folder panel",
                "Neues Ordner-Panel",
                "Jauns mapes panelis",
            ),
            (
                "New pinned list",
                "Neue Dateiliste",
                "Jauna piesprausta liste",
            ),
            (
                "New Recycle Bin panel",
                "Neues Papierkorb-Panel",
                "Jauns atkritnes panelis",
            ),
            (
                "Windows context menu…",
                "Windows-Kontextmenü…",
                "Windows kontekstizvēlne…",
            ),
            (
                "Empty Recycle Bin…",
                "Papierkorb leeren…",
                "Iztukšot atkritni…",
            ),
            (
                "Move tab left",
                "Tab nach links",
                "Pārvietot cilni pa kreisi",
            ),
            (
                "Move tab right",
                "Tab nach rechts",
                "Pārvietot cilni pa labi",
            ),
            (
                "Merge into another panel",
                "Mit anderem Panel verbinden",
                "Apvienot ar citu paneli",
            ),
            (
                "Panel settings",
                "Panel-Einstellungen",
                "Paneļa iestatījumi",
            ),
            ("Home folder", "Startordner", "Sākuma mape"),
            (
                "General settings",
                "Allgemeine Einstellungen",
                "Vispārīgie iestatījumi",
            ),
            (
                "When closing the main window",
                "Beim Schließen des Hauptfensters",
                "Aizverot galveno logu",
            ),
            (
                "Minimize to tray",
                "In den Infobereich minimieren",
                "Minimizēt sistēmas teknē",
            ),
            (
                "Open settings folder",
                "Einstellungsordner öffnen",
                "Atvērt iestatījumu mapi",
            ),
            (
                "Save current layout…",
                "Aktuelles Layout speichern…",
                "Saglabāt pašreizējo izkārtojumu…",
            ),
            (
                "Restore this backup",
                "Dieses Backup wiederherstellen",
                "Atjaunot šo dublējumu",
            ),
            (
                "Delete this backup",
                "Dieses Backup löschen",
                "Dzēst šo dublējumu",
            ),
            (
                "Import language JSON…",
                "Sprach-JSON importieren…",
                "Importēt valodas JSON…",
            ),
            ("Show panel", "Panel anzeigen", "Rādīt paneli"),
            ("Hide panel", "Panel verbergen", "Paslēpt paneli"),
            ("Remove panel", "Panel entfernen", "Noņemt paneli"),
            (
                "Open folders in Explorer",
                "Ordner im Explorer öffnen",
                "Atvērt mapes pārlūkā",
            ),
            (
                "Open items with one click",
                "Elemente mit einem Klick öffnen",
                "Atvērt vienumus ar vienu klikšķi",
            ),
        ] {
            phrases.insert(
                source.into(),
                if code == "de" { german } else { latvian }.into(),
            );
        }
    }
    CURRENT.with(|current| *current.borrow_mut() = phrases);
}
pub fn text(source: &str) -> String {
    let (label, shortcut) = source
        .split_once('\t')
        .map_or((source, None), |(label, shortcut)| (label, Some(shortcut)));
    let translated = CURRENT
        .with(|current| current.borrow().get(label).cloned())
        .unwrap_or_else(|| label.to_owned());
    if let Some(shortcut) = shortcut {
        format!("{translated}\t{shortcut}")
    } else {
        translated
    }
}
pub fn read(path: &Path) -> Result<(String, String, serde_json::Map<String, Value>), String> {
    if std::fs::metadata(path).map_err(|e| e.to_string())?.len() > 4 * 1024 * 1024 {
        return Err("Language file is too large.".into());
    }
    let source = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
    let value: Value =
        serde_json::from_str(source.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
    let resources = value
        .get("resources")
        .and_then(Value::as_object)
        .and_then(|map| map.iter().next());
    let code = value
        .get("code")
        .and_then(Value::as_str)
        .map(str::to_owned)
        .or_else(|| resources.map(|(code, _)| code.to_owned()))
        .or_else(|| {
            path.file_stem()
                .map(|name| name.to_string_lossy().into_owned())
        })
        .unwrap_or_default();
    if code.is_empty()
        || code.len() > 32
        || !code
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '-' || ch == '_')
    {
        return Err("Invalid language code.".into());
    }
    let name = value
        .get("name")
        .and_then(Value::as_str)
        .unwrap_or(&code)
        .to_owned();
    let source = resources
        .map(|(_, value)| value.get("translation").unwrap_or(value))
        .or_else(|| value.get("translations"))
        .unwrap_or(&value);
    let mut translations = serde_json::Map::new();
    if let Some(source) = source.as_object() {
        for (key, value) in source {
            if !value.is_string() {
                continue;
            }
            let key = if key.starts_with("Loc.") {
                key.clone()
            } else {
                format!("Loc.{key}")
            };
            if bundled()["en"].get(&key).is_some() {
                translations.insert(key, value.clone());
            }
        }
    }
    if translations.is_empty() {
        return Err("No supported translations were found.".into());
    }
    Ok((code, name, translations))
}
pub fn languages(directory: &Path) -> Vec<(String, String)> {
    let mut languages = vec![
        ("en".into(), "English".into()),
        ("de".into(), "Deutsch".into()),
        ("lv".into(), "Latviešu".into()),
    ];
    for directory in [
        crate::config::app_data().join("DesktopPlus/Languages"),
        directory.to_owned(),
    ] {
        if let Ok(files) = std::fs::read_dir(directory) {
            for file in files.filter_map(Result::ok) {
                if file.path().extension().is_some_and(|ext| ext == "json") {
                    if let Ok((code, name, _)) = read(&file.path()) {
                        if !languages.iter().any(|(existing, _)| existing == &code) {
                            languages.push((code, name));
                        }
                    }
                }
            }
        }
    }
    languages
}
