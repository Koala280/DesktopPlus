use super::*;
use crate::model::SortRule;
use form::Input;
use sort_rules::Action;

fn parse_rule(original: &SortRule, values: &[String]) -> Result<SortRule, String> {
    let [name, kind, extensions, target, enabled] = values else {
        return Err("Invalid rule fields.".into());
    };
    if name.trim().is_empty() {
        return Err("Enter a rule name.".into());
    }
    let target = target.trim();
    if target.is_empty()
        || matches!(target, "." | "..")
        || target.ends_with('.')
        || target
            .chars()
            .any(|ch| ch.is_control() || "<>:\"/\\|?*".contains(ch))
    {
        return Err(
            "Enter a target panel name without path separators or special filename characters."
                .into(),
        );
    }
    let mut rule = original.clone();
    rule.rule_name = name.trim().into();
    rule.target_panel_name = target.into();
    rule.enabled = enabled == "1";
    rule.match_folders = kind == "1";
    rule.catch_all = kind == "2";
    if !matches!(kind.as_str(), "0" | "1" | "2") {
        return Err("Choose a rule type.".into());
    }
    rule.extensions.clear();
    if kind == "0" {
        rule.extensions = rule_editor::parse_extensions(extensions)?;
    }
    Ok(rule)
}

impl App {
    pub(super) fn sort_rule_action(&mut self, action: Action) -> Result<(), String> {
        let page = control(self.main, dashboard::form_id(AUTO_SORT));
        match action {
            Action::Add => self.edit_sort_rule(None)?,
            Action::Edit(id) => self.edit_sort_rule(Some(&id))?,
            Action::Toggle(id) => {
                if let Some(rule) = self
                    .settings
                    .desktop_auto_sort
                    .rules
                    .iter_mut()
                    .find(|rule| rule.rule_id == id)
                {
                    rule.enabled = !rule.enabled;
                    self.changed();
                    sort_rules::refresh(page, &self.settings.desktop_auto_sort);
                    sort_rules::status(
                        page,
                        "Rule saved. Previously sorted files stay where they are.",
                    );
                }
            }
            Action::Delete(id) => {
                let Some(index) = self
                    .settings
                    .desktop_auto_sort
                    .rules
                    .iter()
                    .position(|rule| rule.rule_id == id)
                else {
                    return Ok(());
                };
                let prompt = crate::locale::text(
                    "Delete rule \"{name}\"? Files and panels will stay where they are.",
                )
                .replace(
                    "{name}",
                    &self.settings.desktop_auto_sort.rules[index].rule_name,
                );
                if unsafe {
                    MessageBoxW(
                        self.main,
                        wide(prompt).as_ptr(),
                        wide(crate::locale::text("Delete rule")).as_ptr(),
                        MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2,
                    )
                } == IDYES
                {
                    self.settings.desktop_auto_sort.rules.remove(index);
                    self.changed();
                    sort_rules::refresh(page, &self.settings.desktop_auto_sort);
                    sort_rules::status(page, "Rule deleted. Files and panels were kept.");
                }
            }
            Action::Automatic(enabled) => {
                self.settings.desktop_auto_sort.auto_sort_enabled = enabled;
                self.configure();
                self.changed();
                sort_rules::refresh(page, &self.settings.desktop_auto_sort);
                sort_rules::status(
                    page,
                    if enabled {
                        "New desktop items will be sorted automatically."
                    } else {
                        "Automatic sorting is off. You can still sort manually."
                    },
                );
            }
            Action::Sort => self.sort_desktop()?,
            Action::Test => {
                if let Some(answer) = form::show(
                    self.main,
                    "Test a filename",
                    &crate::locale::text("Preview only. No files are moved."),
                    vec![form::Page {
                        name: "Test a filename",
                        fields: vec![
                            Input::Text(
                                "Filename, for example invoice.pdf".into(),
                                "invoice.pdf".into(),
                            ),
                            Input::Check("This is a folder".into(), false),
                        ],
                    }],
                    &|values| {
                        if values[0].trim().is_empty() {
                            Err("Enter a filename to test.".into())
                        } else {
                            Ok(())
                        }
                    },
                ) {
                    let name = answer.values[0].trim();
                    let result = match crate::sort::matching_rule(&self.settings.desktop_auto_sort.rules, Path::new(name), answer.values[1] == "1") {
                        Some(rule) => crate::locale::text("{file} → {target}\nMatched rule: {rule}\n\nPreview only. No files were moved.")
                            .replace("{file}", name).replace("{target}", &rule.target_panel_name).replace("{rule}", &rule.rule_name),
                        None => crate::locale::text("No enabled rule matches this item. It would stay on the desktop.\n\nPreview only. No files were moved."),
                    };
                    unsafe {
                        MessageBoxW(
                            self.main,
                            wide(result).as_ptr(),
                            wide(crate::locale::text("Test result")).as_ptr(),
                            MB_OK | MB_ICONINFORMATION,
                        );
                    }
                }
            }
        }
        Ok(())
    }

    fn edit_sort_rule(&mut self, id: Option<&str>) -> Result<(), String> {
        let original = match id {
            Some(id) => match self
                .settings
                .desktop_auto_sort
                .rules
                .iter()
                .find(|rule| rule.rule_id == id)
            {
                Some(rule) => rule.clone(),
                None => return Ok(()),
            },
            None => SortRule::default(),
        };
        if let Some(values) = rule_editor::show(
            self.main,
            &original,
            &self.settings.windows,
            id.is_some(),
            &|values| parse_rule(&original, values).map(|_| ()),
        ) {
            let rule = parse_rule(&original, &values)?;
            let saved_id = rule.rule_id.clone();
            if let Some(index) = self
                .settings
                .desktop_auto_sort
                .rules
                .iter()
                .position(|entry| entry.rule_id == rule.rule_id)
            {
                self.settings.desktop_auto_sort.rules[index] = rule;
            } else {
                self.settings.desktop_auto_sort.rules.push(rule);
            }
            self.changed();
            let page = control(self.main, dashboard::form_id(AUTO_SORT));
            sort_rules::refresh(page, &self.settings.desktop_auto_sort);
            sort_rules::select_rule(page, &saved_id);
            sort_rules::status(
                page,
                "Rule saved. Previously sorted files stay where they are.",
            );
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn values(kind: &str, extensions: &str, target: &str) -> Vec<String> {
        [" My PDFs ", kind, extensions, target, "1"]
            .map(str::to_owned)
            .to_vec()
    }
    #[test]
    fn normalizes_extensions_and_preserves_identity_and_unknown_settings() {
        let mut original = crate::model::default_rules().remove(4);
        original
            .extra
            .insert("FutureOption".into(), serde_json::json!(true));
        let parsed = parse_rule(&original, &values("0", "PDF, .pdf; *.PNG jpg", " Work ")).unwrap();
        assert_eq!(parsed.extensions, [".pdf", ".png", ".jpg"]);
        assert_eq!(parsed.rule_name, "My PDFs");
        assert_eq!(parsed.target_panel_name, "Work");
        assert_eq!(parsed.rule_id, original.rule_id);
        assert!(parsed.is_built_in);
        assert_eq!(parsed.extra, original.extra);
    }
    #[test]
    fn rejects_empty_names_destinations_and_invalid_extensions() {
        let rule = SortRule::default();
        for target in [
            "",
            " ",
            "..",
            ".",
            "../Documents",
            "C:\\Docs",
            "Docs.",
            "a\nb",
        ] {
            assert!(
                parse_rule(&rule, &values("0", "pdf", target)).is_err(),
                "{target}"
            );
        }
        for extensions in ["", ".", "*", "*.a*", "tar.gz", "a/b", "..pdf", "pdf:"] {
            assert!(
                parse_rule(&rule, &values("0", extensions, "Documents")).is_err(),
                "{extensions}"
            );
        }
        let mut input = values("0", "pdf", "Documents");
        input[0] = " ".into();
        assert!(parse_rule(&rule, &input).is_err());
    }
    #[test]
    fn rule_types_are_exclusive_and_clear_unused_extensions() {
        let original = SortRule {
            match_folders: true,
            catch_all: true,
            ..SortRule::default()
        };
        let folders = parse_rule(&original, &values("1", "ignored", "Folders")).unwrap();
        assert!(folders.match_folders && !folders.catch_all && folders.extensions.is_empty());
        let fallback = parse_rule(&original, &values("2", "ignored", "Other")).unwrap();
        assert!(!fallback.match_folders && fallback.catch_all && fallback.extensions.is_empty());
        let extension = parse_rule(&original, &values("0", "pdf", "PDFs")).unwrap();
        assert!(!extension.match_folders && !extension.catch_all);
    }
}
