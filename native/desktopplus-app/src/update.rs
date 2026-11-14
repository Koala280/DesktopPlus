//! Native release updates. Only a verified, architecture-matched Rust executable
//! is staged. A copied helper replaces it after the owning process has exited.
mod http;
#[cfg(test)]
mod tests;

use crate::{
    config::{self, Store},
    model::Settings,
};
use semver::Version;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{Cursor, Read},
    os::windows::{ffi::OsStrExt, process::CommandExt},
    path::{Path, PathBuf},
    process::Command,
    sync::mpsc::{self, Receiver},
    time::Duration,
};
use windows_sys::Win32::{
    Foundation::*, Storage::FileSystem::ReplaceFileW, System::Threading::*,
    UI::WindowsAndMessaging::PostMessageW,
};

const API: &str = "https://api.github.com/repos/Koala280/DesktopPlus/releases/latest";
const ASSET_ROOT: &str = "https://github.com/Koala280/DesktopPlus/releases/download/";
const METADATA_LIMIT: usize = 2 * 1024 * 1024;
const PACKAGE_LIMIT: usize = 128 * 1024 * 1024;
pub const INTERVAL_MS: u32 = 6 * 60 * 60 * 1000;
pub const RETRY_MS: u32 = 15 * 60 * 1000;

#[derive(Deserialize)]
struct Release {
    tag_name: String,
    draft: bool,
    prerelease: bool,
    assets: Vec<Asset>,
}
#[derive(Deserialize)]
struct Asset {
    name: String,
    browser_download_url: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    version: String,
    runtime_identifier: String,
    implementation: String,
    configuration: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Pending {
    pub version: String,
    directory: String,
    sha256: String,
    target: PathBuf,
}
#[derive(Serialize, Deserialize)]
struct Job {
    pending: Pending,
    parent: u32,
    original_sha256: String,
    settings: Option<PathBuf>,
    startup: bool,
}
pub struct ResultMessage {
    pub manual: bool,
    pub result: Result<Option<Pending>, String>,
}
#[derive(Default)]
pub struct Updater {
    receiver: Option<Receiver<ResultMessage>>,
    pub status: String,
}
impl Updater {
    pub fn start(&mut self, hwnd: usize, root: PathBuf, manual: bool) -> Result<bool, String> {
        if self.receiver.is_some() {
            return Ok(false);
        }
        let target = std::env::current_exe().map_err(|e| e.to_string())?;
        let (sender, receiver) = mpsc::channel();
        std::thread::Builder::new()
            .name("desktopplus-updates".into())
            .spawn(move || {
                let result =
                    check_and_stage(&root, &target, env!("DESKTOPPLUS_VERSION"), &http::get);
                let _ = sender.send(ResultMessage { manual, result });
                unsafe {
                    PostMessageW(hwnd as _, crate::worker::WAKE, 0, 0);
                }
            })
            .map_err(|e| e.to_string())?;
        self.receiver = Some(receiver);
        Ok(true)
    }
    pub fn poll(&mut self) -> Option<ResultMessage> {
        let receiver = self.receiver.as_ref()?;
        let result = match receiver.try_recv() {
            Ok(value) => value,
            Err(mpsc::TryRecvError::Empty) => return None,
            Err(mpsc::TryRecvError::Disconnected) => ResultMessage {
                manual: false,
                result: Err("Update worker stopped unexpectedly.".into()),
            },
        };
        self.receiver = None;
        Some(result)
    }
}

fn runtime() -> &'static str {
    match std::env::consts::ARCH {
        "x86_64" => "win-x64",
        "aarch64" => "win-arm64",
        "x86" => "win-x86",
        _ => "unsupported",
    }
}
fn version(value: &str) -> Result<Version, String> {
    Version::parse(value.strip_prefix(['v', 'V']).unwrap_or(value)).map_err(|e| e.to_string())
}
fn newer(current: &str, remote: &str) -> Result<bool, String> {
    let current = version(current)?;
    let remote = version(remote)?;
    Ok(remote.pre.is_empty() && remote.cmp_precedence(&current).is_gt())
}
fn hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn read_limited(path: &Path, limit: usize) -> Result<Vec<u8>, String> {
    let file = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    file.take(limit as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > limit {
        return Err("Update file exceeds the size limit.".into());
    }
    Ok(bytes)
}
fn asset<'a>(release: &'a Release, name: &str) -> Result<&'a str, String> {
    let prefix = format!("{ASSET_ROOT}{}/", release.tag_name);
    let mut matches = release.assets.iter().filter(|asset| asset.name == name);
    let found = matches
        .next()
        .ok_or_else(|| format!("Release is missing {name}."))?;
    if matches.next().is_some()
        || !found.browser_download_url.starts_with(&prefix)
        || found.browser_download_url[prefix.len()..] != *name
    {
        return Err("Invalid or ambiguous release asset URL.".into());
    }
    Ok(&found.browser_download_url)
}
fn checksum(text: &str, name: &str) -> Result<String, String> {
    let mut found = None;
    for line in text.trim_start_matches('\u{feff}').lines() {
        let mut fields = line.split_whitespace();
        if let (Some(digest), Some(file)) = (fields.next(), fields.next()) {
            if file.trim_start_matches('*') == name {
                if found.is_some()
                    || digest.len() != 64
                    || !digest.bytes().all(|b| b.is_ascii_hexdigit())
                    || fields.next().is_some()
                {
                    return Err("Invalid or duplicate release checksum.".into());
                }
                found = Some(digest.to_ascii_lowercase());
            }
        }
    }
    found.ok_or_else(|| "Release checksum is missing.".into())
}
fn json<T: serde::de::DeserializeOwned>(bytes: &[u8]) -> Result<T, String> {
    // PowerShell release metadata may start with a UTF-8 BOM.
    serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes))
        .map_err(|e| e.to_string())
}
fn check_and_stage(
    root: &Path,
    target: &Path,
    current: &str,
    get: &impl Fn(&str, usize) -> Result<Vec<u8>, String>,
) -> Result<Option<Pending>, String> {
    let release: Release = json(&get(API, METADATA_LIMIT)?)?;
    if release.draft || release.prerelease || !newer(current, &release.tag_name)? {
        return Ok(None);
    }
    let release_version = version(&release.tag_name)?.to_string();
    if let Ok(cached) = read_limited(&root.join("pending.json"), METADATA_LIMIT)
        .and_then(|bytes| json::<Pending>(&bytes))
    {
        if cached.version == release_version
            && cached.target == target
            && cached.verify(root).is_ok()
        {
            return Ok(Some(cached));
        }
    }
    let manifest: Manifest = json(&get(
        asset(&release, "release-manifest.json")?,
        METADATA_LIMIT,
    )?)?;
    if manifest.version != release_version
        || manifest.runtime_identifier != runtime()
        || manifest.implementation != "Rust / Win32"
        || manifest.configuration != "Release"
    {
        return Err("No compatible native release is available for this build.".into());
    }
    let name = format!("DesktopPlus-{release_version}-{}-portable.zip", runtime());
    let sums = get(asset(&release, "SHA256SUMS.txt")?, METADATA_LIMIT)?;
    let expected = checksum(
        std::str::from_utf8(&sums).map_err(|e| e.to_string())?,
        &name,
    )?;
    let package = get(asset(&release, &name)?, PACKAGE_LIMIT)?;
    if hash(&package) != expected {
        return Err("Update checksum verification failed.".into());
    }
    let executable = extract_executable(package)?;
    let pending = Pending {
        version: release_version,
        directory: crate::model::new_id(),
        sha256: hash(&executable),
        target: target.to_owned(),
    };
    config::atomic_write(
        &root.join(&pending.directory).join("DesktopPlus.exe"),
        &executable,
    )
    .map_err(|e| e.to_string())?;
    Ok(Some(pending))
}

fn extract_executable(package: Vec<u8>) -> Result<Vec<u8>, String> {
    let mut zip = zip::ZipArchive::new(Cursor::new(package)).map_err(|e| e.to_string())?;
    // No arbitrary archive paths are extracted, including symlinks or traversal.
    if zip
        .file_names()
        .filter(|name| *name == "DesktopPlus.exe")
        .count()
        != 1
    {
        return Err("Release must contain exactly one DesktopPlus.exe.".into());
    }
    let file = zip.by_name("DesktopPlus.exe").map_err(|e| e.to_string())?;
    if !file.is_file()
        || file
            .unix_mode()
            .is_some_and(|mode| mode & 0o170000 == 0o120000)
        || file.size() > PACKAGE_LIMIT as u64
    {
        return Err("Invalid executable in update package.".into());
    }
    let mut bytes = Vec::new();
    file.take(PACKAGE_LIMIT as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > PACKAGE_LIMIT {
        return Err("Update executable exceeds the size limit.".into());
    }
    validate_pe(&bytes)?;
    Ok(bytes)
}
fn validate_pe(bytes: &[u8]) -> Result<(), String> {
    let invalid = || "Update is not a compatible native Windows executable.".to_owned();
    let u16_at = |offset| {
        bytes
            .get(offset..offset + 2)
            .map(|b| u16::from_le_bytes(b.try_into().unwrap()))
            .ok_or_else(invalid)
    };
    let u32_at = |offset| {
        bytes
            .get(offset..offset + 4)
            .map(|b| u32::from_le_bytes(b.try_into().unwrap()))
            .ok_or_else(invalid)
    };
    if bytes.get(..2) != Some(b"MZ") {
        return Err(invalid());
    }
    let pe = u32_at(0x3c)? as usize;
    if pe > bytes.len().saturating_sub(264) || bytes.get(pe..pe + 4) != Some(b"PE\0\0") {
        return Err(invalid());
    }
    let machine = match runtime() {
        "win-x64" => 0x8664,
        "win-arm64" => 0xaa64,
        "win-x86" => 0x14c,
        _ => 0,
    };
    let magic = if runtime() == "win-x86" { 0x10b } else { 0x20b };
    let directories = pe + 24 + if magic == 0x20b { 112 } else { 96 };
    if u16_at(pe + 4)? != machine
        || u16_at(pe + 24)? != magic
        || u16_at(pe + 22)? & 0x2000 != 0
        || u32_at(directories + 14 * 8)? != 0
        || u32_at(directories + 14 * 8 + 4)? != 0
    {
        return Err(invalid());
    }
    Ok(())
}

impl Pending {
    fn executable(&self, root: &Path) -> Result<PathBuf, String> {
        if self.directory.is_empty()
            || !self
                .directory
                .bytes()
                .all(|b| b.is_ascii_hexdigit() || b == b'-')
        {
            return Err("Invalid update staging directory.".into());
        }
        Ok(root.join(&self.directory).join("DesktopPlus.exe"))
    }
    fn verify(&self, root: &Path) -> Result<PathBuf, String> {
        let path = self.executable(root)?;
        let bytes = read_limited(&path, PACKAGE_LIMIT)?;
        if hash(&bytes) != self.sha256 {
            return Err("Staged update checksum verification failed.".into());
        }
        validate_pe(&bytes)?;
        Ok(path)
    }
}
pub fn remember(root: &Path, pending: &Pending) -> Result<(), String> {
    config::atomic_write(
        &root.join("pending.json"),
        &serde_json::to_vec(pending).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}
pub fn discard(root: &Path, pending: &Pending) {
    if let Ok(path) = pending.executable(root) {
        let _ = fs::remove_file(&path);
        if let Some(parent) = path.parent() {
            let _ = fs::remove_dir(parent);
        }
    }
}
pub fn clear_pending(root: &Path) {
    let _ = fs::remove_file(root.join("pending.json"));
}
pub fn pending(root: &Path) -> Result<Option<Pending>, String> {
    let path = root.join("pending.json");
    if !path.exists() {
        return Ok(None);
    }
    let pending: Pending = json(&read_limited(&path, METADATA_LIMIT)?)?;
    if !newer(env!("DESKTOPPLUS_VERSION"), &pending.version)? {
        clear_pending(root);
        discard(root, &pending);
        return Ok(None);
    }
    if pending.target != std::env::current_exe().map_err(|e| e.to_string())? {
        return Ok(None);
    }
    pending.verify(root)?;
    Ok(Some(pending))
}

pub fn launch(
    store: &Store,
    settings: &Settings,
    pending: &Pending,
    startup: bool,
) -> Result<(), String> {
    let root = store.data.join("Updates");
    pending.verify(&root)?;
    let current = std::env::current_exe().map_err(|e| e.to_string())?;
    if pending.target != current || !newer(env!("DESKTOPPLUS_VERSION"), &pending.version)? {
        return Err("The prepared update does not match this application.".into());
    }
    store.save(settings)?;
    let directory = root.join(&pending.directory);
    // Preserve configuration and the exact previous binary before replacing anything.
    config::atomic_write(
        &directory.join("settings-before-update.json"),
        &fs::read(&store.path).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    let helper = directory.join("update-helper.exe");
    fs::copy(&current, &helper).map_err(|e| e.to_string())?;
    let job = Job {
        pending: pending.clone(),
        parent: std::process::id(),
        original_sha256: hash(&read_limited(&current, PACKAGE_LIMIT)?),
        settings: restart_settings(store)?,
        startup,
    };
    let job_path = directory.join("job.json");
    config::atomic_write(
        &job_path,
        &serde_json::to_vec(&job).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    // Consume the automatic marker before starting: a failed installation must
    // never trap the user in a restart loop. A manual check can retry it.
    clear_pending(&root);
    Command::new(helper)
        .arg("--apply-update")
        .arg(&job_path)
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map_err(|e| format!("Cannot start the update helper: {e}"))?;
    Ok(())
}

fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}
fn restart_settings(store: &Store) -> Result<Option<PathBuf>, String> {
    let default = Store::new(None);
    // Passing the default settings path as an override changes Store's data
    // directory. Keep the original storage mode across an update/restart.
    if store.path == default.path && store.data == default.data {
        Ok(None)
    } else {
        std::path::absolute(&store.path)
            .map(Some)
            .map_err(|e| e.to_string())
    }
}
fn restart(job: &Job) -> Result<(), String> {
    let mut command = Command::new(&job.pending.target);
    if let Some(settings) = &job.settings {
        command.arg("--settings").arg(settings);
    }
    if job.startup {
        command.arg("--startup");
    }
    command
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("Cannot restart DesktopPlus: {e}"))
}
struct AppLock(HANDLE);
impl AppLock {
    fn acquire() -> Result<Self, String> {
        unsafe {
            let handle = CreateMutexW(
                std::ptr::null(),
                0,
                crate::shell::wide("Local\\DesktopPlus.Native").as_ptr(),
            );
            if handle.is_null() {
                return Err(crate::shell::error_text());
            }
            if GetLastError() == ERROR_ALREADY_EXISTS {
                CloseHandle(handle);
                return Err("Another DesktopPlus instance is running; update cancelled.".into());
            }
            Ok(Self(handle))
        }
    }
}
impl Drop for AppLock {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}
fn replace(target: &Path, replacement: &Path, backup: Option<&Path>) -> Result<(), String> {
    let backup = backup.map(wide_path);
    if unsafe {
        ReplaceFileW(
            wide_path(target).as_ptr(),
            wide_path(replacement).as_ptr(),
            backup.as_ref().map_or(std::ptr::null(), |b| b.as_ptr()),
            0,
            std::ptr::null(),
            std::ptr::null(),
        )
    } == 0
    {
        return Err(crate::shell::error_text());
    }
    Ok(())
}
fn install(
    target: &Path,
    staged: &Path,
    id: &str,
    start: impl FnOnce() -> Result<(), String>,
) -> Result<(), String> {
    let replacement = target.with_extension(format!("{id}.update.exe"));
    let backup = target.with_extension(format!("{id}.previous.exe"));
    fs::copy(staged, &replacement).map_err(|e| e.to_string())?;
    let mut result = Err("Cannot replace application.".into());
    for _ in 0..20 {
        result = replace(target, &replacement, Some(&backup));
        if result.is_ok() || !replacement.exists() || !target.exists() {
            break;
        }
        std::thread::sleep(Duration::from_millis(250));
    }
    if let Err(error) = result {
        // ReplaceFile can leave the original under its backup name on failure.
        if !target.exists() && backup.exists() {
            fs::rename(&backup, target).map_err(|e| format!("{error}; restore failed: {e}"))?;
        }
        let _ = fs::remove_file(replacement);
        return Err(error);
    }
    if let Err(error) = start() {
        replace(target, &backup, None)
            .map_err(|restore| format!("{error}; rollback failed: {restore}"))?;
        return Err(error);
    }
    Ok(())
}

pub fn apply_job(path: &Path) -> Result<(), String> {
    let job: Job = json(&read_limited(path, METADATA_LIMIT)?)?;
    let directory = path.parent().ok_or("Missing update directory")?;
    let root = directory.parent().ok_or("Missing update root")?;
    if root.join(&job.pending.directory) != directory || !job.pending.target.is_absolute() {
        return Err("Invalid update job paths.".into());
    }
    let result = (|| {
        let staged = job.pending.verify(root)?;
        unsafe {
            let parent = OpenProcess(SYNCHRONIZATION_SYNCHRONIZE, 0, job.parent);
            if !parent.is_null() {
                let wait = WaitForSingleObject(parent, 60_000);
                CloseHandle(parent);
                if wait != WAIT_OBJECT_0 {
                    return Err("DesktopPlus did not exit; update cancelled.".into());
                }
            } else if GetLastError() != ERROR_INVALID_PARAMETER {
                return Err(crate::shell::error_text());
            }
        }
        let lock = AppLock::acquire()?;
        if hash(&read_limited(&job.pending.target, PACKAGE_LIMIT)?) != job.original_sha256 {
            return Err("Application changed since the update was prepared.".into());
        }
        install(&job.pending.target, &staged, &job.pending.directory, || {
            drop(lock);
            restart(&job)
        })
    })();
    let report = match &result {
        Ok(()) => format!("Installed {}", job.pending.version),
        Err(error) => format!("Update failed: {error}"),
    };
    let _ = config::atomic_write(&root.join("last-result.txt"), report.as_bytes());
    if result.is_err() && job.pending.target.exists() {
        // The original program remains usable after a failed replace/relaunch.
        let _ = restart(&job);
    }
    result
}
