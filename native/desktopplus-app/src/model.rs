use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
mod appearance;
pub use appearance::*;

pub fn new_id() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    format!(
        "{:x}-{:x}-{:x}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Tab {
    pub tab_id: String,
    pub tab_name: String,
    pub panel_type: String,
    pub folder_path: String,
    pub default_folder_path: String,
    pub is_hidden: bool,
    pub show_hidden: bool,
    pub show_file_extensions: bool,
    pub open_folders_externally: bool,
    pub open_items_on_single_click: bool,
    pub view_mode: String,
    pub pinned_items: Vec<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
impl Default for Tab {
    fn default() -> Self {
        Self {
            tab_id: new_id(),
            tab_name: "Folder".into(),
            panel_type: "Folder".into(),
            folder_path: String::new(),
            default_folder_path: String::new(),
            is_hidden: false,
            show_hidden: true,
            show_file_extensions: true,
            open_folders_externally: false,
            open_items_on_single_click: false,
            view_mode: "details".into(),
            pinned_items: Vec::new(),
            extra: Map::new(),
        }
    }
}
impl Tab {
    pub fn folder(path: &Path) -> Self {
        let path_string = path.to_string_lossy().into_owned();
        Self {
            tab_name: path
                .file_name()
                .unwrap_or(path.as_os_str())
                .to_string_lossy()
                .into_owned(),
            folder_path: path_string.clone(),
            default_folder_path: path_string,
            ..Self::default()
        }
    }
    pub fn pinned(&self) -> bool {
        self.panel_type.eq_ignore_ascii_case("List")
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Panel {
    pub panel_id: String,
    pub panel_title: String,
    pub panel_type: String,
    pub folder_path: String,
    pub default_folder_path: String,
    pub left: f64,
    pub top: f64,
    pub width: f64,
    pub height: f64,
    pub expanded_height: f64,
    pub zoom: f64,
    pub is_collapsed: bool,
    pub is_hidden: bool,
    pub show_hidden: bool,
    pub show_file_extensions: bool,
    pub open_folders_externally: bool,
    pub open_items_on_single_click: bool,
    pub view_mode: String,
    pub pinned_items: Vec<String>,
    pub tabs: Option<Vec<Tab>>,
    pub active_tab_index: usize,
    pub preset_name: String,
    pub header_style: Option<HeaderStyle>,
    pub appearance_overrides: AppearanceOverrides,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
impl Default for Panel {
    fn default() -> Self {
        Self {
            panel_id: new_id(),
            panel_title: "DesktopPlus".into(),
            panel_type: "Folder".into(),
            folder_path: String::new(),
            default_folder_path: String::new(),
            left: 100.0,
            top: 100.0,
            width: 440.0,
            height: 400.0,
            expanded_height: 400.0,
            zoom: 1.0,
            is_collapsed: false,
            is_hidden: false,
            show_hidden: true,
            show_file_extensions: true,
            open_folders_externally: false,
            open_items_on_single_click: false,
            view_mode: "details".into(),
            pinned_items: Vec::new(),
            tabs: None,
            active_tab_index: 0,
            preset_name: String::new(),
            header_style: None,
            appearance_overrides: AppearanceOverrides::default(),
            extra: Map::new(),
        }
    }
}
impl Panel {
    pub fn folder(path: &Path) -> Self {
        let tab = Tab::folder(path);
        Self {
            panel_title: tab.tab_name.clone(),
            folder_path: tab.folder_path.clone(),
            default_folder_path: tab.default_folder_path.clone(),
            tabs: Some(vec![tab]),
            ..Self::default()
        }
    }
    pub fn normalize(&mut self) {
        if self.panel_id.is_empty() {
            self.panel_id = new_id();
        }
        if !self.width.is_finite() || self.width < 220.0 {
            self.width = 440.0;
        }
        if !self.height.is_finite() || self.height < if self.is_collapsed { 32.0 } else { 80.0 } {
            self.height = if self.is_collapsed { 48.0 } else { 400.0 };
        }
        if !self.expanded_height.is_finite() || self.expanded_height < 160.0 {
            self.expanded_height = self.height.max(400.0);
        }
        if !self.left.is_finite() {
            self.left = 100.0;
        }
        if !self.top.is_finite() {
            self.top = 100.0;
        }
        self.zoom = if self.zoom.is_finite() {
            self.zoom.clamp(0.7, 2.0)
        } else {
            1.0
        };
        self.width = self.width.min(10000.0);
        self.height = self.height.min(10000.0);
        if self.tabs.as_ref().is_none_or(Vec::is_empty) {
            self.tabs = Some(vec![Tab {
                tab_name: self.panel_title.clone(),
                panel_type: self.panel_type.clone(),
                folder_path: self.folder_path.clone(),
                default_folder_path: self.default_folder_path.clone(),
                show_hidden: self.show_hidden,
                show_file_extensions: self.show_file_extensions,
                open_folders_externally: self.open_folders_externally,
                open_items_on_single_click: self.open_items_on_single_click,
                view_mode: self.view_mode.clone(),
                pinned_items: self.pinned_items.clone(),
                extra: self.extra.clone(),
                ..Tab::default()
            }]);
        }
        let tabs = self.tabs.as_mut().unwrap();
        for tab in tabs.iter_mut() {
            if tab.tab_id.is_empty() {
                tab.tab_id = new_id();
            }
            for path in [&mut tab.folder_path, &mut tab.default_folder_path] {
                if !path.is_empty() && tab.panel_type.eq_ignore_ascii_case("Folder") {
                    if let Ok(absolute) = std::path::absolute(&*path) {
                        *path = absolute.to_string_lossy().replace('/', "\\");
                    }
                }
            }
        }
        self.active_tab_index = self.active_tab_index.min(tabs.len() - 1);
    }
    pub fn active(&self) -> &Tab {
        &self.tabs.as_ref().unwrap()[self.active_tab_index]
    }
    pub fn active_mut(&mut self) -> &mut Tab {
        &mut self.tabs.as_mut().unwrap()[self.active_tab_index]
    }
    pub fn synchronize(&mut self) {
        let tab = self.active().clone();
        self.folder_path = tab.folder_path;
        self.default_folder_path = tab.default_folder_path;
        self.panel_type = tab.panel_type;
        self.pinned_items = tab.pinned_items;
        self.show_hidden = tab.show_hidden;
        self.show_file_extensions = tab.show_file_extensions;
        self.open_folders_externally = tab.open_folders_externally;
        self.open_items_on_single_click = tab.open_items_on_single_click;
        self.view_mode = tab.view_mode;
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum HeaderStyle {
    Pills,
    Segmented,
    TwoRows,
    Floating,
    #[default]
    #[serde(other)]
    Line,
}
impl HeaderStyle {
    pub const ALL: [Self; 5] = [
        Self::Line,
        Self::Pills,
        Self::Segmented,
        Self::TwoRows,
        Self::Floating,
    ];
    pub fn caption(self) -> &'static str {
        match self {
            Self::Line => "Clear line",
            Self::Pills => "Compact pills",
            Self::Segmented => "Centered switch",
            Self::TwoRows => "Two rows",
            Self::Floating => "Floating center",
        }
    }
    pub fn index(self) -> usize {
        Self::ALL
            .iter()
            .position(|style| *style == self)
            .unwrap_or(0)
    }
    pub fn from_index(index: usize) -> Self {
        Self::ALL.get(index).copied().unwrap_or_default()
    }
    pub fn captions() -> Vec<String> {
        Self::ALL
            .iter()
            .map(|style| style.caption().into())
            .collect()
    }
    pub fn panel_captions() -> Vec<String> {
        std::iter::once("Inherit from theme".into())
            .chain(Self::captions())
            .collect()
    }
    pub fn height(self, collapsed: bool) -> i32 {
        if collapsed {
            return 48;
        }
        match self {
            Self::TwoRows => 88,
            Self::Floating => 64,
            _ => 48,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Theme {
    pub header_style: HeaderStyle,
    pub header_alignment: HeaderAlignment,
    pub glass_mode: GlassMode,
    pub glass_opacity: u8,
    pub rounded_corners: bool,
    pub header_controls: HeaderControls,
    pub background_color: String,
    pub header_color: String,
    pub accent_color: String,
    pub text_color: String,
    pub muted_text_color: String,
    pub font_family: String,
    pub item_font_size: f64,
    pub title_font_size: f64,
    pub background_opacity: f64,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
impl Default for Theme {
    fn default() -> Self {
        Self {
            header_style: HeaderStyle::default(),
            header_alignment: HeaderAlignment::default(),
            glass_mode: GlassMode::default(),
            glass_opacity: 72,
            rounded_corners: true,
            header_controls: HeaderControls::default(),
            background_color: "#11151F".into(),
            header_color: "#1B2231".into(),
            accent_color: "#A5B4FC".into(),
            text_color: "#EFF2FA".into(),
            muted_text_color: "#9CA9BD".into(),
            font_family: "Segoe UI Variable Text".into(),
            item_font_size: 15.0,
            title_font_size: 16.0,
            background_opacity: 1.0,
            extra: Map::new(),
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Layout {
    pub name: String,
    pub panels: Vec<Panel>,
    pub appearance: Theme,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct SortRule {
    pub rule_id: String,
    pub enabled: bool,
    pub is_built_in: bool,
    pub match_folders: bool,
    pub catch_all: bool,
    pub rule_name: String,
    pub target_panel_name: String,
    pub extensions: Vec<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
impl Default for SortRule {
    fn default() -> Self {
        Self {
            rule_id: new_id(),
            enabled: true,
            is_built_in: false,
            match_folders: false,
            catch_all: false,
            rule_name: String::new(),
            target_panel_name: String::new(),
            extensions: Vec::new(),
            extra: Map::new(),
        }
    }
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct AutoSort {
    pub auto_sort_enabled: bool,
    pub rules: Vec<SortRule>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Shortcuts {
    pub hide_panels_hotkey: String,
    pub foreground_panels_hotkey: String,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
impl Default for Shortcuts {
    fn default() -> Self {
        Self {
            hide_panels_hotkey: "Ctrl + Alt + H".into(),
            foreground_panels_hotkey: "Ctrl + Alt + F".into(),
            extra: Map::new(),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Settings {
    pub windows: Vec<Panel>,
    pub appearance: Theme,
    pub layouts: Vec<Layout>,
    pub presets: Vec<Value>,
    pub language: String,
    pub start_with_windows: bool,
    pub auto_check_updates: bool,
    pub close_behavior: String,
    pub desktop_auto_sort: AutoSort,
    pub global_shortcuts: Shortcuts,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            windows: Vec::new(),
            appearance: Theme::default(),
            layouts: Vec::new(),
            presets: Vec::new(),
            language: "en".into(),
            start_with_windows: false,
            auto_check_updates: false,
            close_behavior: "Minimize".into(),
            desktop_auto_sort: AutoSort::default(),
            global_shortcuts: Shortcuts::default(),
            extra: Map::new(),
        }
    }
}
impl Settings {
    pub fn normalize(&mut self) {
        let mut ids = std::collections::HashSet::new();
        for panel in &mut self.windows {
            panel.normalize();
            if !ids.insert(panel.panel_id.clone()) {
                panel.panel_id = new_id();
                ids.insert(panel.panel_id.clone());
            }
        }
        for layout in &mut self.layouts {
            for panel in &mut layout.panels {
                panel.normalize();
            }
        }
        if self.desktop_auto_sort.rules.is_empty() {
            self.desktop_auto_sort.rules = default_rules();
        }
    }
}

pub fn default_rules() -> Vec<SortRule> {
    [
        ("Folders", ""),
        ("Images", "png jpg jpeg gif bmp webp tif tiff svg heic"),
        ("Videos", "mp4 mkv mov avi wmv webm"),
        ("Audio", "mp3 wav flac ogg aac m4a"),
        (
            "Documents",
            "pdf doc docx xls xlsx ppt pptx txt rtf odt csv",
        ),
        ("Archives", "zip 7z rar tar gz"),
        ("Installers", "exe msi msix appx bat cmd"),
        ("Shortcuts", "lnk url"),
        (
            "Code",
            "rs cs js ts tsx jsx py java cpp c h hpp json xml yaml yml html css ps1 sql",
        ),
        ("Other", ""),
    ]
    .into_iter()
    .enumerate()
    .map(|(i, (name, ext))| SortRule {
        rule_id: format!("builtin-{i}"),
        rule_name: name.into(),
        target_panel_name: name.into(),
        is_built_in: true,
        match_folders: i == 0,
        catch_all: i == 9,
        extensions: ext
            .split_whitespace()
            .map(|ext| format!(".{ext}"))
            .collect(),
        ..SortRule::default()
    })
    .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn header_styles_default_and_roundtrip_with_saved_layouts() {
        let legacy = crate::config::parse(r#"{"Windows":[{"PanelTitle":"Old panel"}]}"#).unwrap();
        assert_eq!(legacy.appearance.header_style, HeaderStyle::Line);
        assert_eq!(legacy.windows[0].header_style, None);
        for style in HeaderStyle::ALL {
            let mut settings = Settings::default();
            settings.appearance.header_style = style;
            settings.windows = vec![
                Panel {
                    header_style: Some(style),
                    ..Panel::default()
                },
                Panel::default(),
            ];
            settings.layouts.push(Layout {
                name: "Saved".into(),
                panels: settings.windows.clone(),
                appearance: settings.appearance.clone(),
                ..Layout::default()
            });
            let saved = serde_json::to_string(&settings).unwrap();
            let restored = crate::config::parse(&saved).unwrap();
            assert_eq!(restored.appearance.header_style, style);
            assert_eq!(restored.windows[0].header_style, Some(style));
            assert_eq!(restored.windows[1].header_style, None);
            assert_eq!(restored.layouts[0].appearance.header_style, style);
            assert_eq!(restored.layouts[0].panels[0].header_style, Some(style));
        }
        let imported = crate::config::parse(
            r#"{"appearance":{"headerstyle":"pills"},"windows":[{"headerStyle":"floating"}]}"#,
        )
        .unwrap();
        assert_eq!(imported.appearance.header_style, HeaderStyle::Pills);
        assert_eq!(
            imported.windows[0].header_style,
            Some(HeaderStyle::Floating)
        );
        let unknown =
            crate::config::parse(r#"{"Appearance":{"HeaderStyle":"future-style"}}"#).unwrap();
        assert_eq!(unknown.appearance.header_style, HeaderStyle::Line);
    }
    #[test]
    fn preserves_collapsed_geometry_and_expanded_height() {
        let mut settings: Settings = serde_json::from_str(r#"{"Windows":[{"PanelTitle":"Bottom","Width":249,"Height":48,"Top":1344,"IsCollapsed":true,"ExpandedHeight":354}]}"#).unwrap();
        settings.normalize();
        let panel = &settings.windows[0];
        assert_eq!(
            (panel.width, panel.height, panel.top, panel.expanded_height),
            (249.0, 48.0, 1344.0, 354.0)
        );
    }
    #[test]
    fn imports_legacy_panels_tabs_and_preserves_unimplemented_settings() {
        let mut settings: Settings = serde_json::from_str(r##"{"Windows":[{"PanelTitle":"Work","FolderPath":"C:\\Work","Width":0,"Height":0,"Tabs":null,"MetadataWidths":{"name":250}}],"Language":"de","Appearance":{"Pattern":"Dots","BackgroundColor":"#123456"},"AutoCheckUpdates":true}"##).unwrap();
        settings.normalize();
        assert_eq!(settings.windows[0].active().folder_path, r"C:\Work");
        assert_eq!(settings.windows[0].width, 440.0);
        let saved = serde_json::to_value(&settings).unwrap();
        assert_eq!(saved["Windows"][0]["MetadataWidths"]["name"], 250);
        assert_eq!(saved["Appearance"]["Pattern"], "Dots");
        assert_eq!(saved["AutoCheckUpdates"], true);
    }
}
