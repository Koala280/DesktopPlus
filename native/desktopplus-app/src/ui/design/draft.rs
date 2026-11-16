//! Editing and previewing never mutate the application's persisted settings.
use super::*;

pub(super) struct Draft {
    pub original: Settings,
    pub settings: Settings,
    pub scope: Option<usize>,
    pub editor: layout_builder::Editor,
    pub values: Vec<String>,
}
impl Draft {
    pub fn new(settings: &Settings, scope: Option<usize>) -> Self {
        let editor =
            layout_builder::Editor::new(settings, scope.and_then(|i| settings.windows.get(i)));
        let values = editor.values();
        Self {
            original: settings.clone(),
            settings: settings.clone(),
            scope,
            editor,
            values,
        }
    }
    pub fn commit(&mut self) -> Result<(), String> {
        self.editor.validate(&self.values)?;
        if let Some(index) = self.scope {
            self.editor
                .apply_panel(&mut self.settings.windows[index], &self.values);
        } else {
            self.editor.apply_theme(&mut self.settings, &self.values);
        }
        Ok(())
    }
    pub fn reload(&mut self) {
        self.editor = layout_builder::Editor::new(
            &self.settings,
            self.scope.map(|i| &self.settings.windows[i]),
        );
        self.values = self.editor.values();
    }
    pub fn switch(&mut self, scope: Option<usize>) -> Result<(), String> {
        self.commit()?;
        self.scope = scope;
        self.reload();
        Ok(())
    }
    pub fn revert(&mut self) {
        if let Some(index) = self.scope {
            let original = &self.original.windows[index];
            let panel = &mut self.settings.windows[index];
            panel.appearance_overrides = original.appearance_overrides.clone();
            panel.header_style = original.header_style;
            panel.preset_name = original.preset_name.clone();
            layout_builder::copy_legacy(panel, original);
        } else {
            self.settings.appearance = self.original.appearance.clone();
        }
        self.reload();
    }
    pub fn preview(&self) -> Result<(Theme, crate::model::Panel), String> {
        self.editor.validate(&self.values)?;
        let mut settings = self.settings.clone();
        let mut panel = self
            .scope
            .map(|i| settings.windows[i].clone())
            .unwrap_or_else(|| {
                let mut panel = crate::model::Panel {
                    panel_title: "My workspace".into(),
                    ..Default::default()
                };
                panel.normalize();
                panel
            });
        if self.scope.is_some() {
            self.editor.apply_panel(&mut panel, &self.values);
        } else {
            self.editor.apply_theme(&mut settings, &self.values);
        }
        let theme = settings.panel_theme(&panel);
        Ok((theme, panel))
    }
    pub fn set(&mut self, key: &str, value: String) {
        if let Some(index) = (0..self.values.len()).find(|i| self.editor.key(*i) == key) {
            self.values[index] = value;
        }
    }
    pub fn preset(&mut self, index: usize) {
        let colors = PALETTES[index.min(PALETTES.len() - 1)].1;
        for (key, value) in ["background", "header", "text", "muted", "accent"]
            .into_iter()
            .zip(colors)
        {
            self.set(key, value.into());
        }
    }
}

pub(super) const PALETTES: &[(&str, [&str; 5])] = &[
    (
        "Midnight",
        ["#11151F", "#1B2231", "#EFF2FA", "#9CA9BD", "#A5B4FC"],
    ),
    (
        "Daylight",
        ["#F3F5F9", "#E6EAF2", "#182237", "#53627B", "#375BC7"],
    ),
    (
        "Graphite",
        ["#202326", "#2C3035", "#F3F4F5", "#AFB7C2", "#95BAFF"],
    ),
    (
        "Forest",
        ["#12241F", "#1B342C", "#ECF7F1", "#A0BCAF", "#8DDCB5"],
    ),
    (
        "Rose",
        ["#281D28", "#392A39", "#FFF0F8", "#C7A9BE", "#F3ABD0"],
    ),
];

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn preview_and_cancel_leave_the_original_untouched() {
        let settings = Settings::default();
        let original = serde_json::to_string(&settings).unwrap();
        let mut draft = Draft::new(&settings, None);
        draft.preset(1);
        draft.set("title_size", "23".into());
        let (theme, _) = draft.preview().unwrap();
        assert_eq!(theme.background_color, "#F3F5F9");
        assert_eq!(theme.title_font_size, 23.0);
        assert_eq!(serde_json::to_string(&settings).unwrap(), original);
        assert_eq!(serde_json::to_string(&draft.settings).unwrap(), original);
        draft.revert();
        assert_eq!(
            draft.preview().unwrap().0.background_color,
            settings.appearance.background_color
        );
    }
    #[test]
    fn scope_switch_keeps_drafts_and_preview_matches_saved_panel() {
        let mut settings = Settings::default();
        settings
            .windows
            .push(crate::model::Panel::folder(&std::env::temp_dir()));
        let mut draft = Draft::new(&settings, None);
        draft.preset(3);
        draft.switch(Some(0)).unwrap();
        assert_eq!(draft.preview().unwrap().0.background_color, "#12241F");
        draft.set("accent", "#F3ABD0".into());
        draft.set("opacity", "43".into());
        let preview = serde_json::to_string(&draft.preview().unwrap().0).unwrap();
        draft.commit().unwrap();
        let saved = draft.settings.panel_theme(&draft.settings.windows[0]);
        assert_eq!(serde_json::to_string(&saved).unwrap(), preview);
        draft.switch(None).unwrap();
        assert_eq!(draft.preview().unwrap().0.accent_color, "#8DDCB5");
        draft.switch(Some(0)).unwrap();
        draft.set("accent", String::new());
        assert_eq!(draft.preview().unwrap().0.accent_color, "#8DDCB5");
        assert_eq!(draft.preview().unwrap().0.glass_opacity, 43);
    }
    #[test]
    fn invalid_input_blocks_save_and_scope_switch_without_mutating_settings() {
        let mut draft = Draft::new(&Settings::default(), None);
        let original = serde_json::to_string(&draft.settings).unwrap();
        for (key, value) in [
            ("background", "#ZZ0011"),
            ("opacity", "NaN"),
            ("title_size", "31"),
        ] {
            draft.revert();
            draft.set(key, value.into());
            assert!(draft.commit().is_err());
            assert!(draft.switch(None).is_err());
            assert_eq!(serde_json::to_string(&draft.settings).unwrap(), original);
        }
    }
}
