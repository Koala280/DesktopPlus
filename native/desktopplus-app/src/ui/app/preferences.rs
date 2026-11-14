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
    pub(super) fn layout_builder(&mut self) -> Result<(), String> {
        let panel_ids: Vec<_> = self
            .settings
            .windows
            .iter()
            .map(|panel| panel.panel_id.clone())
            .collect();
        let pages = vec![
            form::Page {
                name: "Theme",
                fields: vec![
                    Input::Choice(
                        "Panel bar style (theme)".into(),
                        HeaderStyle::captions(),
                        self.settings.appearance.header_style.index(),
                    ),
                    Input::Check("Reset panel overrides to theme".into(), false),
                ],
            },
            form::Page {
                name: "Panels",
                fields: self
                    .settings
                    .windows
                    .iter()
                    .map(|panel| {
                        Input::Choice(
                            panel.panel_title.clone(),
                            HeaderStyle::panel_captions(),
                            panel.header_style.map_or(0, |style| style.index() + 1),
                        )
                    })
                    .collect(),
            },
        ];
        if let Some(answer) = form::show(
            self.main,
            "Layout builder",
            "Choose a theme style or a different style for each panel.",
            pages,
            &|_| Ok(()),
        ) {
            apply_header_styles(&mut self.settings, &panel_ids, &answer.values);
            self.apply_theme();
            // Keep any open appearance draft in sync without discarding its other fields.
            form::choices(
                control(self.main, dashboard::form_id(THEMES)),
                6,
                HeaderStyle::captions(),
                self.settings.appearance.header_style.index(),
            );
            self.changed();
        }
        Ok(())
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

fn apply_header_styles(settings: &mut Settings, panel_ids: &[String], values: &[String]) {
    settings.appearance.header_style = HeaderStyle::from_index(
        values
            .first()
            .and_then(|value| value.parse().ok())
            .unwrap_or(0),
    );
    let reset = values.get(1).is_some_and(|value| value == "1");
    for (index, id) in panel_ids.iter().enumerate() {
        if let Some(panel) = settings
            .windows
            .iter_mut()
            .find(|panel| &panel.panel_id == id)
        {
            panel.header_style = if reset {
                None
            } else {
                values
                    .get(index + 2)
                    .and_then(|value| value.parse::<usize>().ok())
                    .and_then(|choice| choice.checked_sub(1))
                    .and_then(|index| HeaderStyle::ALL.get(index).copied())
            };
        }
    }
}
