//! The theme and each panel use the same editor and validation rules.
use super::*;
use crate::model::{AppearanceOverrides, GlassMode, HeaderAlignment, Panel, Settings};
use form::{Input, Page};

const SEARCH: &[&str] = &["button", "always", "hidden"];
const SETTINGS: &[&str] = &["always", "expandedonly", "hidden"];
const PARENT: &[&str] = &["header", "item", "none"];
pub const LEGACY_KEYS: &[&str] = &[
    "HeaderContentAlignment",
    "SearchVisibilityMode",
    "SearchVisibleOnlyExpanded",
    "SettingsButtonVisibilityMode",
    "ShowSettingsButton",
    "ShowCloseButton",
    "ShowEmptyPanelButton",
    "ShowEmptyRecycleBinButton",
    "IconViewParentNavigationMode",
    "ShowParentNavigationItem",
];

pub struct Editor {
    pub pages: Vec<Page>,
    keys: HashMap<&'static str, usize>,
    panel: bool,
    presets: Vec<String>,
}
impl Editor {
    pub fn new(settings: &Settings, panel: Option<&Panel>) -> Self {
        let theme = &settings.appearance;
        let empty = AppearanceOverrides::default();
        let overrides = panel.map(|p| &p.appearance_overrides).unwrap_or(&empty);
        let mut result = Self {
            pages: Vec::new(),
            keys: HashMap::new(),
            panel: panel.is_some(),
            presets: std::iter::once(String::new())
                .chain(settings.presets.iter().filter_map(|p| {
                    p.get("Name")
                        .and_then(serde_json::Value::as_str)
                        .map(String::from)
                }))
                .collect(),
        };
        if let Some(panel) = panel {
            if !result.presets.contains(&panel.preset_name) {
                result.presets.push(panel.preset_name.clone());
            }
        }
        let choice =
            |label: &str, mut captions: Vec<String>, global: usize, custom: Option<usize>| {
                let selected = if panel.is_some() {
                    captions.insert(0, "Inherit from theme".into());
                    custom.map_or(0, |index| index + 1)
                } else {
                    global
                };
                Input::Choice(label.into(), captions, selected)
            };
        let text = |label: &str, global: String, custom: Option<String>| {
            Input::Text(
                if panel.is_some() {
                    format!(
                        "{} ({})",
                        crate::locale::text(label),
                        crate::locale::text("blank = inherit")
                    )
                } else {
                    label.into()
                },
                if panel.is_some() {
                    custom.unwrap_or_default()
                } else {
                    global
                },
            )
        };
        let boolean = |label: &str, global: bool, custom: Option<bool>| {
            choice(
                label,
                vec!["Hidden".into(), "Visible".into()],
                usize::from(global),
                custom.map(usize::from),
            )
        };
        result.page(
            "Layout",
            vec![
                (
                    "style",
                    choice(
                        "Panel bar style",
                        HeaderStyle::captions(),
                        theme.header_style.index(),
                        panel.and_then(|p| p.header_style).map(HeaderStyle::index),
                    ),
                ),
                (
                    "alignment",
                    choice(
                        "Title and tab alignment",
                        HeaderAlignment::captions(),
                        theme.header_alignment.index(),
                        panel
                            .and_then(panel_options::alignment_override)
                            .map(HeaderAlignment::index),
                    ),
                ),
                (
                    "corners",
                    choice(
                        "Panel corners",
                        vec!["Square".into(), "Rounded".into()],
                        usize::from(theme.rounded_corners),
                        overrides.rounded_corners.map(usize::from),
                    ),
                ),
                (
                    "reset",
                    Input::Check(
                        if panel.is_some() {
                            "Reset this panel to the theme"
                        } else {
                            "Reset all panel overrides to the theme"
                        }
                        .into(),
                        false,
                    ),
                ),
            ],
        );
        result.page(
            "Glass",
            vec![
                (
                    "glass",
                    choice(
                        "Glass effect",
                        GlassMode::captions(),
                        theme.glass_mode.index(),
                        overrides.glass_mode.map(GlassMode::index),
                    ),
                ),
                (
                    "opacity",
                    text(
                        "Tint opacity (10–100%)",
                        theme.glass_opacity.to_string(),
                        overrides.glass_opacity.map(|v| v.to_string()),
                    ),
                ),
            ],
        );
        let mut colors = Vec::new();
        if let Some(panel) = panel {
            let mut names = result.presets.clone();
            names[0] = "Use global appearance".into();
            colors.push((
                "preset",
                Input::Choice(
                    "Appearance preset".into(),
                    names,
                    result
                        .presets
                        .iter()
                        .position(|name| name == &panel.preset_name)
                        .unwrap_or(0),
                ),
            ));
        }
        colors.extend([
            (
                "background",
                text(
                    "Background color",
                    theme.background_color.clone(),
                    overrides.background_color.clone(),
                ),
            ),
            (
                "header",
                text(
                    "Header color",
                    theme.header_color.clone(),
                    overrides.header_color.clone(),
                ),
            ),
            (
                "text",
                text(
                    "Text color",
                    theme.text_color.clone(),
                    overrides.text_color.clone(),
                ),
            ),
            (
                "muted",
                text(
                    "Secondary text color",
                    theme.muted_text_color.clone(),
                    overrides.muted_text_color.clone(),
                ),
            ),
            (
                "accent",
                text(
                    "Accent color",
                    theme.accent_color.clone(),
                    overrides.accent_color.clone(),
                ),
            ),
        ]);
        result.page("Colors", colors);
        result.page(
            "Typography",
            vec![
                (
                    "font",
                    text(
                        "Font family",
                        theme.font_family.clone(),
                        overrides.font_family.clone(),
                    ),
                ),
                (
                    "item_size",
                    text(
                        "Item text size (9–24)",
                        theme.item_font_size.to_string(),
                        overrides.item_font_size.map(|v| v.to_string()),
                    ),
                ),
                (
                    "title_size",
                    text(
                        "Title text size (10–30)",
                        theme.title_font_size.to_string(),
                        overrides.title_font_size.map(|v| v.to_string()),
                    ),
                ),
            ],
        );
        let legacy = panel.map(|p| panel_options::Options::resolved(p, theme));
        let customized = |key: &str| panel.is_some_and(|p| legacy_value(p, key).is_some());
        let search = overrides.search.as_deref().or_else(|| {
            customized("SearchVisibilityMode").then(|| legacy.as_ref().unwrap().search.as_str())
        });
        let settings_button = overrides.settings.as_deref().or_else(|| {
            (customized("SettingsButtonVisibilityMode") || customized("ShowSettingsButton"))
                .then(|| legacy.as_ref().unwrap().settings.as_str())
        });
        let parent = overrides.parent.as_deref().or_else(|| {
            (customized("IconViewParentNavigationMode") || customized("ShowParentNavigationItem"))
                .then(|| legacy.as_ref().unwrap().parent.as_str())
        });
        let controls = &theme.header_controls;
        result.page(
            "Header buttons",
            vec![
                (
                    "search",
                    choice(
                        "Search in the header",
                        captions(&["Search button", "Search field", "Hidden"]),
                        index(SEARCH, &controls.search),
                        search.map(|v| index(SEARCH, v)),
                    ),
                ),
                (
                    "expanded",
                    choice(
                        "Search visibility",
                        captions(&["Also when collapsed", "Only when expanded"]),
                        usize::from(controls.search_expanded),
                        overrides
                            .search_expanded
                            .or_else(|| {
                                customized("SearchVisibleOnlyExpanded")
                                    .then(|| legacy.as_ref().unwrap().search_expanded)
                            })
                            .map(usize::from),
                    ),
                ),
                (
                    "settings",
                    choice(
                        "Settings button",
                        captions(&["Always visible", "Only when expanded", "Hidden"]),
                        index(SETTINGS, &controls.settings),
                        settings_button.map(|v| index(SETTINGS, v)),
                    ),
                ),
                (
                    "close",
                    boolean(
                        "Show close button",
                        controls.close,
                        overrides.close.or_else(|| {
                            customized("ShowCloseButton").then(|| legacy.as_ref().unwrap().close)
                        }),
                    ),
                ),
                (
                    "empty",
                    boolean(
                        "Show clear button",
                        controls.empty_button,
                        overrides.empty_button.or_else(|| {
                            (customized("ShowEmptyPanelButton")
                                || customized("ShowEmptyRecycleBinButton"))
                            .then(|| legacy.as_ref().unwrap().empty_button)
                        }),
                    ),
                ),
                (
                    "parent",
                    choice(
                        "Parent folder navigation",
                        captions(&[
                            "Back button in header",
                            "Parent folder in file list",
                            "Hidden",
                        ]),
                        index(PARENT, &controls.parent),
                        parent.map(|v| index(PARENT, v)),
                    ),
                ),
            ],
        );
        result
    }
    fn page(&mut self, name: &'static str, fields: Vec<(&'static str, Input)>) {
        let start = self.keys.len();
        for (index, (key, _)) in fields.iter().enumerate() {
            self.keys.insert(key, start + index);
        }
        self.pages.push(Page {
            name,
            fields: fields.into_iter().map(|(_, field)| field).collect(),
        });
    }
    fn get<'a>(&self, values: &'a [String], key: &str) -> &'a str {
        values.get(self.keys[key]).map(|v| v.trim()).unwrap_or("")
    }
    fn selected(&self, values: &[String], key: &str) -> Option<usize> {
        let index = self.get(values, key).parse::<usize>().ok()?;
        if self.panel {
            index.checked_sub(1)
        } else {
            Some(index)
        }
    }
    pub fn validate(&self, values: &[String]) -> Result<(), String> {
        if self.panel && self.get(values, "reset") == "1" {
            return Ok(());
        }
        for key in ["background", "header", "text", "muted", "accent"] {
            let value = self.get(values, key);
            if self.panel && value.is_empty() {
                continue;
            }
            if value.len() != 7
                || !value.starts_with('#')
                || u32::from_str_radix(&value[1..], 16).is_err()
            {
                return Err("Colors must use #RRGGBB format.".into());
            }
        }
        if !self.panel && self.get(values, "font").is_empty() {
            return Err("Enter a font family.".into());
        }
        for (key, min, max, error) in [
            (
                "opacity",
                10.0,
                100.0,
                "Tint opacity must be a whole number between 10 and 100.",
            ),
            (
                "item_size",
                9.0,
                24.0,
                "Text size must be between 9 and 24.",
            ),
            (
                "title_size",
                10.0,
                30.0,
                "Title size must be between 10 and 30.",
            ),
        ] {
            let value = self.get(values, key);
            if self.panel && value.is_empty() {
                continue;
            }
            if !value.parse::<f64>().is_ok_and(|v| {
                v.is_finite()
                    && (min..=max).contains(&v)
                    && (key != "opacity" || value.parse::<u8>().is_ok())
            }) {
                return Err(error.into());
            }
        }
        Ok(())
    }
    fn overrides(&self, values: &[String]) -> AppearanceOverrides {
        let text = |key| {
            let value = self.get(values, key);
            (!value.is_empty()).then(|| value.to_string())
        };
        let selected = |key| self.selected(values, key);
        AppearanceOverrides {
            header_alignment: selected("alignment").map(HeaderAlignment::from_index),
            glass_mode: selected("glass").map(GlassMode::from_index),
            glass_opacity: self.get(values, "opacity").parse().ok(),
            rounded_corners: selected("corners").map(|v| v == 1),
            background_color: text("background"),
            header_color: text("header"),
            text_color: text("text"),
            muted_text_color: text("muted"),
            accent_color: text("accent"),
            font_family: text("font"),
            item_font_size: self.get(values, "item_size").parse().ok(),
            title_font_size: self.get(values, "title_size").parse().ok(),
            search: selected("search")
                .and_then(|v| SEARCH.get(v))
                .map(|v| (*v).into()),
            search_expanded: selected("expanded").map(|v| v == 1),
            settings: selected("settings")
                .and_then(|v| SETTINGS.get(v))
                .map(|v| (*v).into()),
            close: selected("close").map(|v| v == 1),
            empty_button: selected("empty").map(|v| v == 1),
            parent: selected("parent")
                .and_then(|v| PARENT.get(v))
                .map(|v| (*v).into()),
        }
    }
    pub fn apply_theme(&self, settings: &mut Settings, values: &[String]) {
        self.overrides(values).apply(&mut settings.appearance);
        settings.appearance.header_style = self
            .selected(values, "style")
            .map(HeaderStyle::from_index)
            .unwrap_or_default();
        if self.get(values, "reset") == "1" {
            for panel in &mut settings.windows {
                reset(panel);
            }
        }
    }
    pub fn apply_panel(&self, panel: &mut Panel, values: &[String]) {
        clear_legacy(panel);
        if self.get(values, "reset") == "1" {
            reset(panel);
        } else {
            panel.header_style = self.selected(values, "style").map(HeaderStyle::from_index);
            panel.appearance_overrides = self.overrides(values);
            panel.preset_name = self
                .get(values, "preset")
                .parse::<usize>()
                .ok()
                .and_then(|i| self.presets.get(i))
                .cloned()
                .unwrap_or_default();
        }
    }
}
fn captions(values: &[&str]) -> Vec<String> {
    values.iter().map(|v| (*v).into()).collect()
}
fn index(values: &[&str], value: &str) -> usize {
    values.iter().position(|v| *v == value).unwrap_or(0)
}
pub fn legacy_value<'a>(panel: &'a Panel, key: &str) -> Option<&'a serde_json::Value> {
    let find = |map: &'a serde_json::Map<String, serde_json::Value>| {
        map.iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(key))
            .map(|(_, v)| v)
    };
    panel
        .tabs
        .as_ref()
        .and_then(|tabs| tabs.get(panel.active_tab_index))
        .and_then(|t| find(&t.extra))
        .or_else(|| find(&panel.extra))
}
pub fn clear_legacy(panel: &mut Panel) {
    let keep = |key: &String, _: &mut serde_json::Value| {
        !LEGACY_KEYS.iter().any(|k| key.eq_ignore_ascii_case(k))
    };
    panel.extra.retain(keep);
    if let Some(tabs) = &mut panel.tabs {
        for tab in tabs {
            tab.extra.retain(keep);
        }
    }
}
pub fn copy_legacy(panel: &mut Panel, previous: &Panel) {
    clear_legacy(panel);
    let copy = |to: &mut serde_json::Map<String, serde_json::Value>,
                from: &serde_json::Map<String, serde_json::Value>| {
        for (key, value) in from
            .iter()
            .filter(|(key, _)| LEGACY_KEYS.iter().any(|k| key.eq_ignore_ascii_case(k)))
        {
            to.insert(key.clone(), value.clone());
        }
    };
    copy(&mut panel.extra, &previous.extra);
    if let (Some(tabs), Some(saved)) = (&mut panel.tabs, &previous.tabs) {
        for tab in tabs {
            if let Some(old) = saved.iter().find(|t| t.tab_id == tab.tab_id) {
                copy(&mut tab.extra, &old.extra);
            }
        }
    }
}
pub fn reset(panel: &mut Panel) {
    panel.header_style = None;
    panel.preset_name.clear();
    panel.appearance_overrides = AppearanceOverrides::default();
    clear_legacy(panel);
}

#[cfg(test)]
mod tests {
    use super::*;
    fn draft(editor: &Editor) -> Vec<String> {
        editor
            .pages
            .iter()
            .flat_map(|page| &page.fields)
            .map(|field| match field {
                Input::Text(_, value) | Input::Folder(_, value) => value.clone(),
                Input::Check(_, value) => usize::from(*value).to_string(),
                Input::Choice(_, _, value) => value.to_string(),
                Input::Action(..) => String::new(),
            })
            .collect()
    }
    #[test]
    fn theme_and_panel_changes_roundtrip_and_inherit_across_tabs() {
        let mut settings = Settings::default();
        let mut panel = Panel::folder(&std::env::temp_dir());
        panel
            .tabs
            .as_mut()
            .unwrap()
            .push(crate::model::Tab::default());
        let editor = Editor::new(&settings, None);
        let mut values = draft(&editor);
        values[editor.keys["alignment"]] = "2".into();
        values[editor.keys["search"]] = "0".into();
        values[editor.keys["opacity"]] = "56".into();
        editor.validate(&values).unwrap();
        editor.apply_theme(&mut settings, &values);
        assert_eq!(
            panel_options::Options::resolved(&panel, &settings.appearance).alignment,
            "right"
        );
        let editor = Editor::new(&settings, Some(&panel));
        let mut values = draft(&editor);
        values[editor.keys["style"]] = "5".into();
        values[editor.keys["alignment"]] = "2".into();
        values[editor.keys["glass"]] = (GlassMode::Clear.index() + 1).to_string();
        editor.apply_panel(&mut panel, &values);
        panel.active_tab_index = 1;
        assert_eq!(
            panel_options::Options::resolved(&panel, &settings.appearance).alignment,
            "center"
        );
        assert_eq!(
            panel_options::Options::resolved(&panel, &settings.appearance).search,
            "button"
        );
        assert_eq!(settings.panel_theme(&panel).glass_mode, GlassMode::Clear);
        assert_eq!(settings.panel_theme(&panel).glass_opacity, 56);
        settings.windows.push(panel);
        let restored = crate::config::parse(&serde_json::to_string(&settings).unwrap()).unwrap();
        assert_eq!(
            restored.panel_theme(&restored.windows[0]).glass_mode,
            GlassMode::Clear
        );
        assert_eq!(
            restored.windows[0].header_style,
            Some(HeaderStyle::Floating)
        );
    }
    #[test]
    fn reset_clears_legacy_settings_in_every_tab_but_keeps_behavior() {
        let mut settings = Settings::default();
        let mut panel = Panel::folder(&std::env::temp_dir());
        panel
            .active_mut()
            .extra
            .insert("headercontentalignment".into(), "left".into());
        panel.extra.insert("MovementMode".into(), "locked".into());
        panel
            .tabs
            .as_mut()
            .unwrap()
            .push(crate::model::Tab::default());
        panel.active_tab_index = 1;
        panel
            .active_mut()
            .extra
            .insert("ShowCloseButton".into(), false.into());
        settings.windows.push(panel);
        let editor = Editor::new(&settings, None);
        let mut values = draft(&editor);
        values[editor.keys["reset"]] = "1".into();
        editor.apply_theme(&mut settings, &values);
        for tab in 0..2 {
            settings.windows[0].active_tab_index = tab;
            let options =
                panel_options::Options::resolved(&settings.windows[0], &settings.appearance);
            assert_eq!(options.alignment, "center");
            assert!(options.close);
            assert_eq!(options.movement, "locked");
        }
    }
    #[test]
    fn validates_custom_values_and_allows_inherited_fields() {
        let settings = Settings::default();
        let panel = Panel::folder(&std::env::temp_dir());
        let editor = Editor::new(&settings, Some(&panel));
        let mut values = draft(&editor);
        editor.validate(&values).unwrap();
        for invalid in ["NaN", "0", "101", "72.5", "72.0", "7.2e1"] {
            values[editor.keys["opacity"]] = invalid.into();
            assert!(editor.validate(&values).is_err());
        }
        values[editor.keys["opacity"]] = "72".into();
        values[editor.keys["background"]] = "invalid".into();
        assert!(editor.validate(&values).is_err());
    }
}
