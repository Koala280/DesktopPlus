use super::*;
use std::{cell::RefCell, collections::HashMap, io::Write};

struct Fixture(PathBuf);
impl Fixture {
    fn new() -> Self {
        let path =
            std::env::temp_dir().join(format!("desktopplus-update-{}", crate::model::new_id()));
        fs::create_dir_all(&path).unwrap();
        Self(path)
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn executable() -> Vec<u8> {
    let mut bytes = vec![0; 512];
    bytes[..2].copy_from_slice(b"MZ");
    bytes[0x3c..0x40].copy_from_slice(&64u32.to_le_bytes());
    bytes[64..68].copy_from_slice(b"PE\0\0");
    let machine: u16 = match runtime() {
        "win-x64" => 0x8664,
        "win-arm64" => 0xaa64,
        _ => 0x14c,
    };
    bytes[68..70].copy_from_slice(&machine.to_le_bytes());
    let magic: u16 = if runtime() == "win-x86" { 0x10b } else { 0x20b };
    bytes[88..90].copy_from_slice(&magic.to_le_bytes());
    bytes
}
fn package(entries: &[(&str, &[u8])]) -> Vec<u8> {
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    for (name, bytes) in entries {
        zip.start_file(*name, zip::write::SimpleFileOptions::default())
            .unwrap();
        zip.write_all(bytes).unwrap();
    }
    zip.finish().unwrap().into_inner()
}
fn release(package: Vec<u8>) -> HashMap<String, Vec<u8>> {
    let name = format!("DesktopPlus-2.1.0-{}-portable.zip", runtime());
    let files = [
        ("release-manifest.json".into(), format!("\u{feff}{{\"version\":\"2.1.0\",\"runtimeIdentifier\":\"{}\",\"implementation\":\"Rust / Win32\",\"configuration\":\"Release\"}}", runtime()).into_bytes()),
        ("SHA256SUMS.txt".into(), format!("\u{feff}{} *{name}\r\n", hash(&package)).into_bytes()),
        (name, package),
    ];
    let mut responses = HashMap::new();
    let assets: Vec<_> = files
        .into_iter()
        .map(|(name, bytes)| {
            let url = format!("{ASSET_ROOT}v2.1.0/{name}");
            responses.insert(url.clone(), bytes);
            serde_json::json!({"name":name,"browser_download_url":url})
        })
        .collect();
    responses.insert(
        API.into(),
        serde_json::to_vec(&serde_json::json!({
            "tag_name":"v2.1.0", "draft":false, "prerelease":false, "assets":assets,
        }))
        .unwrap(),
    );
    responses
}

#[test]
fn versions_never_downgrade_or_install_prereleases_or_build_metadata_only_changes() {
    assert!(newer("2.9.0", "v2.10.0").unwrap());
    assert!(newer("2.1.0-beta.1", "2.1.0").unwrap());
    for (current, remote) in [
        ("2.0.0", "1.6.13"),
        ("2.0.0", "2.0.0"),
        ("2.0.0", "2.1.0-beta.1"),
        ("2.0.0+local", "2.0.0+release"),
    ] {
        assert!(!newer(current, remote).unwrap());
    }
    assert!(newer("invalid", "2.1.0").is_err());
    assert!(newer("2.0.0", "../../2.1.0").is_err());
}
#[test]
fn imports_and_persists_the_real_update_preference() {
    assert!(!Settings::default().auto_check_updates);
    for key in ["AutoCheckUpdates", "autoCheckUpdates", "autocheckupdates"] {
        let settings = config::parse(&format!("{{\"{key}\":true}}")).unwrap();
        assert!(settings.auto_check_updates);
        assert!(!settings.extra.contains_key(key));
        let saved = serde_json::to_value(settings).unwrap();
        assert_eq!(saved["AutoCheckUpdates"], true);
    }
    assert!(
        !config::parse(r#"{"AutoCheckUpdates":null}"#)
            .unwrap()
            .auto_check_updates
    );
}
#[test]
fn verifies_stages_caches_and_rechecks_the_downloaded_binary() {
    let fixture = Fixture::new();
    let target = fixture.0.join("installed.exe");
    let bytes = executable();
    let responses = release(package(&[
        ("DesktopPlus.exe", &bytes),
        ("../escape.txt", b"never extracted"),
    ]));
    let requests = RefCell::new(Vec::new());
    let get = |url: &str, _: usize| {
        requests.borrow_mut().push(url.to_owned());
        responses
            .get(url)
            .cloned()
            .ok_or_else(|| "Unexpected URL".into())
    };
    let pending = check_and_stage(&fixture.0, &target, "2.0.0", &get)
        .unwrap()
        .unwrap();
    let staged = pending.verify(&fixture.0).unwrap();
    assert_eq!(fs::read(&staged).unwrap(), bytes);
    assert!(!fixture.0.join("escape.txt").exists());
    assert!(!fixture.0.join("pending.json").exists()); // UI decides whether to opt in.
    remember(&fixture.0, &pending).unwrap();
    requests.borrow_mut().clear();
    let cached = check_and_stage(&fixture.0, &target, "2.0.0", &get)
        .unwrap()
        .unwrap();
    assert_eq!(cached.directory, pending.directory);
    assert_eq!(*requests.borrow(), [API]);
    fs::write(&staged, b"tampered").unwrap();
    assert!(pending.verify(&fixture.0).is_err());
    let mut traversal = pending.clone();
    traversal.directory = "../outside".into();
    assert!(traversal.verify(&fixture.0).is_err());
}
#[test]
fn corrupt_or_incompatible_releases_never_stage_an_executable() {
    for mode in [
        "checksum",
        "manifest",
        "architecture",
        "debug",
        "url",
        "missing",
        "network",
    ] {
        let fixture = Fixture::new();
        let mut responses = release(package(&[("DesktopPlus.exe", &executable())]));
        let sums = format!("{ASSET_ROOT}v2.1.0/SHA256SUMS.txt");
        let manifest = format!("{ASSET_ROOT}v2.1.0/release-manifest.json");
        match mode {
            "checksum" => {
                responses.insert(sums, vec![b'0'; 64]);
            }
            "manifest" => {
                responses.insert(manifest, b"{}".to_vec());
            }
            "architecture" | "debug" => {
                let mut value: serde_json::Value = json(&responses[&manifest]).unwrap();
                value[if mode == "debug" {
                    "configuration"
                } else {
                    "runtimeIdentifier"
                }] = "unsupported".into();
                responses.insert(manifest, serde_json::to_vec(&value).unwrap());
            }
            "url" => {
                let text = String::from_utf8(responses[API].clone())
                    .unwrap()
                    .replace("https://github.com/", "https://evil.example/");
                responses.insert(API.into(), text.into_bytes());
            }
            "missing" => {
                responses.remove(&sums);
            }
            _ => {
                responses.clear();
            }
        }
        let result = check_and_stage(
            &fixture.0,
            &fixture.0.join("installed.exe"),
            "2.0.0",
            &|url, _| {
                responses
                    .get(url)
                    .cloned()
                    .ok_or_else(|| "Network error".into())
            },
        );
        assert!(result.is_err(), "{mode}");
        assert_eq!(fs::read_dir(&fixture.0).unwrap().count(), 0, "{mode}");
    }
}
#[test]
fn old_draft_and_prerelease_metadata_do_not_download_assets() {
    for (tag, draft, prerelease) in [
        ("v1.6.13", false, false),
        ("v3.0.0", true, false),
        ("v3.0.0", false, true),
    ] {
        let fixture = Fixture::new();
        let response = serde_json::to_vec(
            &serde_json::json!({"tag_name":tag,"draft":draft,"prerelease":prerelease,"assets":[]}),
        )
        .unwrap();
        let result = check_and_stage(
            &fixture.0,
            &fixture.0.join("installed.exe"),
            "2.0.0",
            &|url, _| {
                assert_eq!(url, API);
                Ok(response.clone())
            },
        )
        .unwrap();
        assert!(result.is_none());
        assert_eq!(fs::read_dir(&fixture.0).unwrap().count(), 0);
    }
}
#[test]
fn checksum_requires_an_exact_unique_filename() {
    let digest = "a".repeat(64);
    assert_eq!(
        checksum(&format!("\u{feff}{digest} *app.zip\r\n"), "app.zip").unwrap(),
        digest
    );
    assert!(checksum(&format!("{digest} *other-app.zip"), "app.zip").is_err());
    assert!(checksum(&format!("{digest} *app.zip\n{digest} *app.zip"), "app.zip").is_err());
    assert!(checksum("invalid *app.zip", "app.zip").is_err());
}
#[test]
fn rejects_managed_wrong_architecture_and_non_executable_packages() {
    let mut managed = executable();
    let directory = 64 + 24 + if runtime() == "win-x86" { 96 } else { 112 };
    managed[directory + 14 * 8] = 1;
    assert!(extract_executable(package(&[("DesktopPlus.exe", &managed)])).is_err());
    let mut wrong = executable();
    wrong[68..70].copy_from_slice(&0xffffu16.to_le_bytes());
    assert!(validate_pe(&wrong).is_err());
    for data in [vec![], b"MZ".to_vec(), b"not a PE".to_vec()] {
        assert!(validate_pe(&data).is_err());
    }
    assert!(extract_executable(package(&[("../DesktopPlus.exe", &executable())])).is_err());
}
#[test]
fn replacement_backs_up_and_rolls_back_when_restart_fails() {
    let fixture = Fixture::new();
    let target = fixture.0.join("DesktopPlus.exe");
    let staged = fixture.0.join("staged.exe");
    fs::write(&target, b"previous executable").unwrap();
    fs::write(&staged, b"new executable").unwrap();
    install(&target, &staged, "success", || Ok(())).unwrap();
    assert_eq!(fs::read(&target).unwrap(), b"new executable");
    assert_eq!(
        fs::read(target.with_extension("success.previous.exe")).unwrap(),
        b"previous executable"
    );
    fs::write(&staged, b"broken launch").unwrap();
    assert!(install(&target, &staged, "failed", || Err("Launch failed".into())).is_err());
    assert_eq!(fs::read(&target).unwrap(), b"new executable");
}
#[test]
fn missing_download_cannot_damage_existing_installation() {
    let fixture = Fixture::new();
    let target = fixture.0.join("DesktopPlus.exe");
    fs::write(&target, b"original").unwrap();
    assert!(
        install(
            &target,
            &fixture.0.join("missing.exe"),
            "missing",
            || panic!("must not launch")
        )
        .is_err()
    );
    assert_eq!(fs::read(&target).unwrap(), b"original");
}

#[test]
fn restart_preserves_default_and_isolated_storage_locations() {
    assert_eq!(restart_settings(&Store::new(None)).unwrap(), None);
    let fixture = Fixture::new();
    let isolated = Store::new(Some(fixture.0.join("settings.json")));
    let restarted = Store::new(restart_settings(&isolated).unwrap());
    assert_eq!(isolated.path, restarted.path);
    assert_eq!(isolated.data, restarted.data);
    let default_override = Store::new(Some(Store::new(None).path));
    assert!(restart_settings(&default_override).unwrap().is_some());
}
#[test]
#[ignore = "Read-only integration check against the public GitHub release API"]
fn live_release_metadata_uses_windows_https() {
    let release: Release = json(&http::get(API, METADATA_LIMIT).unwrap()).unwrap();
    assert!(!release.draft && !release.prerelease);
    assert!(version(&release.tag_name).is_ok());
    assert!(!release.assets.is_empty());
    let sums = http::get(asset(&release, "SHA256SUMS.txt").unwrap(), METADATA_LIMIT).unwrap();
    assert!(!sums.is_empty());
}
