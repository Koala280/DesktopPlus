#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backup;
mod config;
mod index;
mod locale;
mod metadata;
mod model;
mod shell;
mod sort;
mod startup;
mod text_encoding;
mod ui;
mod update;
mod worker;

fn main() {
    if let Err(error) = start() {
        eprintln!("DesktopPlus: {error}");
        if std::env::args_os()
            .nth(1)
            .is_some_and(|arg| arg == "--configure-startup")
        {
            std::process::exit(1);
        }
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::MessageBoxW(
                std::ptr::null_mut(),
                shell::wide(&error).as_ptr(),
                shell::wide("DesktopPlus").as_ptr(),
                windows_sys::Win32::UI::WindowsAndMessaging::MB_OK
                    | windows_sys::Win32::UI::WindowsAndMessaging::MB_ICONERROR,
            );
        }
        std::process::exit(1);
    }
}
fn start() -> Result<(), String> {
    let mut args = std::env::args_os().skip(1);
    if std::env::args_os()
        .nth(1)
        .is_some_and(|arg| arg == "--apply-update")
    {
        args.next();
        let job = args.next().ok_or("--apply-update requires a job file")?;
        if args.next().is_some() {
            return Err("--apply-update does not accept additional arguments".into());
        }
        return update::apply_job(std::path::Path::new(&job));
    }
    if std::env::args_os()
        .nth(1)
        .is_some_and(|arg| arg == "--configure-startup")
    {
        args.next();
        let enabled = match args.next().as_deref().and_then(|arg| arg.to_str()) {
            Some("enable") => true,
            Some("disable") => false,
            _ => return Err("--configure-startup requires enable or disable".into()),
        };
        if args.next().is_some() {
            return Err("--configure-startup does not accept additional arguments".into());
        }
        return startup::configure(enabled);
    }
    let mut path = None;
    let mut smoke = None;
    let mut startup = false;
    let mut uninstall = false;
    let mut language = None;
    let mut folders = Vec::new();
    while let Some(argument) = args.next() {
        match argument.to_str() {
            Some("--settings") => {
                path = Some(std::path::PathBuf::from(
                    args.next().ok_or("--settings requires a file path")?,
                ))
            }
            Some("--smoke-test") => {
                smoke = Some(std::path::PathBuf::from(
                    args.next().ok_or("--smoke-test requires a report path")?,
                ))
            }
            Some("--open") => folders.push(std::path::PathBuf::from(
                args.next().ok_or("--open requires a folder")?,
            )),
            Some("--startup") => startup = true,
            Some("--restore-before-uninstall") => uninstall = true,
            Some(value) if value.starts_with("--language=") => {
                language = Some(value[11..].to_owned())
            }
            Some("--version") => {
                println!("DesktopPlus {} (Rust / Win32)", env!("DESKTOPPLUS_VERSION"));
                return Ok(());
            }
            Some("--help") => {
                println!(
                    "DesktopPlus [--settings FILE] [--open FOLDER] [--startup] [--smoke-test REPORT]\nDesktopPlus --configure-startup enable|disable"
                );
                return Ok(());
            }
            _ => return Err(format!("Unknown argument: {}", argument.to_string_lossy())),
        }
    }
    if smoke.is_some() && path.is_none() {
        return Err("Smoke tests require an isolated --settings path.".into());
    }
    let import = path.is_none();
    let store = config::Store::new(path);
    let mut settings = store.load(import)?;
    if uninstall {
        std::process::exit(ui::restore_before_uninstall(
            &store,
            &settings,
            language.as_deref(),
        )?);
    }
    for folder in folders {
        if !folder.is_dir() {
            return Err(format!("Folder does not exist: {}", folder.display()));
        }
        settings.windows.push(model::Panel::folder(&folder));
    }
    ui::run(store, settings, startup, smoke)
}
