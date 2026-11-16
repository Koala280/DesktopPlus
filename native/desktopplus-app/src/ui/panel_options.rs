use super::*;
use crate::model::{HeaderAlignment, HeaderStyle, Panel, Settings, Theme};
use form::{Input, Page};
use serde_json::Value;
#[derive(Clone, Debug, PartialEq)]
pub struct Options {
    pub header_style: HeaderStyle,
    pub hover: bool,
    pub movement: String,
    pub collapse: String,
    pub search: String,
    pub search_expanded: bool,
    pub settings: String,
    pub alignment: String,
    pub close: bool,
    pub empty_button: bool,
    pub parent: String,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            header_style: HeaderStyle::default(),
            hover: false,
            movement: "titlebar".into(),
            collapse: "both".into(),
            search: "hidden".into(),
            search_expanded: true,
            settings: "hidden".into(),
            alignment: "center".into(),
            close: true,
            empty_button: false,
            parent: "header".into(),
        }
    }
}
impl Options {
    pub fn resolved(panel: &Panel, theme: &Theme) -> Self {
        let mut result = Self::read(panel);
        result.header_style = panel.header_style.unwrap_or(theme.header_style);
        result.alignment = alignment_override(panel)
            .unwrap_or(theme.header_alignment)
            .key()
            .into();
        let custom = &panel.appearance_overrides;
        let controls = &theme.header_controls;
        let has = |keys: &[&str]| {
            keys.iter()
                .any(|k| layout_builder::legacy_value(panel, k).is_some())
        };
        macro_rules! resolve {
            ($field:ident, $($key:literal),+) => {
                if let Some(value) = &custom.$field { result.$field = value.clone(); }
                else if !has(&[$($key),+]) { result.$field = controls.$field.clone(); }
            };
        }
        resolve!(search, "SearchVisibilityMode");
        resolve!(
            search_expanded,
            "SearchVisibleOnlyExpanded",
            "SearchVisibilityMode"
        );
        resolve!(
            settings,
            "SettingsButtonVisibilityMode",
            "ShowSettingsButton"
        );
        resolve!(close, "ShowCloseButton");
        resolve!(
            empty_button,
            "ShowEmptyPanelButton",
            "ShowEmptyRecycleBinButton"
        );
        resolve!(
            parent,
            "IconViewParentNavigationMode",
            "ShowParentNavigationItem"
        );
        result
    }
    pub fn read(panel: &Panel) -> Self {
        fn find<'a>(map: &'a serde_json::Map<String, Value>, key: &str) -> Option<&'a Value> {
            map.get(key).or_else(|| {
                map.iter()
                    .find(|(k, _)| k.eq_ignore_ascii_case(key))
                    .map(|(_, v)| v)
            })
        }
        let get = |key: &str| find(&panel.active().extra, key).or_else(|| find(&panel.extra, key));
        let boolean = |key, default| get(key).and_then(Value::as_bool).unwrap_or(default);
        let select = |key, allowed: &[&str], default: &str| {
            get(key)
                .and_then(Value::as_str)
                .map(str::to_ascii_lowercase)
                .filter(|v| allowed.contains(&v.as_str()))
                .unwrap_or_else(|| default.into())
        };
        let search = select(
            "SearchVisibilityMode",
            &[
                "field",
                "always",
                "expandedonly",
                "expanded",
                "button",
                "hidden",
            ],
            "hidden",
        );
        let parent = select(
            "IconViewParentNavigationMode",
            &[
                "header",
                "item",
                "none",
                "details-header",
                "details-item",
                "details-none",
            ],
            match get("ShowParentNavigationItem").and_then(Value::as_bool) {
                Some(true) => "item",
                Some(false) => "none",
                None => "header",
            },
        );
        let mut result = Self {
            header_style: panel.header_style.unwrap_or_default(),
            hover: boolean("ExpandOnHover", false),
            movement: select(
                "MovementMode",
                &["titlebar", "button", "locked"],
                "titlebar",
            ),
            collapse: select(
                "CollapseBehavior",
                &["doubleclick", "button", "both"],
                "both",
            ),
            search: match search.as_str() {
                "field" | "expandedonly" => "always".into(),
                "expanded" => "button".into(),
                _ => search.clone(),
            },
            search_expanded: boolean(
                "SearchVisibleOnlyExpanded",
                search != "field" && search != "always",
            ),
            settings: select(
                "SettingsButtonVisibilityMode",
                &["always", "expandedonly", "hidden"],
                if boolean("ShowSettingsButton", false) {
                    "always"
                } else {
                    "hidden"
                },
            ),
            alignment: select(
                "HeaderContentAlignment",
                &["left", "center", "right"],
                "center",
            ),
            close: boolean("ShowCloseButton", true),
            empty_button: boolean(
                "ShowEmptyPanelButton",
                panel.active().panel_type.eq_ignore_ascii_case("RecycleBin")
                    && boolean("ShowEmptyRecycleBinButton", false),
            ),
            parent: parent.trim_start_matches("details-").into(),
        };
        let custom = &panel.appearance_overrides;
        macro_rules! apply { ($($key:ident),*) => { $(if let Some(value) = &custom.$key { result.$key = value.clone(); })* }; }
        apply!(
            search,
            search_expanded,
            settings,
            close,
            empty_button,
            parent
        );
        if let Some(alignment) = custom.header_alignment {
            result.alignment = alignment.key().into();
        }
        result
    }
    pub fn write(&self, panel: &mut Panel) {
        let entries = [
            ("ExpandOnHover", Value::Bool(self.hover)),
            ("MovementMode", Value::String(self.movement.clone())),
            ("CollapseBehavior", Value::String(self.collapse.clone())),
            ("SearchVisibilityMode", Value::String(self.search.clone())),
            (
                "SearchVisibleOnlyExpanded",
                Value::Bool(self.search_expanded),
            ),
            (
                "SettingsButtonVisibilityMode",
                Value::String(self.settings.clone()),
            ),
            ("ShowSettingsButton", Value::Bool(self.settings != "hidden")),
            (
                "HeaderContentAlignment",
                Value::String(self.alignment.clone()),
            ),
            ("ShowCloseButton", Value::Bool(self.close)),
            ("ShowEmptyPanelButton", Value::Bool(self.empty_button)),
            (
                "IconViewParentNavigationMode",
                Value::String(self.parent.clone()),
            ),
            (
                "ShowParentNavigationItem",
                Value::Bool(self.parent == "item"),
            ),
        ];
        for (key, value) in entries {
            panel.extra.insert(key.into(), value.clone());
            panel.active_mut().extra.insert(key.into(), value);
        }
        if panel.active().panel_type.eq_ignore_ascii_case("RecycleBin") {
            panel.extra.insert(
                "ShowEmptyRecycleBinButton".into(),
                Value::Bool(self.empty_button),
            );
            panel.active_mut().extra.insert(
                "ShowEmptyRecycleBinButton".into(),
                Value::Bool(self.empty_button),
            );
        }
    }
}
pub fn alignment_override(panel: &Panel) -> Option<HeaderAlignment> {
    panel.appearance_overrides.header_alignment.or_else(|| {
        let value = layout_builder::legacy_value(panel, "HeaderContentAlignment")?.as_str()?;
        HeaderAlignment::ALL
            .into_iter()
            .find(|alignment| alignment.key().eq_ignore_ascii_case(value))
    })
}
pub fn set_alignment(panel: &mut Panel, alignment: Option<HeaderAlignment>) {
    panel.appearance_overrides.header_alignment = alignment;
    panel
        .extra
        .retain(|key, _| !key.eq_ignore_ascii_case("HeaderContentAlignment"));
    if let Some(tabs) = &mut panel.tabs {
        for tab in tabs {
            tab.extra
                .retain(|key, _| !key.eq_ignore_ascii_case("HeaderContentAlignment"));
        }
    }
}
pub const FIT_WIDTH: u16 = 250;
pub const FIT_HEIGHT: u16 = 251;
pub struct Editor {
    pub pages: Vec<Page>,
    keys: HashMap<&'static str, usize>,
    presets: Vec<String>,
    folder: bool,
    original_options: Options,
}
impl Editor {
    pub fn new(panel: &Panel, settings: &Settings) -> Self {
        let tab = panel.active();
        let options = Options::resolved(panel, &settings.appearance);
        let mut editor = Self {
            pages: Vec::new(),
            keys: HashMap::new(),
            presets: vec![String::new()],
            folder: tab.panel_type == "Folder",
            original_options: options.clone(),
        };
        for preset in &settings.presets {
            if let Some(name) = preset.get("Name").and_then(Value::as_str) {
                editor.presets.push(name.into());
            }
        }
        if !panel.preset_name.is_empty() && !editor.presets.contains(&panel.preset_name) {
            editor.presets.push(panel.preset_name.clone());
        }
        let choice = |label: &str, entries: &[(&str, &str)], value: &str| {
            Input::Choice(
                label.into(),
                entries
                    .iter()
                    .map(|(_, caption)| caption.to_string())
                    .collect(),
                entries
                    .iter()
                    .position(|(key, _)| *key == value)
                    .unwrap_or(0),
            )
        };
        let mut fields = vec![
            (
                "panel_title",
                Input::Text("Panel title".into(), panel.panel_title.clone()),
            ),
            ("tab", Input::Text("Tab name".into(), tab.tab_name.clone())),
        ];
        if editor.folder {
            fields.push((
                "folder",
                Input::Folder(
                    "Home folder".into(),
                    if tab.default_folder_path.is_empty() {
                        tab.folder_path.clone()
                    } else {
                        tab.default_folder_path.clone()
                    },
                ),
            ));
        }
        fields.extend([
            (
                "folders",
                choice(
                    "Open folders",
                    &[
                        ("internal", "Inside the panel"),
                        ("external", "In File Explorer"),
                    ],
                    if tab.open_folders_externally {
                        "external"
                    } else {
                        "internal"
                    },
                ),
            ),
            (
                "click",
                choice(
                    "Open files",
                    &[("double", "Double-click"), ("single", "Single-click")],
                    if tab.open_items_on_single_click {
                        "single"
                    } else {
                        "double"
                    },
                ),
            ),
            (
                "hidden",
                Input::Check("Show hidden items".into(), tab.show_hidden),
            ),
            (
                "extensions",
                Input::Check("Show file extensions".into(), tab.show_file_extensions),
            ),
        ]);
        editor.page("General", fields);
        let mut names = editor.presets.clone();
        names[0] = "Use global appearance".into();
        editor.page(
            "View",
            vec![
                (
                    "view",
                    choice(
                        "View mode",
                        &[
                            ("icons", "Icons"),
                            ("details", "Details"),
                            ("photos", "Photos"),
                        ],
                        &tab.view_mode,
                    ),
                ),
                (
                    "preset",
                    Input::Choice(
                        "Appearance preset".into(),
                        names,
                        editor
                            .presets
                            .iter()
                            .position(|name| name == &panel.preset_name)
                            .unwrap_or(0),
                    ),
                ),
                (
                    "fit_width",
                    Input::Action("Fit width to contents".into(), FIT_WIDTH),
                ),
                (
                    "fit_height",
                    Input::Action("Fit height to contents".into(), FIT_HEIGHT),
                ),
                (
                    "panel_appearance",
                    Input::Action("Panel appearance…".into(), LAYOUT_BUILDER),
                ),
                ("new_tab", Input::Action("Add tab".into(), ADD_TAB)),
                (
                    "manage_tabs",
                    Input::Action("Manage tabs".into(), MANAGE_TABS),
                ),
                (
                    "new_panel",
                    Input::Action("New folder panel".into(), NEW_FOLDER),
                ),
            ],
        );
        editor.page(
            "Header",
            vec![
                (
                    "header_style",
                    Input::Choice(
                        "Panel bar style".into(),
                        HeaderStyle::panel_captions(),
                        panel.header_style.map_or(0, |style| style.index() + 1),
                    ),
                ),
                (
                    "alignment",
                    Input::Choice(
                        "Title and tab alignment".into(),
                        std::iter::once("Inherit from theme".into())
                            .chain(HeaderAlignment::captions())
                            .collect(),
                        alignment_override(panel).map_or(0, |v| v.index() + 1),
                    ),
                ),
                (
                    "search",
                    choice(
                        "Search in the header",
                        &[
                            ("button", "Search button"),
                            ("always", "Search field"),
                            ("hidden", "Hidden"),
                        ],
                        &options.search,
                    ),
                ),
                (
                    "search_expanded",
                    Input::Check("Search only when expanded".into(), options.search_expanded),
                ),
                (
                    "settings",
                    choice(
                        "Settings button",
                        &[
                            ("always", "Always visible"),
                            ("expandedonly", "Only when expanded"),
                            ("hidden", "Hidden"),
                        ],
                        &options.settings,
                    ),
                ),
                (
                    "close",
                    Input::Check("Show close button".into(), options.close),
                ),
                (
                    "empty_button",
                    Input::Check("Show clear button".into(), options.empty_button),
                ),
            ],
        );
        editor.page(
            "Behavior",
            vec![
                (
                    "movement",
                    choice(
                        "Move panel",
                        &[
                            ("titlebar", "Drag the header"),
                            ("button", "Drag handle only"),
                            ("locked", "Lock position and size"),
                        ],
                        &options.movement,
                    ),
                ),
                (
                    "collapse",
                    choice(
                        "Collapse panel",
                        &[
                            ("both", "Button and double-click"),
                            ("button", "Button only"),
                            ("doubleclick", "Double-click only"),
                        ],
                        &options.collapse,
                    ),
                ),
                (
                    "hover",
                    Input::Check("Expand on mouse hover".into(), options.hover),
                ),
                (
                    "parent",
                    choice(
                        "Parent folder navigation",
                        &[
                            ("header", "Back button in header"),
                            ("item", "Parent folder in file list"),
                            ("none", "Hidden"),
                        ],
                        &options.parent,
                    ),
                ),
            ],
        );
        let columns = crate::metadata::COLUMNS
            .iter()
            .enumerate()
            .skip(1)
            .map(|(i, (key, label, _))| {
                (
                    *key,
                    Input::Check(
                        label.to_string(),
                        tab.extra
                            .get(&format!("ShowMetadata{label}"))
                            .and_then(Value::as_bool)
                            .unwrap_or(i <= 3),
                    ),
                )
            })
            .collect();
        editor.page("Columns", columns);
        editor
    }
    fn page(&mut self, name: &'static str, fields: Vec<(&'static str, Input)>) {
        let start = self.keys.len();
        for (i, (key, _)) in fields.iter().enumerate() {
            self.keys.insert(key, start + i);
        }
        self.pages.push(Page {
            name,
            fields: fields.into_iter().map(|(_, field)| field).collect(),
        });
    }
    fn get<'a>(&self, values: &'a [String], key: &str) -> &'a str {
        self.keys
            .get(key)
            .and_then(|i| values.get(*i))
            .map(String::as_str)
            .unwrap_or("")
    }
    fn selected<'a>(&self, values: &[String], key: &str, choices: &[&'a str]) -> &'a str {
        choices[self
            .get(values, key)
            .parse::<usize>()
            .unwrap_or(0)
            .min(choices.len() - 1)]
    }
    pub fn validate(&self, values: &[String]) -> Result<(), String> {
        if self.get(values, "panel_title").trim().is_empty()
            || self.get(values, "tab").trim().is_empty()
        {
            return Err("Enter a name for the panel and tab.".into());
        }
        if self.folder && !std::path::Path::new(self.get(values, "folder").trim()).is_dir() {
            return Err("Choose an existing home folder.".into());
        }
        Ok(())
    }
    pub fn apply(&self, panel: &mut Panel, values: &[String]) {
        let previous = panel.clone();
        let style = self
            .get(values, "header_style")
            .parse::<usize>()
            .unwrap_or(0);
        panel.header_style = style
            .checked_sub(1)
            .and_then(|index| HeaderStyle::ALL.get(index).copied());
        panel.panel_title = self.get(values, "panel_title").trim().into();
        let preset = self.get(values, "preset").parse::<usize>().unwrap_or(0);
        panel.preset_name = self.presets.get(preset).cloned().unwrap_or_default();
        let tab = panel.active_mut();
        tab.tab_name = self.get(values, "tab").trim().into();
        if self.folder {
            let path = self.get(values, "folder").trim();
            if tab.default_folder_path != path {
                tab.folder_path = path.into();
            }
            tab.default_folder_path = path.into();
        }
        tab.show_hidden = self.get(values, "hidden") == "1";
        tab.show_file_extensions = self.get(values, "extensions") == "1";
        tab.open_folders_externally = self.get(values, "folders") == "1";
        tab.open_items_on_single_click = self.get(values, "click") == "1";
        tab.view_mode = self
            .selected(values, "view", &["icons", "details", "photos"])
            .into();
        for (key, label, _) in crate::metadata::COLUMNS.iter().skip(1) {
            tab.extra.insert(
                format!("ShowMetadata{label}"),
                Value::Bool(self.get(values, key) == "1"),
            );
        }
        let updated = Options {
            header_style: panel.header_style.unwrap_or_default(),
            hover: self.get(values, "hover") == "1",
            movement: self
                .selected(values, "movement", &["titlebar", "button", "locked"])
                .into(),
            collapse: self
                .selected(values, "collapse", &["both", "button", "doubleclick"])
                .into(),
            search: self
                .selected(values, "search", &["button", "always", "hidden"])
                .into(),
            search_expanded: self.get(values, "search_expanded") == "1",
            settings: self
                .selected(values, "settings", &["always", "expandedonly", "hidden"])
                .into(),
            alignment: self
                .selected(values, "alignment", &["inherit", "left", "center", "right"])
                .into(),
            close: self.get(values, "close") == "1",
            empty_button: self.get(values, "empty_button") == "1",
            parent: self
                .selected(values, "parent", &["header", "item", "none"])
                .into(),
        };
        updated.write(panel);
        layout_builder::copy_legacy(panel, &previous);
        macro_rules! changed { ($($key:ident),*) => { $(if updated.$key != self.original_options.$key {
            panel.appearance_overrides.$key = Some(updated.$key.clone());
        })* }; }
        changed!(
            search,
            search_expanded,
            settings,
            close,
            empty_button,
            parent
        );
        set_alignment(
            panel,
            self.get(values, "alignment")
                .parse::<usize>()
                .ok()
                .and_then(|v| v.checked_sub(1))
                .map(HeaderAlignment::from_index),
        );
        panel.synchronize();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn editing_a_title_keeps_theme_controls_inherited() {
        let mut settings = Settings::default();
        settings.appearance.header_alignment = HeaderAlignment::Right;
        settings.appearance.header_controls.search = "button".into();
        let mut panel = Panel::folder(&std::env::temp_dir());
        let editor = Editor::new(&panel, &settings);
        let mut values = draft(&editor);
        values[editor.keys["panel_title"]] = "New title".into();
        editor.apply(&mut panel, &values);
        settings.appearance.header_alignment = HeaderAlignment::Left;
        settings.appearance.header_controls.search = "always".into();
        let options = Options::resolved(&panel, &settings.appearance);
        assert_eq!(options.alignment, "left");
        assert_eq!(options.search, "always");
        assert!(panel.appearance_overrides.search.is_none());
        assert!(alignment_override(&panel).is_none());
    }
    #[test]
    fn panel_style_inherits_and_stays_with_panel_when_tabs_change() {
        let mut panel = Panel::folder(&std::env::temp_dir());
        panel
            .tabs
            .as_mut()
            .unwrap()
            .push(crate::model::Tab::default());
        let mut settings = Settings::default();
        settings.appearance.header_style = HeaderStyle::Segmented;
        assert_eq!(
            Options::resolved(&panel, &settings.appearance).header_style,
            HeaderStyle::Segmented
        );
        let editor = Editor::new(&panel, &settings);
        let mut values = draft(&editor);
        values[editor.keys["header_style"]] = (HeaderStyle::Floating.index() + 1).to_string();
        editor.apply(&mut panel, &values);
        panel.active_tab_index = 1;
        assert_eq!(
            Options::resolved(&panel, &settings.appearance).header_style,
            HeaderStyle::Floating
        );
        settings.appearance.header_style = HeaderStyle::Pills;
        assert_eq!(
            Options::resolved(&panel, &settings.appearance).header_style,
            HeaderStyle::Floating
        );
        panel.active_tab_index = 0;
        values[editor.keys["header_style"]] = "0".into();
        editor.apply(&mut panel, &values);
        assert_eq!(panel.header_style, None);
        assert_eq!(
            Options::resolved(&panel, &settings.appearance).header_style,
            HeaderStyle::Pills
        );
        assert!(
            panel
                .tabs
                .as_ref()
                .unwrap()
                .iter()
                .all(|tab| !tab.extra.contains_key("HeaderStyle"))
        );
    }
    fn draft(editor: &Editor) -> Vec<String> {
        editor
            .pages
            .iter()
            .flat_map(|page| page.fields.iter())
            .map(|field| match field {
                Input::Text(_, v) | Input::Folder(_, v) => v.clone(),
                Input::Check(_, v) => usize::from(*v).to_string(),
                Input::Choice(_, _, v) => v.to_string(),
                Input::Action(..) => String::new(),
            })
            .collect()
    }
    #[test]
    fn settings_roundtrip_keeps_tab_identity_columns_and_unknown_values() {
        let mut panel = Panel::folder(&std::env::temp_dir());
        assert!(!Options::default().empty_button);
        assert!(!Options::read(&panel).empty_button);
        panel.panel_title = "Work".into();
        panel.active_mut().tab_name = "Documents".into();
        panel
            .active_mut()
            .extra
            .insert("CustomSetting".into(), Value::String("retain".into()));
        let editor = Editor::new(&panel, &Settings::default());
        let mut values = draft(&editor);
        values[editor.keys["hover"]] = "1".into();
        values[editor.keys["empty_button"]] = "0".into();
        values[editor.keys["movement"]] = "2".into();
        values[editor.keys["size"]] = "0".into();
        values[editor.keys["title"]] = "1".into();
        editor.validate(&values).unwrap();
        let id = panel.active().tab_id.clone();
        editor.apply(&mut panel, &values);
        assert_eq!(panel.panel_title, "Work");
        assert_eq!(panel.active().tab_name, "Documents");
        assert_eq!(panel.active().tab_id, id);
        assert_eq!(panel.active().extra["CustomSetting"], "retain");
        assert_eq!(panel.active().extra["ShowMetadataSize"], false);
        assert_eq!(panel.active().extra["ShowMetadataTitle"], true);
        let options = Options::read(&panel);
        assert!(options.hover);
        assert!(!options.empty_button);
        assert_eq!(options.movement, "locked");
        let reloaded: Panel =
            serde_json::from_value(serde_json::to_value(&panel).unwrap()).unwrap();
        assert_eq!(Options::read(&reloaded), options);
        values[editor.keys["empty_button"]] = "1".into();
        editor.apply(&mut panel, &values);
        assert!(Options::read(&panel).empty_button);
    }
    #[test]
    fn rejects_empty_names_and_missing_folder_without_modifying_panel() {
        let panel = Panel::folder(&std::env::temp_dir());
        let original = panel.panel_title.clone();
        let editor = Editor::new(&panel, &Settings::default());
        let mut values = draft(&editor);
        values[editor.keys["panel_title"]] = " ".into();
        assert!(editor.validate(&values).is_err());
        values[editor.keys["panel_title"]] = original.clone();
        values[editor.keys["folder"]] = std::env::temp_dir()
            .join(crate::model::new_id())
            .to_string_lossy()
            .into_owned();
        assert!(editor.validate(&values).is_err());
        assert_eq!(panel.panel_title, original);
    }
    #[test]
    fn imports_legacy_search_and_parent_navigation_modes() {
        let mut panel = Panel::default();
        panel.normalize();
        panel
            .active_mut()
            .extra
            .insert("searchVisibilityMode".into(), Value::String("FIELD".into()));
        panel.extra.insert(
            "IconViewParentNavigationMode".into(),
            Value::String("details-item".into()),
        );
        let options = Options::read(&panel);
        assert_eq!(options.search, "always");
        assert!(!options.search_expanded);
        assert_eq!(options.parent, "item");
        panel.extra.remove("IconViewParentNavigationMode");
        panel
            .extra
            .insert("ShowParentNavigationItem".into(), Value::Bool(false));
        assert_eq!(Options::read(&panel).parent, "none");
        panel
            .extra
            .insert("ShowEmptyRecycleBinButton".into(), Value::Bool(false));
        assert!(!Options::read(&panel).empty_button);
        panel.active_mut().panel_type = "RecycleBin".into();
        assert!(!Options::read(&panel).empty_button);
        panel.extra.remove("ShowEmptyRecycleBinButton");
        assert!(!Options::read(&panel).empty_button);
        panel
            .extra
            .insert("ShowEmptyRecycleBinButton".into(), Value::Bool(true));
        assert!(Options::read(&panel).empty_button);
        panel
            .active_mut()
            .extra
            .insert("ShowEmptyPanelButton".into(), Value::Bool(false));
        assert!(!Options::read(&panel).empty_button);
        panel
            .active_mut()
            .extra
            .insert("ShowEmptyPanelButton".into(), Value::Bool(true));
        assert!(Options::read(&panel).empty_button);
    }
    #[test]
    fn active_tab_options_override_legacy_panel_defaults() {
        let mut panel = Panel::default();
        panel.normalize();
        panel
            .extra
            .insert("MovementMode".into(), Value::String("locked".into()));
        panel
            .active_mut()
            .extra
            .insert("MovementMode".into(), Value::String("button".into()));
        assert_eq!(Options::read(&panel).movement, "button");
        panel
            .extra
            .insert("ShowEmptyPanelButton".into(), Value::Bool(false));
        panel
            .active_mut()
            .extra
            .insert("ShowEmptyPanelButton".into(), Value::Bool(true));
        assert!(Options::read(&panel).empty_button);
    }
}
