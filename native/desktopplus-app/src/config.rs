use crate::model::Settings;
use serde_json::Value;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use windows_sys::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
};

pub fn app_data() -> PathBuf {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
}
pub fn local_data() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(app_data)
        .join("DesktopPlus")
}

pub struct Store {
    pub path: PathBuf,
    pub data: PathBuf,
}
impl Store {
    pub fn new(path: Option<PathBuf>) -> Self {
        match path {
            Some(path) => Self {
                data: path.parent().unwrap_or(Path::new(".")).join("data"),
                path,
            },
            None => Self {
                path: app_data()
                    .join("DesktopPlus")
                    .join("Native")
                    .join("settings.json"),
                data: local_data().join("Native"),
            },
        }
    }
    pub fn load(&self, import: bool) -> Result<Settings, String> {
        let legacy = app_data().join("DesktopPlus_Settings.json");
        let source = if self.path.exists() {
            &self.path
        } else if import && legacy.exists() {
            &legacy
        } else {
            let mut settings = Settings::default();
            settings.normalize();
            return Ok(settings);
        };
        let metadata = fs::metadata(source).map_err(|error| error.to_string())?;
        if metadata.len() > 32 * 1024 * 1024 {
            return Err("Settings exceed the 32 MiB limit.".into());
        }
        let bytes = fs::read_to_string(source)
            .map_err(|error| format!("Cannot read {}: {error}", source.display()))?;
        let mut settings = parse(&bytes)?;
        settings.normalize();
        if source == &legacy {
            atomic_write(&self.data.join("migration-original.json"), bytes.as_bytes())
                .map_err(|error| error.to_string())?;
        }
        Ok(settings)
    }
    pub fn save(&self, settings: &Settings) -> Result<(), String> {
        let bytes = serde_json::to_vec_pretty(settings).map_err(|error| error.to_string())?;
        atomic_write(&self.path, &bytes)
            .map_err(|error| format!("Cannot save {}: {error}", self.path.display()))
    }
}

pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temp = path.with_extension(format!("{}.tmp", crate::model::new_id()));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        let from: Vec<_> = temp.as_os_str().encode_wide().chain(Some(0)).collect();
        let to: Vec<_> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        if unsafe {
            MoveFileExW(
                from.as_ptr(),
                to.as_ptr(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result
}

pub fn parse(input: &str) -> Result<Settings, String> {
    let cleaned = clean_json(input.trim_start_matches('\u{feff}'));
    let mut value: Value = serde_json::from_str(&cleaned)
        .map_err(|error| format!("Invalid settings JSON: {error}"))?;
    if value.is_array() {
        value = serde_json::json!({"Windows": value});
    }
    normalize_json(&mut value);
    serde_json::from_value(value).map_err(|error| format!("Invalid settings: {error}"))
}

fn normalize_json(value: &mut Value) {
    const FIELDS: &str = "Windows Appearance Layouts Presets Language StartWithWindows CloseBehavior DesktopAutoSort GlobalShortcuts PanelId PanelTitle PanelType FolderPath DefaultFolderPath Left Top Width Height ExpandedHeight Zoom IsCollapsed IsHidden ShowHidden ShowFileExtensions OpenFoldersExternally OpenItemsOnSingleClick ViewMode PinnedItems Tabs ActiveTabIndex PresetName TabId TabName BackgroundColor HeaderColor AccentColor TextColor MutedTextColor FontFamily ItemFontSize TitleFontSize BackgroundOpacity Name Panels RuleId Enabled IsBuiltIn MatchFolders CatchAll RuleName TargetPanelName Extensions AutoSortEnabled Rules HidePanelsHotkey ForegroundPanelsHotkey";
    match value {
        Value::Object(map) => {
            for key in map.keys().cloned().collect::<Vec<_>>() {
                if let Some(canonical) = FIELDS
                    .split_whitespace()
                    .chain([
                        "HeaderStyle",
                        "AutoCheckUpdates",
                        "HeaderAlignment",
                        "GlassMode",
                        "GlassOpacity",
                        "RoundedCorners",
                        "HeaderControls",
                        "AppearanceOverrides",
                        "Search",
                        "SearchExpanded",
                        "Close",
                        "EmptyButton",
                        "Parent",
                    ])
                    .find(|field| field.eq_ignore_ascii_case(&key))
                {
                    if key != canonical {
                        if let Some(item) = map.remove(&key) {
                            map.entry(canonical).or_insert(item);
                        }
                    }
                    if map.get(canonical).is_some_and(Value::is_null) {
                        map.remove(canonical);
                        continue;
                    }
                    if matches!(
                        canonical,
                        "Left"
                            | "Top"
                            | "Width"
                            | "Height"
                            | "ExpandedHeight"
                            | "Zoom"
                            | "ItemFontSize"
                            | "BackgroundOpacity"
                    ) {
                        if let Some(text) = map.get(canonical).and_then(Value::as_str) {
                            let number = text.parse::<f64>().unwrap_or(0.0);
                            map.insert(
                                canonical.into(),
                                serde_json::json!(if number.is_finite() { number } else { 0.0 }),
                            );
                        }
                    }
                }
            }
            for item in map.values_mut() {
                normalize_json(item);
            }
        }
        Value::Array(items) => {
            items.retain(|item| !item.is_null());
            for item in items {
                normalize_json(item);
            }
        }
        _ => {}
    }
}

// Match the original settings reader's comments/trailing-comma allowance while
// preserving comment markers and escaped quotes inside string values.
fn clean_json(input: &str) -> String {
    let mut bytes = input.as_bytes().to_vec();
    let mut string = false;
    let mut escaped = false;
    let mut index = 0;
    while index < bytes.len() {
        let ch = bytes[index];
        if string {
            if escaped {
                escaped = false;
            } else if ch == b'\\' {
                escaped = true;
            } else if ch == b'"' {
                string = false;
            }
            index += 1;
            continue;
        }
        if ch == b'"' {
            string = true;
            index += 1;
            continue;
        }
        if ch == b'/' && bytes.get(index + 1) == Some(&b'/') {
            while index < bytes.len() && bytes[index] != b'\n' {
                bytes[index] = b' ';
                index += 1;
            }
        } else if ch == b'/' && bytes.get(index + 1) == Some(&b'*') {
            bytes[index] = b' ';
            bytes[index + 1] = b' ';
            index += 2;
            while index < bytes.len() {
                if bytes[index] == b'*' && bytes.get(index + 1) == Some(&b'/') {
                    bytes[index] = b' ';
                    bytes[index + 1] = b' ';
                    index += 2;
                    break;
                }
                if bytes[index] != b'\n' && bytes[index] != b'\r' {
                    bytes[index] = b' ';
                }
                index += 1;
            }
        } else {
            index += 1;
        }
    }
    string = false;
    escaped = false;
    for index in 0..bytes.len() {
        let ch = bytes[index];
        if string {
            if escaped {
                escaped = false;
            } else if ch == b'\\' {
                escaped = true;
            } else if ch == b'"' {
                string = false;
            }
        } else if ch == b'"' {
            string = true;
        } else if ch == b','
            && bytes[index + 1..]
                .iter()
                .find(|ch| !ch.is_ascii_whitespace())
                .is_some_and(|ch| matches!(ch, b']' | b'}'))
        {
            bytes[index] = b' ';
        }
    }
    String::from_utf8(bytes).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn imports_case_insensitive_commented_legacy_json() {
        let data = "/* config */ {\"windows\": [{\"FolderPath\":\"C:\\\\files\", \"width\":\"420\",\"Tabs\":null,},], // yes\n \"Language\": \"de\",}";
        let mut settings = parse(data).unwrap();
        settings.normalize();
        assert_eq!(settings.language, "de");
        assert_eq!(settings.windows[0].width, 420.0);
    }
    #[test]
    fn does_not_strip_comment_characters_inside_strings() {
        let settings = parse(r#"{"Language":"https://host/a/*b*/"}"#).unwrap();
        assert_eq!(settings.language, "https://host/a/*b*/");
    }
    #[test]
    fn atomic_save_replaces_existing_file_and_failed_parse_is_reported() {
        let directory = std::env::temp_dir().join(crate::model::new_id());
        let path = directory.join("settings.json");
        atomic_write(&path, b"one").unwrap();
        atomic_write(&path, b"two").unwrap();
        assert_eq!(fs::read(&path).unwrap(), b"two");
        assert!(parse("{broken").is_err());
        fs::remove_file(path).unwrap();
        fs::remove_dir(directory).unwrap();
    }
}
