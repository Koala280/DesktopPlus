use crate::{config, model::Settings};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Move {
    pub from: PathBuf,
    pub to: PathBuf,
    pub identity: Option<Identity>,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq, Hash)]
pub struct Identity {
    volume: u32,
    id: u64,
    created: u64,
}
pub fn identity(path: &Path) -> Option<Identity> {
    use windows_sys::Win32::{Foundation::*, Storage::FileSystem::*};
    unsafe {
        let handle = CreateFileW(
            crate::shell::wide(path).as_ptr(),
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            std::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            std::ptr::null_mut(),
        );
        if handle == INVALID_HANDLE_VALUE {
            return None;
        }
        let mut info: BY_HANDLE_FILE_INFORMATION = std::mem::zeroed();
        let ok = GetFileInformationByHandle(handle, &mut info);
        CloseHandle(handle);
        (ok != 0).then_some(Identity {
            volume: info.dwVolumeSerialNumber,
            id: ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64,
            created: ((info.ftCreationTime.dwHighDateTime as u64) << 32)
                | info.ftCreationTime.dwLowDateTime as u64,
        })
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Reference {
    pub path: PathBuf,
    pub identity: Identity,
}
#[derive(Serialize, Deserialize)]
pub struct Backup {
    pub version: u32,
    pub created: u64,
    pub reason: String,
    pub settings: Settings,
    pub moves: Vec<Move>,
    #[serde(default)]
    pub references: Vec<Reference>,
    #[serde(default)]
    pub languages: std::collections::HashMap<String, serde_json::Value>,
}

pub fn create(
    directory: &Path,
    settings: &Settings,
    reason: &str,
    moves: Vec<Move>,
    protect: Option<&Path>,
) -> Result<PathBuf, String> {
    let managed = crate::config::local_data().join("AutoSortStorage");
    create_with_references(
        directory,
        settings,
        reason,
        moves,
        protect,
        capture(&[crate::shell::desktop()], &managed),
    )
}

pub fn save(path: &Path, archive: &Backup) -> Result<(), String> {
    config::atomic_write(
        path,
        &serde_json::to_vec_pretty(archive).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}

pub fn create_with_references(
    directory: &Path,
    settings: &Settings,
    reason: &str,
    moves: Vec<Move>,
    protect: Option<&Path>,
    references: Vec<Reference>,
) -> Result<PathBuf, String> {
    let created = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let mut languages = std::collections::HashMap::new();
    for folder in [
        crate::config::app_data().join("DesktopPlus/Languages"),
        directory.parent().unwrap_or(directory).join("Languages"),
    ] {
        if let Ok(entries) = fs::read_dir(folder) {
            for entry in entries.filter_map(Result::ok) {
                if entry
                    .path()
                    .extension()
                    .is_some_and(|extension| extension == "json")
                    && entry
                        .metadata()
                        .is_ok_and(|meta| meta.len() < 4 * 1024 * 1024)
                {
                    if let Ok(value) = fs::read(entry.path()).and_then(|bytes| {
                        serde_json::from_slice(&bytes).map_err(std::io::Error::other)
                    }) {
                        languages.insert(entry.file_name().to_string_lossy().into_owned(), value);
                    }
                }
            }
        }
    }
    let archive = Backup {
        version: 1,
        created,
        reason: reason.into(),
        settings: settings.clone(),
        moves,
        references,
        languages,
    };
    let path = directory.join(format!("{created}-{}.json", crate::model::new_id()));
    save(&path, &archive)?;
    let mut files = list(directory)?;
    // The Rust app owns only its own restore points. Legacy archives remain available.
    files.retain(|file| file.parent() == Some(directory));
    while files.len() > 5 {
        let Some(index) = files
            .iter()
            .rposition(|file| file.as_path() != path && Some(file.as_path()) != protect)
        else {
            break;
        };
        fs::remove_file(files.remove(index)).map_err(|e| e.to_string())?;
    }
    Ok(path)
}
pub fn list(directory: &Path) -> Result<Vec<PathBuf>, String> {
    let mut directories = vec![directory.to_owned()];
    if directory
        .parent()
        .is_some_and(|parent| parent.file_name().is_some_and(|name| name == "Native"))
    {
        directories.push(crate::config::local_data().join("Backups"));
    }
    let mut files = Vec::new();
    for directory in directories {
        if directory.exists() {
            files.extend(
                fs::read_dir(directory)
                    .map_err(|e| e.to_string())?
                    .filter_map(Result::ok)
                    .map(|entry| entry.path())
                    .filter(|path| {
                        path.extension()
                            .is_some_and(|ext| ext == "json" || ext == "zip")
                    }),
            );
        }
    }
    files.sort_by_cached_key(|path| {
        std::cmp::Reverse(
            fs::metadata(path)
                .and_then(|meta| meta.modified())
                .unwrap_or(UNIX_EPOCH),
        )
    });
    Ok(files)
}
pub fn read(path: &Path) -> Result<Backup, String> {
    if path.extension().is_some_and(|extension| extension == "zip") {
        return read_legacy(path);
    }
    if fs::metadata(path).map_err(|e| e.to_string())?.len() > 64 * 1024 * 1024 {
        return Err("Backup exceeds the size limit.".into());
    }
    let mut value: serde_json::Value =
        serde_json::from_slice(&fs::read(path).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())?;
    if let Some(settings) = value.get_mut("settings") {
        config::repair_settings_text(settings);
    }
    let archive: Backup = serde_json::from_value(value).map_err(|e| e.to_string())?;
    if archive.version != 1 {
        return Err("Unsupported backup version.".into());
    }
    Ok(archive)
}
fn read_legacy(path: &Path) -> Result<Backup, String> {
    let mut archive = zip::ZipArchive::new(fs::File::open(path).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    fn entry(archive: &mut zip::ZipArchive<fs::File>, name: &str) -> Result<String, String> {
        let mut file = archive.by_name(name).map_err(|e| e.to_string())?;
        if file.size() > 32 * 1024 * 1024 {
            return Err("Backup entry is too large.".into());
        }
        let mut text = String::new();
        file.read_to_string(&mut text).map_err(|e| e.to_string())?;
        Ok(text)
    }
    let settings = config::parse(&entry(
        &mut archive,
        "user-data/roaming/DesktopPlus_Settings.json",
    )?)?;
    let manifest: serde_json::Value =
        serde_json::from_str(&entry(&mut archive, "manifest.json")?).map_err(|e| e.to_string())?;
    let mut references = Vec::new();
    if let Some(items) = manifest["FileReferences"].as_array() {
        for item in items {
            let Some(file_id) = item["FileId"].as_str() else {
                continue;
            };
            let Some((volume, id)) = file_id.split_once(':') else {
                continue;
            };
            let (Ok(volume), Ok(id), Some(created), Some(path)) = (
                u32::from_str_radix(volume, 16),
                u64::from_str_radix(id, 16),
                item["CreationTimeUtcTicks"].as_u64(),
                item["Path"].as_str(),
            ) else {
                continue;
            };
            let Some(created) = created.checked_sub(504911232000000000) else {
                continue;
            };
            references.push(Reference {
                path: path.into(),
                identity: Identity {
                    volume,
                    id,
                    created,
                },
            });
        }
    }
    let mut moves = Vec::new();
    if let Some(items) = manifest["DesktopMoves"].as_array() {
        for item in items {
            if let (Some(from), Some(to)) =
                (item["SourcePath"].as_str(), item["TargetPath"].as_str())
            {
                let identity = references
                    .iter()
                    .find(|reference| reference.path == Path::new(from))
                    .map(|reference| reference.identity.clone());
                moves.push(Move {
                    from: from.into(),
                    to: to.into(),
                    identity,
                });
            }
        }
    }
    let names: Vec<_> = archive
        .file_names()
        .filter(|name| {
            name.starts_with("user-data/roaming/DesktopPlus/Languages/") && name.ends_with(".json")
        })
        .map(str::to_owned)
        .collect();
    let mut languages = std::collections::HashMap::new();
    for name in names {
        if let Ok(text) = entry(&mut archive, &name) {
            if let Ok(value) = serde_json::from_str(&text) {
                if let Some(filename) = Path::new(&name).file_name() {
                    languages.insert(filename.to_string_lossy().into_owned(), value);
                }
            }
        }
    }
    Ok(Backup {
        version: 1,
        created: fs::metadata(path)
            .and_then(|metadata| metadata.modified())
            .unwrap_or(UNIX_EPOCH)
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs(),
        reason: manifest["Reason"]
            .as_str()
            .unwrap_or("Imported backup")
            .into(),
        settings,
        moves,
        references,
        languages,
    })
}

pub fn capture(roots: &[PathBuf], storage: &Path) -> Vec<Reference> {
    use std::os::windows::fs::MetadataExt;
    let mut paths = std::collections::HashSet::new();
    let entries = |folder: &Path| {
        fs::read_dir(folder)
            .map(|entries| {
                entries
                    .filter_map(Result::ok)
                    .map(|entry| entry.path())
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default()
    };
    for root in roots {
        paths.extend(entries(root));
    }
    for category in entries(storage) {
        if let Ok(meta) = category.symlink_metadata() {
            if meta.file_attributes() & 0x400 != 0 {
                continue;
            }
            if !meta.is_dir() {
                paths.insert(category);
                continue;
            }
            if category
                .file_name()
                .is_some_and(|name| name == ".DuplicateShortcuts")
            {
                for nested in entries(&category) {
                    if nested
                        .symlink_metadata()
                        .is_ok_and(|meta| meta.is_dir() && meta.file_attributes() & 0x400 == 0)
                    {
                        paths.extend(entries(&nested));
                    }
                }
            } else {
                paths.extend(entries(&category));
            }
        }
    }
    paths
        .into_iter()
        .filter_map(|path| identity(&path).map(|identity| Reference { path, identity }))
        .collect()
}

pub fn restore(backup: &Backup, directory: &Path) -> Result<Vec<String>, String> {
    let (mut skipped, restored) = restore_recorded_moves(&backup.moves);
    let current = capture(
        &[crate::shell::desktop()],
        &crate::config::local_data().join("AutoSortStorage"),
    );
    let mut candidates: std::collections::HashMap<Identity, Vec<PathBuf>> =
        std::collections::HashMap::new();
    for reference in current {
        candidates
            .entry(reference.identity)
            .or_default()
            .push(reference.path);
    }
    for reference in &backup.references {
        if restored.contains(&reference.path) {
            continue;
        }
        if identity(&reference.path).as_ref() == Some(&reference.identity) {
            continue;
        }
        if reference.path.exists() {
            skipped.push(format!(
                "Destination occupied: {}",
                reference.path.display()
            ));
            continue;
        }
        if let Some(paths) = candidates.get(&reference.identity) {
            if paths.len() == 1 {
                skipped.extend(restore_moves(&[Move {
                    from: reference.path.clone(),
                    to: paths[0].clone(),
                    identity: Some(reference.identity.clone()),
                }]));
                continue;
            }
        }
        skipped.push(format!(
            "Missing or ambiguous: {}",
            reference.path.display()
        ));
    }
    for (name, value) in &backup.languages {
        if Path::new(name).file_name() != Some(std::ffi::OsStr::new(name))
            || !name.ends_with(".json")
        {
            continue;
        }
        config::atomic_write(
            &directory.join("Languages").join(name),
            &serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(skipped)
}
pub fn restore_moves(moves: &[Move]) -> Vec<String> {
    restore_recorded_moves(moves).0
}
fn restore_recorded_moves(moves: &[Move]) -> (Vec<String>, std::collections::HashSet<PathBuf>) {
    let mut skipped = Vec::new();
    let mut restored = std::collections::HashSet::new();
    for item in moves.iter().rev() {
        if item.from.exists() {
            if identity(&item.from) != item.identity {
                skipped.push(format!("Destination occupied: {}", item.from.display()));
            } else {
                restored.insert(item.from.clone());
            }
            continue;
        }
        if item.identity.is_none() || identity(&item.to) != item.identity {
            skipped.push(format!("Missing or changed: {}", item.to.display()));
            continue;
        }
        if let Some(parent) = item.from.parent() {
            if let Err(error) = fs::create_dir_all(parent) {
                skipped.push(error.to_string());
                continue;
            }
        }
        // MoveFileEx without REPLACE_EXISTING never overwrites a new occupant.
        let moved = unsafe {
            windows_sys::Win32::Storage::FileSystem::MoveFileExW(
                crate::shell::wide(&item.to).as_ptr(),
                crate::shell::wide(&item.from).as_ptr(),
                windows_sys::Win32::Storage::FileSystem::MOVEFILE_COPY_ALLOWED,
            )
        };
        if moved == 0 {
            skipped.push(format!(
                "{}: {}",
                item.from.display(),
                std::io::Error::last_os_error()
            ));
        } else {
            restored.insert(item.from.clone());
        }
    }
    (skipped, restored)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reading_native_backup_repairs_labels_without_rewriting_archive_paths() {
        let directory = std::env::temp_dir().join(crate::model::new_id());
        fs::create_dir_all(&directory).unwrap();
        let path = directory.join("backup.json");
        let source = serde_json::json!({
            "version": 1,
            "created": 1,
            "reason": "encoding regression",
            "settings": {
                "Windows": [{"PanelTitle": "VerknÃƒÂ¼pfungen"}],
                "Presets": [{"Name": "RosÃƒÂ©"}]
            },
            "moves": [{"from": "C:\\Ã¼.txt", "to": "C:\\target.txt", "identity": null}]
        })
        .to_string();
        fs::write(&path, &source).unwrap();
        let archive = read(&path).unwrap();
        assert_eq!(archive.settings.windows[0].panel_title, "Verknüpfungen");
        assert_eq!(archive.settings.presets[0]["Name"], "Rosé");
        assert_eq!(archive.moves[0].from, Path::new("C:\\Ã¼.txt"));
        assert_eq!(fs::read_to_string(&path).unwrap(), source);
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn reads_legacy_archive_identities_and_languages_without_extracting_files() {
        use std::io::Write;
        let root = std::env::temp_dir().join(crate::model::new_id());
        fs::create_dir(&root).unwrap();
        let path = root.join("backup.zip");
        let mut writer = zip::ZipWriter::new(fs::File::create(&path).unwrap());
        let options = zip::write::SimpleFileOptions::default();
        for (name, value) in [
            (
                "user-data/roaming/DesktopPlus_Settings.json",
                serde_json::json!({"Windows":[],"Language":"de","PreservedSetting":17}),
            ),
            (
                "manifest.json",
                serde_json::json!({"Reason":"Legacy restore", "FileReferences":[{"Path":"C:\\Desktop\\report.txt", "FileId":"12345678:0000000000000042", "CreationTimeUtcTicks":504911232000000123u64}], "DesktopMoves":[{"SourcePath":"C:\\Desktop\\report.txt","TargetPath":"C:\\Sorted\\report.txt"}]}),
            ),
            (
                "user-data/roaming/DesktopPlus/Languages/fr.json",
                serde_json::json!({"code":"fr","translations":{"Loc.Save":"Enregistrer"}}),
            ),
            ("../../outside.txt", serde_json::json!("untrusted")),
        ] {
            writer.start_file(name, options).unwrap();
            writer.write_all(value.to_string().as_bytes()).unwrap();
        }
        writer.finish().unwrap();
        let archive = read(&path).unwrap();
        assert_eq!(archive.settings.language, "de");
        assert_eq!(
            archive.references[0].identity,
            Identity {
                volume: 0x12345678,
                id: 0x42,
                created: 123
            }
        );
        assert_eq!(
            archive.moves[0].identity,
            Some(archive.references[0].identity.clone())
        );
        assert_eq!(archive.languages.len(), 1);
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn retention_keeps_five_native_points_and_protects_selected_restore() {
        let root = std::env::temp_dir().join(crate::model::new_id());
        let settings = Settings::default();
        let selected =
            create_with_references(&root, &settings, "Selected", Vec::new(), None, Vec::new())
                .unwrap();
        for _ in 0..6 {
            create_with_references(
                &root,
                &settings,
                "Next",
                Vec::new(),
                Some(&selected),
                Vec::new(),
            )
            .unwrap();
        }
        assert!(selected.exists());
        assert_eq!(list(&root).unwrap().len(), 5);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn restore_does_not_overwrite_or_move_an_unrelated_replacement() {
        let directory = std::env::temp_dir().join(crate::model::new_id());
        fs::create_dir(&directory).unwrap();
        let from = directory.join("original.txt");
        let to = directory.join("moved.txt");
        fs::write(&from, "original").unwrap();
        let file_id = identity(&from);
        fs::rename(&from, &to).unwrap();
        fs::write(&from, "new occupant").unwrap();
        let moves = [Move {
            from: from.clone(),
            to: to.clone(),
            identity: file_id,
        }];
        assert_eq!(restore_moves(&moves).len(), 1);
        assert_eq!(fs::read_to_string(&from).unwrap(), "new occupant");
        fs::remove_file(&from).unwrap();
        assert!(restore_moves(&moves).is_empty());
        assert_eq!(fs::read_to_string(&from).unwrap(), "original");
        fs::remove_file(from).unwrap();
        fs::remove_dir(directory).unwrap();
    }
}
