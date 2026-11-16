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
        SHORTCUTS if values.iter().any(|value| hotkey(value).is_none()) => {
            return Err("Use a shortcut such as Ctrl + Alt + H.".into());
        }
        _ => {}
    }
    Ok(())
}

impl App {
    pub(super) fn panel_layout_builder(&mut self, hwnd: usize) -> Result<(), String> {
        if let Some(index) = self.panel_index(hwnd) {
            if let Some(draft) = design::show(hwnd as _, &self.settings, Some(index)) {
                self.save_design(draft);
            }
        }
        Ok(())
    }
    pub(super) fn layout_builder(&mut self) -> Result<(), String> {
        if let Some(draft) = design::show(self.main, &self.settings, None) {
            self.save_design(draft);
            let editor = control(self.main, dashboard::form_id(THEMES));
            if !editor.is_null() {
                unsafe {
                    DestroyWindow(editor);
                }
                self.open_preference(THEMES)?;
            }
        }
        Ok(())
    }
    fn save_design(&mut self, draft: Settings) {
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
        self.changed();
    }

    pub(super) fn open_preference(&mut self, command: u16) -> Result<(), String> {
        if command == AUTO_SORT {
            let page = control(self.main, dashboard::form_id(AUTO_SORT));
            if page.is_null() {
                sort_rules::embed(self.main, &self.settings.desktop_auto_sort)?;
            } else {
                sort_rules::refresh(page, &self.settings.desktop_auto_sort);
            }
            dashboard::select(self.main, AUTO_SORT);
            sort_rules::busy(
                control(self.main, dashboard::form_id(AUTO_SORT)),
                self.sorting,
            );
            return Ok(());
        }
        if command == THEMES {
            if control(self.main, dashboard::form_id(THEMES)).is_null() {
                design::embed(self.main, &self.settings)?;
            }
            dashboard::select(self.main, THEMES);
            return Ok(());
        }
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
        if command == THEMES {
            let draft = design::saved(control(self.main, dashboard::form_id(THEMES)))?;
            self.save_design(draft);
            return Ok(());
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
