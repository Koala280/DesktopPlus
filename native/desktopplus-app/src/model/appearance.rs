use super::*;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum HeaderAlignment {
    Left,
    Right,
    #[default]
    #[serde(other)]
    Center,
}
impl HeaderAlignment {
    pub const ALL: [Self; 3] = [Self::Left, Self::Center, Self::Right];
    pub fn key(self) -> &'static str {
        match self {
            Self::Left => "left",
            Self::Center => "center",
            Self::Right => "right",
        }
    }
    pub fn index(self) -> usize {
        Self::ALL.iter().position(|v| *v == self).unwrap_or(1)
    }
    pub fn from_index(index: usize) -> Self {
        Self::ALL.get(index).copied().unwrap_or_default()
    }
    pub fn captions() -> Vec<String> {
        ["Left", "Center", "Right"].map(String::from).to_vec()
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum GlassMode {
    Acrylic,
    Clear,
    Solid,
    #[default]
    #[serde(other)]
    Frosted,
}
impl GlassMode {
    pub const ALL: [Self; 4] = [Self::Frosted, Self::Acrylic, Self::Clear, Self::Solid];
    pub fn index(self) -> usize {
        Self::ALL.iter().position(|v| *v == self).unwrap_or(0)
    }
    pub fn from_index(index: usize) -> Self {
        Self::ALL.get(index).copied().unwrap_or_default()
    }
    pub fn captions() -> Vec<String> {
        [
            "Frosted glass",
            "Strong frosted glass",
            "Clear glass (no blur)",
            "Solid (no transparency)",
        ]
        .map(String::from)
        .to_vec()
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct HeaderControls {
    pub search: String,
    pub search_expanded: bool,
    pub settings: String,
    pub close: bool,
    pub empty_button: bool,
    pub parent: String,
}
impl Default for HeaderControls {
    fn default() -> Self {
        Self {
            search: "hidden".into(),
            search_expanded: true,
            settings: "hidden".into(),
            close: true,
            empty_button: false,
            parent: "header".into(),
        }
    }
}

/// Only explicitly customized fields are stored. Everything else follows the theme.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct AppearanceOverrides {
    pub header_alignment: Option<HeaderAlignment>,
    pub glass_mode: Option<GlassMode>,
    pub glass_opacity: Option<u8>,
    pub rounded_corners: Option<bool>,
    pub background_color: Option<String>,
    pub header_color: Option<String>,
    pub text_color: Option<String>,
    pub muted_text_color: Option<String>,
    pub accent_color: Option<String>,
    pub font_family: Option<String>,
    pub item_font_size: Option<f64>,
    pub title_font_size: Option<f64>,
    pub search: Option<String>,
    pub search_expanded: Option<bool>,
    pub settings: Option<String>,
    pub close: Option<bool>,
    pub empty_button: Option<bool>,
    pub parent: Option<String>,
}
impl AppearanceOverrides {
    pub fn apply(&self, theme: &mut Theme) {
        macro_rules! apply { ($($key:ident),*) => { $(if let Some(value) = &self.$key { theme.$key = value.clone(); })* }; }
        apply!(
            header_alignment,
            glass_mode,
            glass_opacity,
            rounded_corners,
            background_color,
            header_color,
            text_color,
            muted_text_color,
            accent_color,
            font_family,
            item_font_size,
            title_font_size
        );
        macro_rules! controls { ($($key:ident),*) => { $(if let Some(value) = &self.$key { theme.header_controls.$key = value.clone(); })* }; }
        controls!(
            search,
            search_expanded,
            settings,
            close,
            empty_button,
            parent
        );
    }
}

impl Settings {
    pub fn panel_theme(&self, panel: &Panel) -> Theme {
        let mut theme = self
            .presets
            .iter()
            .find(|preset| {
                preset.get("Name").and_then(Value::as_str) == Some(panel.preset_name.as_str())
            })
            .and_then(|preset| preset.get("Settings"))
            .and_then(|value| serde_json::from_value::<Theme>(value.clone()).ok())
            .unwrap_or_else(|| self.appearance.clone());
        // Presets supply colors/fonts; layout and glass follow the current theme.
        theme.header_style = panel.header_style.unwrap_or(self.appearance.header_style);
        theme.header_alignment = self.appearance.header_alignment;
        theme.glass_mode = self.appearance.glass_mode;
        theme.glass_opacity = self.appearance.glass_opacity;
        theme.rounded_corners = self.appearance.rounded_corners;
        theme.header_controls = self.appearance.header_controls.clone();
        panel.appearance_overrides.apply(&mut theme);
        theme
    }
}
