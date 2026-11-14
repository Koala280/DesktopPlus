use super::*;
use form::Input;

const OPEN_SETTINGS_FOLDER: u16 = 3;
const IMPORT_LANGUAGE: u16 = 4;
const CHECK_UPDATES: u16 = 5;

#[cfg(test)]
#[path = "preferences_tests.rs"]
mod tests;

fn validate(command: u16, values: &[String]) -> Result<(), String> {
    match command {
        THEMES => {
            for value in &values[..4] {
                if value.len() != 7
                    || !value.starts_with('#')
                    || u32::from_str_radix(&value[1..], 16).is_err()
                {
                    return Err("Colors must use #RRGGBB format.".into());
                }
            }
            if values[4].trim().is_empty() {
                return Err("Enter a font family.".into());
            }
            if !values[5]
                .parse::<f64>()
                .is_ok_and(|size| size.is_finite() && (9.0..=24.0).contains(&size))
            {
                return Err("Text size must be between 9 and 24.".into());
            }
        }
        SHORTCUTS if values.iter().any(|value| hotkey(value).is_none()) => {
            return Err("Use a shortcut such as Ctrl + Alt + H.".into());
        }
        _ => {}
    }
    Ok(())
}

impl App {
    pub(super) fn panel_layout_builder(&mut self, hwnd: usize) -> Result<(), String> {
        let Some(index) = self.panel_index(hwnd) else {
            return Ok(());
        };
        let mut editor =
            layout_builder::Editor::new(&self.settings, Some(&self.settings.windows[index]));
        let pages = std::mem::take(&mut editor.pages);
        if let Some(answer) = form::show(
            hwnd as _,
            "Panel appearance",
            &crate::locale::text(
                "Inherit from theme, or customize this panel. Blank fields follow the theme.",
            ),
            pages,
            &|values| editor.validate(values),
        ) {
            editor.apply_panel(&mut self.settings.windows[index], &answer.values);
            self.apply_theme();
            self.refresh(hwnd);
            self.changed();
        }
        Ok(())
    }
    pub(super) fn layout_builder(&mut self) -> Result<(), String> {
        let mut draft = self.settings.clone();
        loop {
            let mut editor = layout_builder::Editor::new(&draft, None);
            editor.pages.push(form::Page {
                name: "Panels",
                fields: draft
                    .windows
                    .iter()
                    .enumerate()
                    .map(|(i, panel)| {
                        Input::Action(
                            format!(
                                "{} — {}",
                                panel.panel_title,
                                crate::locale::text("Customize…")
                            ),
                            4000 + i as u16,
                        )
                    })
                    .collect(),
            });
            let pages = std::mem::take(&mut editor.pages);
            let Some(answer) = form::show(
                self.main,
                "Layout builder",
                &crate::locale::text(
                    "Theme defaults and individual panels. Changes apply when you save.",
                ),
                pages,
                &|values| editor.validate(values),
            ) else {
                return Ok(());
            };
            editor.apply_theme(&mut draft, &answer.values);
            if let Some(action) = answer.action {
                let index = usize::from(action.saturating_sub(4000));
                if let Some(panel) = draft.windows.get(index) {
                    let title = format!(
                        "{} — {}",
                        crate::locale::text("Layout builder"),
                        panel.panel_title
                    );
                    let mut panel_editor = layout_builder::Editor::new(&draft, Some(panel));
                    let pages = std::mem::take(&mut panel_editor.pages);
                    if let Some(answer) = form::show(
                        self.main,
                        &title,
                        &crate::locale::text(
                            "Inherit from theme, or customize this panel. Blank fields follow the theme.",
                        ),
                        pages,
                        &|values| panel_editor.validate(values),
                    ) {
                        panel_editor.apply_panel(&mut draft.windows[index], &answer.values);
                    }
                }
                continue;
            }
            self.settings.appearance = draft.appearance;
            for panel in &mut self.settings.windows {
                if let Some(updated) = draft.windows.iter().find(|p| p.panel_id == panel.panel_id) {
                    panel.header_style = updated.header_style;
                    panel.appearance_overrides = updated.appearance_overrides.clone();
                    panel.preset_name = updated.preset_name.clone();
                    layout_builder::copy_legacy(panel, updated);
                }
            }
            self.apply_theme();
            for hwnd in self.windows.keys().copied().collect::<Vec<_>>() {
                self.refresh(hwnd);
            }
            // Refresh only the appearance editor; other preference drafts stay intact.
            let form = control(self.main, dashboard::form_id(THEMES));
            if !form.is_null() {
                unsafe {
                    DestroyWindow(form);
                }
                self.open_preference(THEMES)?;
            }
            self.changed();
            return Ok(());
        }
    }

    pub(super) fn open_preference(&mut self, command: u16) -> Result<(), String> {
        if control(self.main, dashboard::form_id(command)).is_null() {
            let (title, fields) = match command {
                GENERAL => {
                    self.preference_languages =
                        crate::locale::languages(&self.store.data.join("Languages"));
                    let language = self
                        .preference_languages
                        .iter()
                        .position(|(code, _)| code == &self.settings.language)
                        .unwrap_or(0);
                    (
                        "General",
                        vec![
                            Input::Choice(
                                "Language".into(),
                                self.preference_languages
                                    .iter()
                                    .map(|(_, name)| name.clone())
                                    .collect(),
                                language,
                            ),
                            Input::Check(
                                "Start with Windows".into(),
                                self.settings.start_with_windows,
                            ),
                            Input::Choice(
                                "When closing the main window".into(),
                                vec!["Minimize to tray".into(), "Exit".into()],
                                usize::from(self.settings.close_behavior == "Exit"),
                            ),
                            Input::Check(
                                "Automatically install updates".into(),
                                self.settings.auto_check_updates,
                            ),
                            Input::Action("Check for updates now".into(), CHECK_UPDATES),
                            Input::Action("Import language JSON…".into(), IMPORT_LANGUAGE),
                            Input::Action("Open settings folder".into(), OPEN_SETTINGS_FOLDER),
                            Input::Action("Project / help".into(), HELP),
                            Input::Action("Exit DesktopPlus".into(), EXIT),
                        ],
                    )
                }
                THEMES => {
                    let theme = &self.settings.appearance;
                    (
                        "Appearance",
                        vec![
                            Input::Text(
                                "Background (#RRGGBB)".into(),
                                theme.background_color.clone(),
                            ),
                            Input::Text(
                                "Header and buttons (#RRGGBB)".into(),
                                theme.header_color.clone(),
                            ),
                            Input::Text("Text (#RRGGBB)".into(), theme.text_color.clone()),
                            Input::Text("Accent (#RRGGBB)".into(), theme.accent_color.clone()),
                            Input::Text("Font family".into(), theme.font_family.clone()),
                            Input::Text(
                                "Text size (9–24)".into(),
                                theme.item_font_size.to_string(),
                            ),
                            Input::Choice(
                                "Panel bar style (theme)".into(),
                                HeaderStyle::captions(),
                                theme.header_style.index(),
                            ),
                            Input::Choice(
                                "Title and tab alignment".into(),
                                crate::model::HeaderAlignment::captions(),
                                theme.header_alignment.index(),
                            ),
                            Input::Action("Layout builder…".into(), LAYOUT_BUILDER),
                        ],
                    )
                }
                SHORTCUTS => (
                    "Keyboard shortcuts",
                    vec![
                        Input::Text(
                            "Show / hide panels".into(),
                            self.settings.global_shortcuts.hide_panels_hotkey.clone(),
                        ),
                        Input::Text(
                            "Bring panels forward temporarily".into(),
                            self.settings
                                .global_shortcuts
                                .foreground_panels_hotkey
                                .clone(),
                        ),
                    ],
                ),
                _ => return Ok(()),
            };
            form::embed(self.main, command, title, fields, move |values| {
                validate(command, values)
            })?;
        }
        dashboard::select(self.main, command);
        if command == GENERAL {
            let status = if self.updater.status.is_empty() {
                format!("DesktopPlus {}", env!("DESKTOPPLUS_VERSION"))
            } else {
                self.updater.status.clone()
            };
            form::status(control(self.main, dashboard::form_id(GENERAL)), &status);
        }
        Ok(())
    }

    pub(super) fn save_preference(
        &mut self,
        command: u16,
        answer: form::Answer,
    ) -> Result<(), String> {
        if let Some(action) = answer.action {
            return self.preference_action(action);
        }
        let values = answer.values;
        validate(command, &values)?;
        match command {
            GENERAL => {
                let startup = values[1] == "1";
                if startup != self.settings.start_with_windows {
                    shell::startup(startup)?;
                }
                self.settings.language = self.preference_languages[values[0]
                    .parse::<usize>()
                    .unwrap_or(0)
                    .min(self.preference_languages.len() - 1)]
                .0
                .clone();
                self.settings.start_with_windows = startup;
                self.settings.close_behavior =
                    if values[2] == "1" { "Exit" } else { "Minimize" }.into();
                let auto_updates = values[3] == "1";
                if auto_updates != self.settings.auto_check_updates {
                    self.settings.auto_check_updates = auto_updates;
                    self.configure_updates();
                }
                self.relabel();
            }
            THEMES => {
                let theme = &mut self.settings.appearance;
                theme.background_color = values[0].clone();
                theme.header_color = values[1].clone();
                theme.text_color = values[2].clone();
                theme.accent_color = values[3].clone();
                theme.font_family = values[4].clone();
                theme.item_font_size = values[5].parse().unwrap_or(14.0);
                theme.header_style = HeaderStyle::from_index(
                    values
                        .get(6)
                        .and_then(|value| value.parse().ok())
                        .unwrap_or(0),
                );
                theme.header_alignment = crate::model::HeaderAlignment::from_index(
                    values.get(7).and_then(|v| v.parse().ok()).unwrap_or(1),
                );
                self.apply_theme();
            }
            SHORTCUTS => {
                self.settings.global_shortcuts.hide_panels_hotkey = values[0].clone();
                self.settings.global_shortcuts.foreground_panels_hotkey = values[1].clone();
                self.register_hotkeys();
            }
            _ => return Ok(()),
        }
        self.changed();
        form::status(control(self.main, dashboard::form_id(command)), "Saved.");
        Ok(())
    }

    fn preference_action(&mut self, action: u16) -> Result<(), String> {
        match action {
            CHECK_UPDATES => self.check_updates(true)?,
            OPEN_SETTINGS_FOLDER => {
                if let Some(parent) = self.store.path.parent() {
                    std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
                    shell::open(self.main, parent)?;
                }
            }
            IMPORT_LANGUAGE => {
                if let Some(path) = shell::choose_path(self.main, false)? {
                    let (code, name, translations) = crate::locale::read(&path)?;
                    if matches!(code.as_str(), "en" | "de" | "lv") {
                        return Err("Built-in language codes cannot be replaced.".into());
                    }
                    crate::config::atomic_write(&self.store.data.join("Languages").join(format!("{code}.json")), &serde_json::to_vec_pretty(&serde_json::json!({"code":code,"name":name,"translations":translations})).unwrap()).map_err(|e| e.to_string())?;
                    self.settings.language = code;
                    self.preference_languages =
                        crate::locale::languages(&self.store.data.join("Languages"));
                    let selected = self
                        .preference_languages
                        .iter()
                        .position(|(code, _)| code == &self.settings.language)
                        .unwrap_or(0);
                    form::choices(
                        control(self.main, dashboard::form_id(GENERAL)),
                        0,
                        self.preference_languages
                            .iter()
                            .map(|(_, name)| name.clone())
                            .collect(),
                        selected,
                    );
                    self.relabel();
                    self.changed();
                }
            }
            command => self.command(self.main as usize, command)?,
        }
        Ok(())
    }
}
