use crate::{
    backup,
    model::{Settings, SortRule},
    shell,
};
use std::{
    collections::{HashMap, HashSet},
    fs,
    os::windows::fs::MetadataExt,
    path::{Path, PathBuf},
};

pub struct Report {
    pub moved: usize,
    pub duplicates: usize,
    pub errors: Vec<String>,
    pub targets: HashMap<String, PathBuf>,
    pub remaps: Vec<(PathBuf, PathBuf)>,
}
struct Planned {
    movement: backup::Move,
    target: String,
    canonical: Option<PathBuf>,
}

pub fn run(
    settings: &Settings,
    roots: &[PathBuf],
    storage: &Path,
    backups: &Path,
) -> Result<Report, String> {
    let mut report = Report {
        moved: 0,
        duplicates: 0,
        errors: Vec::new(),
        targets: HashMap::new(),
        remaps: Vec::new(),
    };
    let rules: Vec<_> = settings
        .desktop_auto_sort
        .rules
        .iter()
        .filter(|rule| rule.enabled)
        .collect();
    let mut destinations = HashMap::new();
    for panel in &settings.windows {
        for tab in panel.tabs.as_deref().unwrap_or_default() {
            if !tab.pinned() && !tab.folder_path.is_empty() {
                destinations.insert(tab.tab_name.to_lowercase(), PathBuf::from(&tab.folder_path));
            }
        }
        if let Some(tab) = panel
            .tabs
            .as_ref()
            .and_then(|tabs| tabs.get(panel.active_tab_index))
        {
            if !tab.pinned() && !tab.folder_path.is_empty() {
                destinations.insert(
                    panel.panel_title.to_lowercase(),
                    PathBuf::from(&tab.folder_path),
                );
            }
        }
    }
    let mut plan = Vec::new();
    let mut reserved = HashSet::new();
    let mut links: HashMap<PathBuf, HashMap<String, PathBuf>> = HashMap::new();
    for rule in &rules {
        let name = rule.target_panel_name.trim();
        if name.is_empty() {
            continue;
        }
        let folder = destinations
            .get(&name.to_lowercase())
            .cloned()
            .unwrap_or_else(|| storage.join(sanitize(name)));
        if roots
            .iter()
            .any(|root| folder == *root || root.starts_with(&folder))
        {
            return Err("A sort target cannot contain the source desktop.".into());
        }
        report.targets.insert(name.to_owned(), folder.clone());
        if links.contains_key(&folder) {
            continue;
        }
        let mut map = HashMap::new();
        if let Ok(entries) = fs::read_dir(&folder) {
            let mut entries: Vec<_> = entries
                .filter_map(Result::ok)
                .map(|entry| entry.path())
                .collect();
            entries.sort();
            for path in entries {
                if let Some(key) = shortcut_key(&path) {
                    if let Some(canonical) = map.get(&key).cloned() {
                        let destination = unique(
                            &storage.join(".DuplicateShortcuts").join(sanitize(name)),
                            path.file_name().unwrap_or_default(),
                            &mut reserved,
                        );
                        plan.push(Planned {
                            movement: backup::Move {
                                identity: backup::identity(&path),
                                from: path,
                                to: destination,
                            },
                            target: name.into(),
                            canonical: Some(canonical),
                        });
                    } else {
                        map.insert(key, path);
                    }
                }
            }
        }
        links.insert(folder, map);
    }
    for root in roots {
        let entries = match fs::read_dir(root) {
            Ok(entries) => entries,
            Err(error) => {
                report.errors.push(format!("{}: {error}", root.display()));
                continue;
            }
        };
        for entry in entries.filter_map(Result::ok) {
            let path = entry.path();
            let name = entry.file_name();
            if matches!(
                name.to_string_lossy().to_ascii_lowercase().as_str(),
                "desktop.ini" | "thumbs.db" | "desktopplus organized"
            ) {
                continue;
            }
            let metadata = match entry.metadata() {
                Ok(meta) => meta,
                Err(error) => {
                    report.errors.push(error.to_string());
                    continue;
                }
            };
            if metadata.is_dir() && metadata.file_attributes() & (4 | 0x400) != 0 {
                continue;
            }
            let Some(rule) = resolve(&rules, &path, metadata.is_dir()) else {
                continue;
            };
            let target = rule.target_panel_name.trim();
            let Some(folder) = report.targets.get(target) else {
                continue;
            };
            if report
                .targets
                .values()
                .any(|target| target.starts_with(&path))
            {
                continue;
            }
            let key = shortcut_key(&path);
            let canonical = key
                .as_ref()
                .and_then(|key| links.get(folder).and_then(|map| map.get(key)))
                .cloned();
            let destination = if canonical.is_some() {
                unique(
                    &storage.join(".DuplicateShortcuts").join(sanitize(target)),
                    &name,
                    &mut reserved,
                )
            } else {
                unique(folder, &name, &mut reserved)
            };
            if canonical.is_none() {
                if let Some(key) = key {
                    links
                        .entry(folder.clone())
                        .or_default()
                        .insert(key, destination.clone());
                }
            }
            plan.push(Planned {
                movement: backup::Move {
                    identity: backup::identity(&path),
                    from: path,
                    to: destination,
                },
                target: target.into(),
                canonical,
            });
        }
    }
    if plan.is_empty() {
        return Ok(report);
    }
    let archive_path = backup::create_with_references(
        backups,
        settings,
        "Before auto-sort",
        plan.iter().map(|item| item.movement.clone()).collect(),
        None,
        backup::capture(roots, storage),
    )?;
    let mut archive = backup::read(&archive_path)?;
    for (number, item) in plan.into_iter().enumerate() {
        let movement = item.movement;
        if movement.identity.is_none() || backup::identity(&movement.from) != movement.identity {
            report.errors.push(format!(
                "File changed or cannot be identified: {}",
                movement.from.display()
            ));
            continue;
        }
        if let Some(canonical) = &item.canonical {
            if shortcut_key(canonical).is_none()
                || shortcut_key(canonical) != shortcut_key(&movement.from)
            {
                report
                    .errors
                    .push(format!("Shortcut changed: {}", movement.from.display()));
                continue;
            }
        }
        if let Some(parent) = movement.to.parent() {
            if let Err(error) = fs::create_dir_all(parent) {
                report.errors.push(error.to_string());
                continue;
            }
        }
        // No replacement flag: a file arriving since planning is never overwritten.
        if unsafe {
            windows_sys::Win32::Storage::FileSystem::MoveFileExW(
                shell::wide(&movement.from).as_ptr(),
                shell::wide(&movement.to).as_ptr(),
                windows_sys::Win32::Storage::FileSystem::MOVEFILE_COPY_ALLOWED,
            )
        } == 0
        {
            report.errors.push(format!(
                "{}: {}",
                movement.from.display(),
                shell::error_text()
            ));
            continue;
        }
        if item.canonical.is_some() {
            report.duplicates += 1;
        } else {
            report.moved += 1;
        }
        let new_identity = backup::identity(&movement.to);
        if new_identity != movement.identity {
            archive.moves[number].identity = new_identity.clone();
            if let Some(new_identity) = new_identity {
                for reference in &mut archive.references {
                    if Some(&reference.identity) == movement.identity.as_ref() {
                        reference.identity = new_identity.clone();
                    }
                }
            }
            if let Err(error) = backup::save(&archive_path, &archive) {
                report.errors.push(format!(
                    "Cannot update restore point after moving {}: {error}",
                    movement.to.display()
                ));
                break;
            }
        }
        report
            .remaps
            .push((movement.from, item.canonical.unwrap_or(movement.to)));
        let _ = item.target;
    }
    Ok(report)
}
/// Resolve a sample name using exactly the same enabled rules as a real sort.
pub fn matching_rule<'a>(
    rules: &'a [SortRule],
    path: &Path,
    directory: bool,
) -> Option<&'a SortRule> {
    resolve(
        &rules.iter().filter(|rule| rule.enabled).collect::<Vec<_>>(),
        path,
        directory,
    )
}

fn resolve<'a>(rules: &[&'a SortRule], path: &Path, directory: bool) -> Option<&'a SortRule> {
    if directory {
        return rules.iter().find(|rule| rule.match_folders).copied();
    }
    let extension = path
        .extension()
        .map(|ext| format!(".{}", ext.to_string_lossy()))
        .unwrap_or_default();
    let matches = |rule: &&SortRule| {
        !rule.match_folders
            && !rule.catch_all
            && rule
                .extensions
                .iter()
                .any(|ext| ext.eq_ignore_ascii_case(&extension))
    };
    rules
        .iter()
        .filter(|rule| !rule.is_built_in)
        .find(|rule| matches(rule))
        .copied()
        .or_else(|| {
            rules
                .iter()
                .filter(|rule| rule.is_built_in)
                .find(|rule| matches(rule))
                .copied()
        })
        .or_else(|| rules.iter().find(|rule| rule.catch_all).copied())
}
fn sanitize(name: &str) -> String {
    let value: String = name
        .chars()
        .map(|ch| {
            if ch.is_control() || "<>:\"/\\|?*".contains(ch) {
                '_'
            } else {
                ch
            }
        })
        .collect();
    let value = value.trim().trim_end_matches('.');
    if value.is_empty()
        || matches!(
            value.to_ascii_uppercase().as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "COM1" | "LPT1"
        )
    {
        "Sorted".into()
    } else {
        value.into()
    }
}
fn unique(folder: &Path, name: &std::ffi::OsStr, reserved: &mut HashSet<String>) -> PathBuf {
    let original = Path::new(name);
    let stem = original.file_stem().unwrap_or(name).to_string_lossy();
    let extension = original
        .extension()
        .map(|ext| format!(".{}", ext.to_string_lossy()))
        .unwrap_or_default();
    for number in 0u64.. {
        let candidate = if number == 0 {
            folder.join(name)
        } else {
            folder.join(format!("{stem} ({number}){extension}"))
        };
        if !candidate.exists() && reserved.insert(candidate.to_string_lossy().to_lowercase()) {
            return candidate;
        }
    }
    unreachable!()
}

fn shortcut_key(path: &Path) -> Option<String> {
    let extension = path.extension()?.to_string_lossy().to_ascii_lowercase();
    if extension == "url" {
        let text = fs::read_to_string(path).ok()?;
        let mut fields: Vec<_> = text
            .lines()
            .filter_map(|line| line.split_once('='))
            .filter(|(key, _)| {
                matches!(
                    key.trim().to_ascii_lowercase().as_str(),
                    "url" | "iconfile" | "iconindex" | "hotkey"
                )
            })
            .map(|(key, value)| (key.trim().to_ascii_lowercase(), value.trim().to_owned()))
            .collect();
        if !fields.iter().any(|(key, _)| key == "url") {
            return None;
        }
        fields.sort();
        return serde_json::to_string(&fields).ok();
    }
    if extension != "lnk" {
        return None;
    }
    use windows::{
        Win32::{System::Com::*, UI::Shell::*},
        core::{Interface, PCWSTR},
    };
    unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).ok()?;
        let file: IPersistFile = link.cast().ok()?;
        file.Load(PCWSTR(shell::wide(path).as_ptr()), STGM_READ)
            .ok()?;
        let mut target = vec![0u16; 32768];
        let mut arguments = vec![0u16; 32768];
        let mut directory = vec![0u16; 32768];
        let mut icon = vec![0u16; 32768];
        let mut icon_index = 0;
        link.GetPath(&mut target, std::ptr::null_mut(), SLGP_RAWPATH.0 as u32)
            .ok()?;
        link.GetArguments(&mut arguments).ok()?;
        link.GetWorkingDirectory(&mut directory).ok()?;
        link.GetIconLocation(&mut icon, &mut icon_index).ok()?;
        let take = |value: &[u16]| {
            String::from_utf16_lossy(
                &value[..value.iter().position(|ch| *ch == 0).unwrap_or(value.len())],
            )
        };
        let target = take(&target);
        if target.is_empty() {
            return None;
        }
        serde_json::to_string(&(
            target.to_lowercase(),
            take(&arguments),
            take(&directory).to_lowercase(),
            take(&icon).to_lowercase(),
            icon_index,
            link.GetHotkey().ok()?,
            link.GetShowCmd().ok()?.0,
        ))
        .ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn duplicate_shortcuts_are_recoverable_and_target_folders_are_not_sorted() {
        let root = std::env::temp_dir().join(crate::model::new_id());
        let desktop = root.join("desktop");
        let storage = root.join("storage");
        let backups = root.join("backups");
        fs::create_dir_all(desktop.join("Images")).unwrap();
        for name in ["one.url", "two.url"] {
            fs::write(
                desktop.join(name),
                "[InternetShortcut]\nURL=https://example.com/\n",
            )
            .unwrap();
        }
        let mut settings = Settings::default();
        settings.normalize();
        settings
            .windows
            .push(crate::model::Panel::folder(&desktop.join("Images")));
        let report = run(
            &settings,
            std::slice::from_ref(&desktop),
            &storage,
            &backups,
        )
        .unwrap();
        assert_eq!(report.moved, 1);
        assert_eq!(report.duplicates, 1);
        assert!(desktop.join("Images").is_dir());
        assert!(report.errors.is_empty());
        let archive = backup::read(&backup::list(&backups).unwrap()[0]).unwrap();
        assert!(backup::restore_moves(&archive.moves).is_empty());
        assert!(desktop.join("one.url").is_file());
        assert!(desktop.join("two.url").is_file());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn custom_extensions_win_and_folders_do_not_fall_into_catch_all() {
        let mut rules = crate::model::default_rules();
        rules.push(SortRule {
            extensions: vec![".PDF".into()],
            target_panel_name: "Custom".into(),
            ..SortRule::default()
        });
        let active: Vec<_> = rules.iter().collect();
        assert_eq!(
            resolve(&active, Path::new("report.pdf"), false)
                .unwrap()
                .target_panel_name,
            "Custom"
        );
        assert!(
            resolve(&active, Path::new("folder.pdf"), true)
                .unwrap()
                .match_folders
        );
    }
    #[test]
    fn preview_uses_enabled_rules_and_the_same_priority_as_sorting() {
        let mut rules = crate::model::default_rules();
        rules.push(SortRule {
            extensions: vec![".pdf".into()],
            target_panel_name: "Custom PDFs".into(),
            ..SortRule::default()
        });
        rules.push(SortRule {
            extensions: vec![".pdf".into()],
            target_panel_name: "Later PDFs".into(),
            ..SortRule::default()
        });
        assert_eq!(
            matching_rule(&rules, Path::new("REPORT.PDF"), false)
                .unwrap()
                .target_panel_name,
            "Custom PDFs"
        );
        rules[10].enabled = false;
        assert_eq!(
            matching_rule(&rules, Path::new("report.pdf"), false)
                .unwrap()
                .target_panel_name,
            "Later PDFs"
        );
        rules[11].enabled = false;
        assert_eq!(
            matching_rule(&rules, Path::new("report.pdf"), false)
                .unwrap()
                .target_panel_name,
            "Documents"
        );
        assert!(
            matching_rule(&rules, Path::new("report.unknown"), false)
                .unwrap()
                .catch_all
        );
        rules[0].enabled = false;
        assert!(matching_rule(&rules, Path::new("folder.pdf"), true).is_none());
        for rule in &mut rules {
            rule.enabled = false;
        }
        assert!(matching_rule(&rules, Path::new("report.pdf"), false).is_none());
        assert!(matching_rule(&[], Path::new("report.pdf"), false).is_none());
    }
    #[test]
    fn sort_creates_restore_point_and_never_overwrites_collisions() {
        let root = std::env::temp_dir().join(crate::model::new_id());
        let desktop = root.join("desktop");
        let storage = root.join("storage");
        let backups = root.join("backups");
        fs::create_dir_all(&desktop).unwrap();
        fs::create_dir_all(storage.join("Documents")).unwrap();
        fs::write(desktop.join("report.pdf"), "new").unwrap();
        fs::write(storage.join("Documents/report.pdf"), "existing").unwrap();
        let mut settings = Settings::default();
        settings.normalize();
        let report = run(
            &settings,
            std::slice::from_ref(&desktop),
            &storage,
            &backups,
        )
        .unwrap();
        assert_eq!(report.moved, 1);
        assert_eq!(
            fs::read_to_string(storage.join("Documents/report.pdf")).unwrap(),
            "existing"
        );
        let backup = backup::read(&backup::list(&backups).unwrap()[0]).unwrap();
        assert!(backup::restore_moves(&backup.moves).is_empty());
        assert_eq!(
            fs::read_to_string(desktop.join("report.pdf")).unwrap(),
            "new"
        );
        fs::remove_dir_all(root).unwrap();
    }
}
