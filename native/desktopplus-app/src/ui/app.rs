use super::*;
mod behavior;
mod preferences;
mod updates;
use crate::{
    backup,
    config::Store,
    model::{Layout, Panel, Settings, Tab},
    worker::{Job, Output, Worker},
};
use dialog::Field;
use std::{
    collections::HashSet,
    path::Path,
    sync::atomic::{AtomicU64, Ordering},
    time::Instant,
};
use windows_sys::Win32::System::{Ole::*, Threading::*};

#[cfg(test)]
mod behavior_tests;
#[cfg(test)]
mod geometry_tests;
#[cfg(test)]
mod hotkey_tests;

struct PanelWindow {
    id: String,
    generation: u64,
    gate: Arc<AtomicU64>,
    query: String,
    sort: usize,
    descending: bool,
    hover_restore: Option<RECT>,
    hover_held_open: bool,
    hover_suppressed: bool,
    fit_after_load: Option<bool>,
    image_gate: Arc<AtomicU64>,
    pending_zoom: Option<f64>,
}
struct App {
    main: HWND,
    store: Store,
    settings: Settings,
    preference_languages: Vec<(String, String)>,
    updater: crate::update::Updater,
    windows: HashMap<usize, PanelWindow>,
    worker: Worker,
    hidden: bool,
    foreground: bool,
    foreground_restore: Option<usize>,
    exiting: bool,
    tray: NOTIFYICONDATAW,
    smoke: Option<PathBuf>,
    started: Instant,
    sorting: bool,
    smoke_errors: Vec<String>,
}

fn fit_panel_bounds(mut bounds: RECT, work: RECT) -> RECT {
    let width = (bounds.right - bounds.left)
        .max(220)
        .min((work.right - work.left).max(1));
    let height = (bounds.bottom - bounds.top)
        .max(PANEL_HEADER_HEIGHT)
        .min((work.bottom - work.top).max(1));
    bounds.left = bounds
        .left
        .clamp(work.left, (work.right - width).max(work.left));
    bounds.top = bounds
        .top
        .clamp(work.top, (work.bottom - height).max(work.top));
    bounds.right = bounds.left + width;
    bounds.bottom = bounds.top + height;
    bounds
}

fn resized_panel_bounds(mut bounds: RECT, height: i32, work: RECT) -> RECT {
    if (bounds.bottom - work.bottom).abs() <= 2 {
        bounds.top = work.bottom - height;
    }
    bounds.bottom = bounds.top + height;
    fit_panel_bounds(bounds, work)
}

pub fn restore_before_uninstall(
    store: &Store,
    settings: &Settings,
    language: Option<&str>,
) -> Result<i32, String> {
    unsafe {
        let running = OpenMutexW(
            SYNCHRONIZATION_SYNCHRONIZE,
            0,
            wide("Local\\DesktopPlus.Native").as_ptr(),
        );
        if !running.is_null() {
            CloseHandle(running);
            return Ok(3);
        }
        if OleInitialize(null()) < 0 {
            return Err("Cannot initialize Windows recovery.".into());
        }
    }
    let result = (|| {
        let language = match language {
            Some("german") => "de",
            Some("english") => "en",
            Some(other) => other,
            None => &settings.language,
        };
        crate::locale::configure(language, &store.data.join("Languages"));
        let directory = store.data.join("Backups");
        let paths = backup::list(&directory)?;
        if paths.is_empty() {
            return Ok(0);
        }
        let choices = paths
            .iter()
            .map(|path| {
                backup::read(path)
                    .map(|backup| format!("{} — {}", backup.created, backup.reason))
                    .unwrap_or_else(|_| {
                        path.file_name()
                            .unwrap_or_default()
                            .to_string_lossy()
                            .into_owned()
                    })
            })
            .collect();
        let Some(values) = dialog::edit(
            null_mut(),
            "Restore before uninstall",
            vec![Field::Choice("Backup".into(), choices, 0)],
        ) else {
            return Ok(2);
        };
        let path = &paths[values[0].parse::<usize>().unwrap_or(0).min(paths.len() - 1)];
        let mut restore = backup::read(path)?;
        backup::create(
            &directory,
            settings,
            "Before uninstall restore",
            Vec::new(),
            Some(path),
        )?;
        let skipped = backup::restore(&restore, &store.data)?;
        restore.settings.normalize();
        store.save(&restore.settings)?;
        if !skipped.is_empty() {
            let report = directory.join("last-restore.txt");
            crate::config::atomic_write(&report, skipped.join("\r\n").as_bytes())
                .map_err(|e| e.to_string())?;
            return Err(format!(
                "Some files could not be restored. Review {} before uninstalling.",
                report.display()
            ));
        }
        Ok(0)
    })();
    unsafe {
        OleUninitialize();
    }
    result
}

pub fn run(
    store: Store,
    mut settings: Settings,
    startup: bool,
    smoke: Option<PathBuf>,
) -> Result<(), String> {
    unsafe {
        if OleInitialize(null()) < 0 {
            return Err("Cannot initialize Windows drag and drop.".into());
        }
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let instance = GetModuleHandleW(null());
        let mutex_name = if smoke.is_some() {
            format!("Local\\DesktopPlus.Native.Smoke.{}", std::process::id())
        } else {
            "Local\\DesktopPlus.Native".into()
        };
        let mutex = CreateMutexW(null(), 0, wide(&mutex_name).as_ptr());
        if mutex.is_null() {
            return Err(shell::error_text());
        }
        if GetLastError() == ERROR_ALREADY_EXISTS {
            CloseHandle(mutex);
            if !startup {
                let existing = FindWindowW(wide(CLASS).as_ptr(), wide("DesktopPlus").as_ptr());
                if !existing.is_null() {
                    ShowWindow(existing, SW_SHOW);
                    SetForegroundWindow(existing);
                }
            }
            OleUninitialize();
            return Ok(());
        }
        settings.normalize();
        crate::locale::configure(&settings.language, &store.data.join("Languages"));
        PALETTE.with(|palette| *palette.borrow_mut() = Palette::new(&settings.appearance));
        let init = INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_LISTVIEW_CLASSES | ICC_TAB_CLASSES | ICC_STANDARD_CLASSES,
        };
        InitCommonControlsEx(&init);
        let icon = LoadIconW(instance, 1usize as _);
        let class_name = wide(CLASS);
        let class = WNDCLASSW {
            style: CS_DBLCLKS,
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            hIcon: icon,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class_name.as_ptr(),
            ..std::mem::zeroed()
        };
        if RegisterClassW(&class) == 0 {
            CloseHandle(mutex);
            return Err(shell::error_text());
        }
        if smoke.is_none() && settings.auto_check_updates && !cfg!(debug_assertions) {
            let update_root = store.data.join("Updates");
            let result = crate::update::pending(&update_root).and_then(|pending| {
                if let Some(pending) = pending {
                    crate::update::launch(&store, &settings, &pending, startup)?;
                    Ok(true)
                } else {
                    Ok(false)
                }
            });
            match result {
                Ok(true) => {
                    CloseHandle(mutex);
                    OleUninitialize();
                    return Ok(());
                }
                Err(error) => {
                    crate::update::clear_pending(&update_root);
                    let _ = crate::config::atomic_write(
                        &update_root.join("last-result.txt"),
                        error.as_bytes(),
                    );
                }
                Ok(false) => {}
            }
        }
        let main = CreateWindowExW(
            0,
            class_name.as_ptr(),
            wide("DesktopPlus").as_ptr(),
            WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            1020,
            720,
            null_mut(),
            null_mut(),
            instance,
            1usize as _,
        );
        if main.is_null() {
            CloseHandle(mutex);
            return Err(shell::error_text());
        }
        SetWindowLongPtrW(main, GWLP_USERDATA, 1);
        TASKBAR_CREATED
            .with(|message| message.set(RegisterWindowMessageW(wide("TaskbarCreated").as_ptr())));
        theme_frame(main);
        dashboard::create(main);
        let worker = Worker::start(main as usize, store.data.join("SearchIndex"));
        let mut tray: NOTIFYICONDATAW = std::mem::zeroed();
        tray.cbSize = std::mem::size_of_val(&tray) as u32;
        tray.hWnd = main;
        tray.uID = 1;
        tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        tray.uCallbackMessage = TRAY_MESSAGE;
        tray.hIcon = icon;
        copy_text(
            tray.szTip.as_mut_ptr(),
            tray.szTip.len() as i32,
            &"DesktopPlus".encode_utf16().collect::<Vec<_>>(),
        );
        if smoke.is_none() {
            if Shell_NotifyIconW(NIM_ADD, &tray) == 0 {
                // A logon task can beat Explorer to the desktop. Preserve the user's
                // close preference and attach the tray icon when Explorer is ready.
                SetTimer(main, TRAY_RETRY_TIMER, 1000, None);
            }
            tray.Anonymous.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, &tray);
        }
        let mut app = App {
            main,
            store,
            settings,
            preference_languages: Vec::new(),
            updater: crate::update::Updater::default(),
            windows: HashMap::new(),
            worker,
            hidden: false,
            foreground: false,
            foreground_restore: None,
            exiting: false,
            tray,
            smoke,
            started: Instant::now(),
            sorting: false,
            smoke_errors: Vec::new(),
        };
        for panel in app.settings.windows.clone() {
            app.open_panel(&panel)?;
        }
        app.update_main();
        app.configure();
        app.register_hotkeys();
        app.configure_updates();
        if !startup || app.settings.close_behavior == "Exit" {
            ShowWindow(main, SW_SHOW);
            UpdateWindow(main);
        }
        if app.smoke.is_some() {
            SetTimer(main, SMOKE_TIMER, 30000, None);
        }
        let mut msg = MSG::default();
        while !app.exiting {
            let result = GetMessageW(&mut msg, null_mut(), 0, 0);
            if result <= 0 {
                break;
            }
            if !(msg.hwnd == main || IsChild(main, msg.hwnd) != 0)
                || IsDialogMessageW(main, &msg) == 0
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            loop {
                let event = EVENTS.with(|events| events.borrow_mut().pop_front());
                let Some(event) = event else {
                    break;
                };
                if let Err(error) = app.event(event) {
                    app.status(&error);
                    if app.smoke.is_some() {
                        app.smoke_errors.push(error.clone());
                    }
                    if app.smoke.is_none() {
                        message(app.main, &error);
                    }
                }
                if app.exiting {
                    break;
                }
            }
            app.outputs();
            app.update_results();
        }
        app.capture_geometry();
        if app.smoke.is_none() {
            let _ = app.store.save(&app.settings);
            Shell_NotifyIconW(NIM_DELETE, &app.tray);
        }
        UnregisterHotKey(main, 1);
        UnregisterHotKey(main, 2);
        for window in app.windows.keys() {
            DestroyWindow(*window as HWND);
        }
        DestroyWindow(main);
        motion::shutdown();
        drop(app);
        CloseHandle(mutex);
        OleUninitialize();
        Ok(())
    }
}

impl App {
    fn status(&self, value: &str) {
        set_text(control(self.main, STATUS), value);
    }
    fn panel_index(&self, hwnd: usize) -> Option<usize> {
        self.windows.get(&hwnd).and_then(|window| {
            self.settings
                .windows
                .iter()
                .position(|p| p.panel_id == window.id)
        })
    }
    fn open_panel(&mut self, panel: &Panel) -> Result<(), String> {
        let name = wide(CLASS);
        let mut bounds = RECT {
            left: panel.left as i32,
            top: panel.top as i32,
            right: (panel.left + panel.width) as i32,
            bottom: panel.top as i32
                + if panel.is_collapsed {
                    (PANEL_HEADER_HEIGHT as u32 * unsafe { GetDpiForSystem() } / 96) as i32
                } else {
                    panel.height as i32
                },
        };
        let mut monitor: MONITORINFO = unsafe { std::mem::zeroed() };
        monitor.cbSize = std::mem::size_of_val(&monitor) as u32;
        unsafe {
            if GetMonitorInfoW(
                MonitorFromRect(&bounds, MONITOR_DEFAULTTONEAREST),
                &mut monitor,
            ) != 0
            {
                bounds = fit_panel_bounds(bounds, monitor.rcWork);
            }
        }
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_ACCEPTFILES,
                name.as_ptr(),
                wide(&panel.panel_title).as_ptr(),
                WS_POPUP | WS_SYSMENU | WS_THICKFRAME | WS_CLIPCHILDREN,
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                if panel.is_collapsed {
                    (PANEL_HEADER_HEIGHT as u32 * GetDpiForSystem() / 96) as i32
                } else {
                    bounds.bottom - bounds.top
                },
                null_mut(),
                null_mut(),
                GetModuleHandleW(null()),
                2usize as _,
            )
        };
        if hwnd.is_null() {
            return Err(shell::error_text());
        }
        theme_frame(hwnd);
        let tab_control = child(
            hwnd,
            "SysTabControl32",
            "",
            TABS,
            WS_TABSTOP | TCS_SINGLELINE | TCS_FIXEDWIDTH | TCS_FOCUSNEVER,
        );
        chrome::setup_tabs(tab_control);
        for (id, caption) in [
            (UP, "Parent folder"),
            (HOME, "Home folder"),
            (VIEW, "View"),
            (PANEL_SETTINGS, "Panel settings"),
            (COLLAPSE, "Collapse / expand"),
            (HIDE_PANEL, "Hide panel"),
            (SEARCH_TOGGLE, "Search files and folders…"),
            (MOVE_PANEL, "Move panel"),
            (EMPTY_PANEL, "Empty folder…"),
        ] {
            button(hwnd, caption, id);
        }
        chrome::tooltips(
            hwnd,
            &[
                UP,
                HOME,
                VIEW,
                PANEL_SETTINGS,
                COLLAPSE,
                HIDE_PANEL,
                SEARCH_TOGGLE,
                MOVE_PANEL,
                EMPTY_PANEL,
            ],
        );
        let search = child(hwnd, "EDIT", "", SEARCH, WS_TABSTOP | ES_AUTOHSCROLL as u32);
        chrome::setup_search(search);
        child(hwnd, "STATIC", "", SOURCE, SS_PATHELLIPSIS | SS_CENTERIMAGE);
        create_list(
            hwnd,
            &crate::metadata::COLUMNS
                .iter()
                .map(|(_, caption, width)| (*caption, *width))
                .collect::<Vec<_>>(),
        );
        child(hwnd, "STATIC", "Loading…", STATUS, 0);
        VIEWS.with(|views| {
            views.borrow_mut().insert(
                hwnd as usize,
                ViewData {
                    source: None,
                    rows: Arc::new(Vec::new()),
                    main: Vec::new(),
                    is_main: false,
                    workspace_page: OVERVIEW,
                    photos: false,
                    details: false,
                    single_click: false,
                    columns: [false; 10],
                    collapsed: panel.is_collapsed,
                    tab_count: panel.tabs.as_ref().map_or(1, Vec::len),
                    kind: panel.active().panel_type.clone(),
                    loaded: false,
                    searching: false,
                    search_open: false,
                    options: panel_options::Options::resolved(panel, &self.settings.appearance),
                    hover_tracking: false,
                    zoom: panel.zoom,
                },
            );
        });
        grip::create(hwnd);
        self.windows.insert(
            hwnd as usize,
            PanelWindow {
                id: panel.panel_id.clone(),
                generation: 0,
                gate: Arc::new(AtomicU64::new(0)),
                query: String::new(),
                sort: 0,
                descending: false,
                hover_restore: None,
                hover_held_open: false,
                hover_suppressed: false,
                fit_after_load: None,
                image_gate: Arc::new(AtomicU64::new(0)),
                pending_zoom: None,
            },
        );
        self.apply_panel_theme(hwnd as usize);
        self.tabs(hwnd as usize);
        layout(hwnd);
        if panel.is_collapsed {
            unsafe {
                SetWindowPos(
                    hwnd,
                    null_mut(),
                    0,
                    0,
                    bounds.right - bounds.left,
                    chrome::scale(hwnd, PANEL_HEADER_HEIGHT),
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
                );
            }
        }
        if !panel.is_hidden {
            unsafe {
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(
                    hwnd,
                    HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
                );
            }
        }
        self.refresh(hwnd as usize);
        Ok(())
    }
    fn tabs(&self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        let panel = &self.settings.windows[index];
        let update = content::Update::new(hwnd as _);
        if content::prepare(hwnd as _, panel.active()) {
            self.windows[&hwnd]
                .image_gate
                .fetch_add(1, Ordering::Relaxed);
        }
        set_text(
            control(hwnd as _, EMPTY_PANEL as i32),
            &crate::locale::text(empty_panel::caption(&panel.active().panel_type)),
        );
        VIEWS.with(|views| {
            if let Some(view) = views.borrow_mut().get_mut(&hwnd) {
                view.zoom = panel.zoom;
                view.photos = panel.active().view_mode == "photos";
                view.details = !matches!(panel.active().view_mode.as_str(), "photos" | "icons");
                view.single_click = panel.active().open_items_on_single_click;
                view.collapsed = panel.is_collapsed
                    && self.windows[&hwnd].hover_restore.is_none()
                    && !motion::resizing(hwnd as _);
                view.tab_count = panel.tabs.as_ref().unwrap().len();
                view.kind = panel.active().panel_type.clone();
                view.options = panel_options::Options::resolved(panel, &self.settings.appearance);
            }
        });
        unsafe {
            let tabs = control(hwnd as _, TABS);
            let count = SendMessageW(tabs, TCM_GETITEMCOUNT, 0, 0).max(0) as usize;
            for (index, tab) in panel.tabs.as_ref().unwrap().iter().enumerate() {
                let mut name = wide(&tab.tab_name);
                if index < count {
                    let mut old = vec![0u16; name.len() + 1];
                    let mut current = TCITEMW {
                        mask: TCIF_TEXT,
                        pszText: old.as_mut_ptr(),
                        cchTextMax: old.len() as i32,
                        ..std::mem::zeroed()
                    };
                    SendMessageW(tabs, TCM_GETITEMW, index, &mut current as *mut _ as isize);
                    if old[..old.iter().position(|c| *c == 0).unwrap_or(old.len())]
                        == name[..name.len() - 1]
                    {
                        continue;
                    }
                }
                let mut item: TCITEMW = std::mem::zeroed();
                item.mask = TCIF_TEXT;
                item.pszText = name.as_mut_ptr();
                SendMessageW(
                    tabs,
                    if index < count {
                        TCM_SETITEMW
                    } else {
                        TCM_INSERTITEMW
                    },
                    index,
                    &item as *const _ as isize,
                );
            }
            for index in (panel.tabs.as_ref().unwrap().len()..count).rev() {
                SendMessageW(tabs, TCM_DELETEITEM, index, 0);
            }
            SendMessageW(tabs, TCM_SETCURSEL, panel.active_tab_index, 0);
            let view = match panel.active().view_mode.as_str() {
                "icons" => LV_VIEW_ICON,
                "photos" => LV_VIEW_ICON,
                _ => LV_VIEW_DETAILS,
            };
            SendMessageW(control(hwnd as _, LIST), LVM_SETVIEW, view as usize, 0);
        }
        images::configure(hwnd as _);
        self.apply_columns(hwnd);
        set_text(
            control(hwnd as _, SOURCE),
            &if panel.active().pinned() {
                crate::locale::text("Pinned files")
            } else if panel.active().panel_type.eq_ignore_ascii_case("RecycleBin") {
                crate::locale::text("Recycle Bin")
            } else {
                Path::new(&panel.active().folder_path)
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .into_owned()
            },
        );
        set_text(hwnd as _, &panel.panel_title);
        layout(hwnd as _);
        drop(update);
    }
    fn apply_columns(&self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        let extra = &self.settings.windows[index].active().extra;
        let list = control(hwnd as _, LIST);
        let _update = content::Update::new(hwnd as _);
        let mut order = Vec::new();
        let columns = std::array::from_fn(|column| {
            column == 0
                || extra
                    .get(&format!(
                        "ShowMetadata{}",
                        crate::metadata::COLUMNS[column].1
                    ))
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(column < 4)
        });
        // Publish visibility before changing native widths: header callbacks
        // must allow newly enabled columns and reject disabled ones immediately.
        VIEWS.with(|views| {
            if let Some(view) = views.borrow_mut().get_mut(&hwnd) {
                view.columns = columns;
            }
        });
        if let Some(keys) = extra
            .get("MetadataOrder")
            .and_then(serde_json::Value::as_array)
        {
            for key in keys {
                if let Some(index) = crate::metadata::COLUMNS
                    .iter()
                    .position(|(name, _, _)| key.as_str() == Some(name))
                {
                    if !order.contains(&(index as i32)) {
                        order.push(index as i32);
                    }
                }
            }
        }
        if !order.contains(&0) {
            order.insert(0, 0);
        }
        for (column, (key, _, default_width)) in crate::metadata::COLUMNS.iter().enumerate() {
            if !order.contains(&(column as i32)) {
                order.push(column as i32);
            }
            let visible = columns[column];
            let width = extra
                .get("MetadataWidths")
                .and_then(|widths| widths.get(key))
                .and_then(serde_json::Value::as_f64)
                .unwrap_or(*default_width as f64)
                .clamp(36.0, 2000.0) as i32;
            set_column_width(
                list,
                column,
                chrome::content_scale(hwnd as _, width),
                visible,
            );
        }
        unsafe {
            SendMessageW(
                list,
                LVM_SETCOLUMNORDERARRAY,
                order.len(),
                order.as_ptr() as isize,
            );
        }
    }
    fn capture_columns(&mut self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        let list = control(hwnd as _, LIST);
        let mut order = [0i32; 10];
        unsafe {
            SendMessageW(
                list,
                LVM_GETCOLUMNORDERARRAY,
                order.len(),
                order.as_mut_ptr() as isize,
            );
        }
        let extra = &mut self.settings.windows[index].active_mut().extra;
        let names: Vec<_> = order
            .iter()
            .filter_map(|index| {
                crate::metadata::COLUMNS
                    .get(*index as usize)
                    .map(|(key, _, _)| *key)
            })
            .collect();
        extra.insert("MetadataOrder".into(), serde_json::json!(names));
        let mut widths = extra
            .get("MetadataWidths")
            .and_then(serde_json::Value::as_object)
            .cloned()
            .unwrap_or_default();
        for (column, (key, _, _)) in crate::metadata::COLUMNS.iter().enumerate() {
            let width = unsafe { SendMessageW(list, LVM_GETCOLUMNWIDTH, column, 0) };
            if width > 0 {
                let scale = chrome::zoom(hwnd as _)
                    * unsafe { GetDpiForWindow(hwnd as _) }.max(96) as f64
                    / 96.0;
                widths.insert((*key).into(), serde_json::json!(width as f64 / scale));
            }
        }
        extra.insert("MetadataWidths".into(), serde_json::Value::Object(widths));
    }
    fn columns_dialog(&mut self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        self.capture_columns(hwnd);
        let extra = &self.settings.windows[index].active().extra;
        let fields = crate::metadata::COLUMNS
            .iter()
            .enumerate()
            .skip(1)
            .map(|(index, (_, caption, _))| {
                Field::Check(
                    (*caption).into(),
                    extra
                        .get(&format!("ShowMetadata{caption}"))
                        .and_then(serde_json::Value::as_bool)
                        .unwrap_or(index < 4),
                )
            })
            .collect();
        if let Some(values) = dialog::edit(hwnd as _, "Column settings", fields) {
            let extra = &mut self.settings.windows[index].active_mut().extra;
            for ((_, caption, _), value) in crate::metadata::COLUMNS.iter().skip(1).zip(values) {
                extra.insert(
                    format!("ShowMetadata{caption}"),
                    serde_json::json!(value == "1"),
                );
            }
            self.apply_columns(hwnd);
            self.changed();
        }
    }
    fn configure(&self) {
        let mut folders = Vec::new();
        let mut seen = HashSet::new();
        for panel in &self.settings.windows {
            for tab in std::iter::once(panel.active()).chain(panel.tabs.as_ref().unwrap().iter()) {
                if !tab.pinned()
                    && !tab.folder_path.is_empty()
                    && !tab.panel_type.eq_ignore_ascii_case("RecycleBin")
                    && seen.insert(tab.folder_path.to_lowercase())
                {
                    folders.push(PathBuf::from(&tab.folder_path));
                }
            }
        }
        if self.settings.desktop_auto_sort.auto_sort_enabled && self.smoke.is_none() {
            let desktop = shell::desktop();
            if seen.insert(desktop.to_string_lossy().to_lowercase()) {
                folders.push(desktop);
            }
        }
        let _ = self.worker.sender.send(Job::Configure(folders));
    }
    fn refresh(&mut self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        let mut tab = self.settings.windows[index].active().clone();
        content::prepare(hwnd as _, &tab);
        tab.extra.insert(
            "IconViewParentNavigationMode".into(),
            serde_json::Value::String(
                panel_options::Options::resolved(
                    &self.settings.windows[index],
                    &self.settings.appearance,
                )
                .parent,
            ),
        );
        let window = self.windows.get_mut(&hwnd).unwrap();
        window.generation += 1;
        window.image_gate.fetch_add(1, Ordering::Relaxed);
        window.gate.store(window.generation, Ordering::Relaxed);
        let _ = self.worker.sender.send(Job::List {
            window: hwnd,
            generation: window.generation,
            gate: window.gate.clone(),
            tab: Box::new(tab),
            query: window.query.clone(),
            sort: window.sort,
            descending: window.descending,
        });
        set_text(control(hwnd as _, STATUS), "Loading…");
    }
    fn update_main(&self) {
        let rows = self
            .settings
            .windows
            .iter()
            .map(|panel| {
                let source = if panel.active().pinned() {
                    crate::locale::text("{count} pinned files")
                        .replace("{count}", &panel.active().pinned_items.len().to_string())
                } else if panel.active().panel_type.eq_ignore_ascii_case("RecycleBin") {
                    crate::locale::text("Recycle Bin")
                } else {
                    panel.active().folder_path.clone()
                };
                vec![
                    panel.panel_title.encode_utf16().collect(),
                    source.encode_utf16().collect(),
                    panel
                        .tabs
                        .as_ref()
                        .unwrap()
                        .len()
                        .to_string()
                        .encode_utf16()
                        .collect(),
                    crate::locale::text(if panel.is_hidden { "Hidden" } else { "Visible" })
                        .encode_utf16()
                        .collect(),
                ]
            })
            .collect();
        VIEWS.with(|views| {
            if let Some(view) = views.borrow_mut().get_mut(&(self.main as usize)) {
                view.main = rows;
            }
        });
        unsafe {
            SendMessageW(
                control(self.main, LIST),
                LVM_SETITEMCOUNT,
                self.settings.windows.len(),
                LVSICF_NOSCROLL as isize,
            );
            InvalidateRect(control(self.main, LIST), null(), 1);
        }
        self.status(
            &crate::locale::text("{count} panels · Double-click to open")
                .replace("{count}", &self.settings.windows.len().to_string()),
        );
        unsafe {
            InvalidateRect(self.main, null(), 0);
        }
    }
    fn changed(&mut self) {
        for panel in &mut self.settings.windows {
            panel.synchronize();
        }
        self.update_main();
        unsafe {
            SetTimer(self.main, SAVE_TIMER, 500, None);
        }
    }
    fn capture_geometry(&mut self) {
        for (hwnd, window) in &self.windows {
            if window.hover_restore.is_some() {
                continue;
            }
            let Some(panel) = self
                .settings
                .windows
                .iter_mut()
                .find(|panel| panel.panel_id == window.id)
            else {
                continue;
            };
            let mut rect = RECT::default();
            if unsafe { GetWindowRect(*hwnd as _, &mut rect) } == 0 {
                continue;
            }
            rect = motion::destination(*hwnd as _).unwrap_or(rect);
            panel.left = rect.left as f64;
            panel.top = rect.top as f64;
            panel.width = (rect.right - rect.left) as f64;
            panel.height = (rect.bottom - rect.top) as f64;
            if !panel.is_collapsed {
                panel.expanded_height = panel.height;
            }
        }
    }
    fn backup(&mut self, reason: &str) -> Result<(), String> {
        self.capture_geometry();
        let directory = self.store.data.join("Backups");
        let settings = self.settings.clone();
        let reason = reason.to_owned();
        dialog::work(self.main, "Creating backup", move || {
            backup::create(&directory, &settings, &reason, Vec::new(), None)
        })?;
        Ok(())
    }
    fn rows(&self, hwnd: usize) -> Arc<Vec<Row>> {
        VIEWS.with(|views| {
            views
                .borrow()
                .get(&hwnd)
                .map(|view| view.rows.clone())
                .unwrap_or_default()
        })
    }
    fn selected_paths(&self, hwnd: usize) -> Vec<PathBuf> {
        let rows = self.rows(hwnd);
        selected(hwnd as _)
            .iter()
            .filter_map(|index| {
                rows.get(*index)
                    .filter(|row| !row.parent_navigation)
                    .map(|row| row.path.clone())
            })
            .collect()
    }
    fn panel_hidden(&self, window: &PanelWindow) -> bool {
        self.hidden
            || self
                .settings
                .windows
                .iter()
                .find(|panel| panel.panel_id == window.id)
                .is_some_and(|panel| panel.is_hidden)
    }
    fn toggle(&mut self) {
        self.set_foreground(false);
        self.hidden = !self.hidden;
        for (hwnd, window) in &self.windows {
            let hidden = self.panel_hidden(window);
            unsafe {
                ShowWindow(*hwnd as _, if hidden { SW_HIDE } else { SW_SHOWNOACTIVATE });
            }
        }
    }
    fn set_foreground(&mut self, foreground: bool) {
        if self.foreground == foreground {
            return;
        }
        self.foreground = foreground;
        unsafe {
            if foreground {
                if SetTimer(self.main, FOREGROUND_TIMER, 40, None) == 0 {
                    self.foreground = false;
                }
            } else {
                KillTimer(self.main, FOREGROUND_TIMER);
            }
        }
        let foreground = self.foreground;
        let restore_focus = if foreground {
            None
        } else {
            self.take_foreground_restore(unsafe { GetForegroundWindow() } as usize)
        };
        for (hwnd, window) in &self.windows {
            let hidden = self.panel_hidden(window);
            unsafe {
                SetWindowPos(
                    *hwnd as _,
                    // NOTOPMOST still leaves panels above ordinary windows.
                    // Return them to the desktop layer when the chord is released.
                    if foreground && !hidden {
                        HWND_TOPMOST
                    } else {
                        HWND_BOTTOM
                    },
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE
                        | SWP_NOSIZE
                        | SWP_NOACTIVATE
                        | if hidden {
                            SWP_HIDEWINDOW
                        } else {
                            SWP_SHOWWINDOW
                        },
                );
            }
        }
        if let Some(previous) = restore_focus {
            if unsafe { IsWindow(previous as HWND) } != 0 {
                unsafe {
                    SetForegroundWindow(previous as HWND);
                }
            }
        }
    }
    fn take_foreground_restore(&mut self, current: usize) -> Option<usize> {
        self.foreground_restore
            .take()
            .filter(|previous| *previous != current && self.windows.contains_key(&current))
    }
    fn foreground_panel(&self, current: usize) -> Option<usize> {
        self.windows
            .iter()
            .filter(|(hwnd, window)| {
                !self.panel_hidden(window) && unsafe { IsWindowVisible(**hwnd as HWND) } != 0
            })
            .map(|(hwnd, _)| *hwnd)
            // Prefer the active panel, but never activate a hidden panel.
            .min_by_key(|hwnd| *hwnd != current)
    }
    fn activate_foreground_panel(&mut self) {
        if self.foreground {
            return;
        }
        let current = unsafe { GetForegroundWindow() } as usize;
        if let Some(anchor) = self.foreground_panel(current) {
            self.foreground_restore = Some(current);
            unsafe {
                ShowWindow(anchor as HWND, SW_SHOWNOACTIVATE);
                // Activate a panel using the user's hotkey before raising the
                // group. SetWindowPos alone can silently ignore TOPMOST when
                // DesktopPlus is in the background, despite reporting success.
                SetForegroundWindow(anchor as HWND);
            }
        }
    }
    fn register_hotkeys(&mut self) {
        self.set_foreground(false);
        if self.smoke.is_some() {
            return;
        }
        unsafe {
            UnregisterHotKey(self.main, 1);
            UnregisterHotKey(self.main, 2);
        }
        for (id, value) in [
            (1, &self.settings.global_shortcuts.hide_panels_hotkey),
            (2, &self.settings.global_shortcuts.foreground_panels_hotkey),
        ] {
            if let Some((modifiers, key)) = hotkey(value) {
                if unsafe { RegisterHotKey(self.main, id, modifiers | MOD_NOREPEAT, key) } == 0 {
                    self.status(&format!("Shortcut already in use: {value}"));
                }
            } else {
                self.status(&format!("Invalid shortcut: {value}"));
            }
        }
    }
    fn outputs(&mut self) {
        while let Ok(output) = self.worker.receiver.try_recv() {
            match output {
                Output::Rows {
                    window,
                    generation,
                    rows,
                    preparing,
                    error,
                } => {
                    if !self
                        .windows
                        .get(&window)
                        .is_some_and(|window| window.generation == generation)
                    {
                        continue;
                    }
                    let selection: HashSet<_> = self.selected_paths(window).into_iter().collect();
                    let length = rows.len();
                    let selected: Vec<_> = rows
                        .iter()
                        .enumerate()
                        .filter(|(_, row)| selection.contains(&row.path))
                        .map(|(index, _)| index)
                        .collect();
                    let update = content::Update::new(window as _);
                    VIEWS.with(|views| {
                        if let Some(view) = views.borrow_mut().get_mut(&window) {
                            view.rows = rows;
                            view.loaded = true;
                            view.searching = !self.windows[&window].query.is_empty();
                        }
                    });
                    unsafe {
                        let list = control(window as _, LIST);
                        let mut item: LVITEMW = std::mem::zeroed();
                        item.stateMask = LVIS_SELECTED;
                        SendMessageW(
                            list,
                            LVM_SETITEMSTATE,
                            usize::MAX,
                            &item as *const _ as isize,
                        );
                        SendMessageW(
                            list,
                            LVM_SETITEMCOUNT,
                            length,
                            (LVSICF_NOSCROLL | LVSICF_NOINVALIDATEALL) as isize,
                        );
                        item.state = LVIS_SELECTED;
                        for index in selected {
                            SendMessageW(list, LVM_SETITEMSTATE, index, &item as *const _ as isize);
                        }
                    }
                    drop(update);
                    let status = error.unwrap_or_else(|| {
                        if preparing {
                            crate::locale::text("Updating search…")
                        } else {
                            String::new()
                        }
                    });
                    set_text(control(window as _, STATUS), &status);
                    if let Some(fit_width) = self
                        .windows
                        .get_mut(&window)
                        .and_then(|w| w.fit_after_load.take())
                    {
                        self.fit_contents(window, fit_width);
                    }
                }
                Output::Changed(paths) => {
                    if self.settings.desktop_auto_sort.auto_sort_enabled
                        && !self.sorting
                        && self.smoke.is_none()
                        && paths
                            .iter()
                            .any(|(path, _)| path.parent() == Some(shell::desktop().as_path()))
                    {
                        unsafe {
                            SetTimer(self.main, SORT_TIMER, 2000, None);
                        }
                    }
                    let refresh: Vec<_> = self
                        .windows
                        .keys()
                        .copied()
                        .filter(|hwnd| {
                            self.panel_index(*hwnd).is_some_and(|index| {
                                let tab = self.settings.windows[index].active();
                                if tab.pinned() {
                                    paths.iter().any(|(path, _)| {
                                        tab.pinned_items.iter().any(|item| path == Path::new(item))
                                    })
                                } else if self.windows[hwnd].query.is_empty() {
                                    paths.iter().any(|(path, _)| {
                                        path.parent() == Some(Path::new(&tab.folder_path))
                                    })
                                } else {
                                    paths.iter().any(|(path, structural)| {
                                        (*structural && path.starts_with(&tab.folder_path))
                                            || self.rows(*hwnd).iter().any(|row| row.path == *path)
                                    })
                                }
                            })
                        })
                        .collect();
                    for hwnd in refresh {
                        self.refresh(hwnd);
                    }
                }
                Output::Indexed(root) => {
                    let refresh: Vec<_> = self
                        .windows
                        .iter()
                        .filter(|(hwnd, window)| {
                            !window.query.is_empty()
                                && self.panel_index(**hwnd).is_some_and(|index| {
                                    Path::new(&self.settings.windows[index].active().folder_path)
                                        == root
                                })
                        })
                        .map(|(hwnd, _)| *hwnd)
                        .collect();
                    for hwnd in refresh {
                        self.refresh(hwnd);
                    }
                }
                Output::SearchStatus {
                    count,
                    preparing,
                    error,
                } => {
                    if let Some(error) = error {
                        self.status(&format!("Search index: {error}"));
                        continue;
                    }
                    self.status(&format!(
                        "{} · {}",
                        crate::locale::text(if preparing {
                            "Updating search…"
                        } else {
                            "Search ready"
                        }),
                        crate::locale::text("{count} items").replace("{count}", &count.to_string())
                    ));
                }
                Output::Done(result) => {
                    if let Err(error) = result {
                        self.status(&error);
                        message(self.main, &error);
                    }
                    for hwnd in self.windows.keys().copied().collect::<Vec<_>>() {
                        self.refresh(hwnd);
                    }
                }
                Output::Sorted(result) => {
                    self.sorting = false;
                    match result {
                        Ok(report) => {
                            for (name, path) in &report.targets {
                                if !path.exists()
                                    || self.settings.windows.iter().any(|panel| {
                                        panel.panel_title.eq_ignore_ascii_case(name)
                                            || panel
                                                .tabs
                                                .as_deref()
                                                .unwrap_or_default()
                                                .iter()
                                                .any(|tab| tab.tab_name.eq_ignore_ascii_case(name))
                                    })
                                {
                                    continue;
                                }
                                let mut panel = Panel::folder(path);
                                panel.panel_title = name.clone();
                                panel.left += (self.settings.windows.len() % 8) as f64 * 32.0;
                                panel.top += (self.settings.windows.len() % 8) as f64 * 32.0;
                                self.settings.windows.push(panel.clone());
                                if let Err(error) = self.open_panel(&panel) {
                                    self.status(&error);
                                }
                            }
                            for panel in &mut self.settings.windows {
                                for tab in panel.tabs.as_mut().unwrap() {
                                    for path in &mut tab.pinned_items {
                                        if let Some((_, destination)) =
                                            report.remaps.iter().find(|(from, _)| {
                                                from.to_string_lossy().eq_ignore_ascii_case(path)
                                            })
                                        {
                                            *path = destination.to_string_lossy().into_owned();
                                        }
                                    }
                                }
                            }
                            self.configure();
                            self.changed();
                            self.status(&format!(
                                "Sorted {} items; saved {} duplicate shortcuts; {} errors.",
                                report.moved,
                                report.duplicates,
                                report.errors.len()
                            ));
                            if !report.errors.is_empty() {
                                let _ = crate::config::atomic_write(
                                    &self.store.data.join("last-sort.txt"),
                                    report.errors.join("\r\n").as_bytes(),
                                );
                            }
                        }
                        Err(error) => {
                            self.status(&error);
                            message(self.main, &error);
                        }
                    }
                }
                Output::Image {
                    path,
                    modified,
                    spec,
                    bitmap,
                    cancelled,
                } => {
                    images::insert(spec, path, modified, bitmap, cancelled);
                    for hwnd in self.windows.keys() {
                        if images::spec(*hwnd as _) == spec {
                            unsafe {
                                InvalidateRect(control(*hwnd as _, LIST), null(), 0);
                            }
                        }
                    }
                }
                Output::Error(error) => {
                    self.status(&error);
                    if self.smoke.is_some() {
                        self.smoke_errors.push(error);
                    }
                }
                Output::Metadata {
                    path,
                    modified,
                    column,
                    value,
                } => {
                    METADATA
                        .with(|cache| cache.borrow_mut().insert((path, modified, column), value));
                    for hwnd in self.windows.keys() {
                        unsafe {
                            InvalidateRect(control(*hwnd as _, LIST), null(), 0);
                        }
                    }
                }
            }
        }
    }
    fn event(&mut self, event: Event) -> Result<(), String> {
        match event {
            Event::PreferenceSaved(command, answer) => self.save_preference(command, answer)?,
            Event::PreferenceCancelled(command) => {
                unsafe {
                    DestroyWindow(control(self.main, dashboard::form_id(command)));
                }
                self.open_preference(command)?;
                unsafe {
                    SetFocus(control(self.main, command as i32));
                }
            }
            Event::Image(hwnd, spec, path, modified, visible) => {
                // Native layout can queue image requests before a tab event
                // is handled. Do not give those stale requests the new gate.
                let current = images::spec(hwnd as _) == spec
                    && self
                        .rows(hwnd)
                        .iter()
                        .any(|row| row.path == path && row.modified == modified);
                if let Some(window) = self.windows.get(&hwnd).filter(|_| current) {
                    self.worker.images.request(crate::worker::ImageRequest {
                        generation: window.image_gate.load(Ordering::Relaxed),
                        gate: window.image_gate.clone(),
                        path,
                        modified,
                        spec,
                        visible,
                    });
                } else {
                    images::insert(spec, path, modified, None, true);
                    // Image requests are shared across panels. Releasing a
                    // stale owner's pending slot must let another panel retry.
                    for hwnd in self.windows.keys() {
                        if images::spec(*hwnd as _) == spec {
                            unsafe {
                                InvalidateRect(control(*hwnd as _, LIST), null(), 0);
                            }
                        }
                    }
                }
            }
            Event::Zoom(hwnd, delta) => {
                if let Some(index) = self.panel_index(hwnd) {
                    let window = self.windows.get_mut(&hwnd).unwrap();
                    let current = window
                        .pending_zoom
                        .unwrap_or(self.settings.windows[index].zoom);
                    let start = window.pending_zoom.is_none();
                    window.pending_zoom = Some(
                        ((current + delta as f64 / 120.0 * 0.05).clamp(0.7, 2.0) * 1000.0).round()
                            / 1000.0,
                    );
                    unsafe {
                        KillTimer(hwnd as _, ZOOM_STATUS_TIMER);
                        if start {
                            SetTimer(hwnd as _, ZOOM_TIMER, 22, None);
                        }
                    }
                }
            }
            Event::ApplyZoom(hwnd) => {
                unsafe {
                    KillTimer(hwnd as _, ZOOM_TIMER);
                }
                if let Some(index) = self.panel_index(hwnd) {
                    if let Some(zoom) = self
                        .windows
                        .get_mut(&hwnd)
                        .and_then(|w| w.pending_zoom.take())
                    {
                        self.settings.windows[index].zoom = zoom;
                        self.windows[&hwnd]
                            .image_gate
                            .fetch_add(1, Ordering::Relaxed);
                        VIEWS.with(|views| {
                            if let Some(view) = views.borrow_mut().get_mut(&hwnd) {
                                view.zoom = zoom;
                            }
                        });
                        chrome::restyle(hwnd as _);
                        self.tabs(hwnd);
                        set_text(control(hwnd as _, STATUS), &format!("{:.0}%", zoom * 100.0));
                        unsafe {
                            SetTimer(hwnd as _, ZOOM_STATUS_TIMER, 1000, None);
                        }
                        self.changed();
                    }
                }
            }
            Event::HideZoom(hwnd) => {
                unsafe {
                    KillTimer(hwnd as _, ZOOM_STATUS_TIMER);
                }
                if let Some(index) = self.panel_index(hwnd) {
                    let status = control(hwnd as _, STATUS);
                    // A refresh may have replaced the percentage with a loading
                    // message or error while this one-shot timer was pending.
                    if self.windows[&hwnd].pending_zoom.is_none()
                        && text(status)
                            == format!("{:.0}%", self.settings.windows[index].zoom * 100.0)
                    {
                        set_text(status, "");
                    }
                }
            }
            Event::DisplayScale(hwnd) => {
                self.apply_columns(hwnd);
            }
            Event::TaskbarCreated | Event::Timer(TRAY_RETRY_TIMER) => {
                if self.smoke.is_none() {
                    unsafe {
                        if Shell_NotifyIconW(NIM_ADD, &self.tray) != 0
                            || Shell_NotifyIconW(NIM_MODIFY, &self.tray) != 0
                        {
                            Shell_NotifyIconW(NIM_SETVERSION, &self.tray);
                            KillTimer(self.main, TRAY_RETRY_TIMER);
                        } else {
                            SetTimer(self.main, TRAY_RETRY_TIMER, 1000, None);
                        }
                    }
                }
            }
            Event::Metadata(hwnd, path, modified, column) => {
                if let Some(window) = self.windows.get(&hwnd) {
                    let _ = self.worker.sender.send(Job::Metadata {
                        generation: window.generation,
                        gate: window.gate.clone(),
                        path,
                        modified,
                        column,
                    });
                }
            }
            Event::Columns(hwnd) => {
                self.capture_columns(hwnd);
                self.changed();
            }
            Event::Drag(hwnd) => {
                let paths = self.selected_paths(hwnd);
                if !paths.is_empty() {
                    let dragged = paths.clone();
                    shell::drag_out(hwnd as _, &paths, move || {
                        drag_drop::finish_internal(hwnd, &dragged)
                    })?;
                    self.refresh(hwnd);
                }
            }
            Event::Command(hwnd, command) => self.command(hwnd, command)?,
            Event::HeaderDoubleClick(hwnd) => self.header_double_click(hwnd)?,
            Event::Close(hwnd) if hwnd == self.main as usize => {
                if self.settings.close_behavior.eq_ignore_ascii_case("Exit") {
                    self.exiting = true;
                } else {
                    unsafe {
                        ShowWindow(self.main, SW_HIDE);
                    }
                }
            }
            Event::Close(hwnd) => {
                if let Some(index) = self.panel_index(hwnd) {
                    self.settings.windows[index].is_hidden = true;
                    unsafe {
                        ShowWindow(hwnd as _, SW_HIDE);
                    }
                    self.changed();
                }
            }
            Event::Geometry(hwnd) => {
                if let Some(window) = self.windows.get_mut(&hwnd) {
                    if window.hover_restore.take().is_some() {
                        if let Some(panel) = self
                            .settings
                            .windows
                            .iter_mut()
                            .find(|p| p.panel_id == window.id)
                        {
                            panel.is_collapsed = false;
                        }
                    }
                }
                if self.windows.contains_key(&hwnd) {
                    self.capture_geometry();
                    self.changed();
                }
            }
            Event::Search(hwnd) => {
                let query = text(control(hwnd as _, SEARCH)).trim().to_owned();
                if let Some(window) = self.windows.get_mut(&hwnd) {
                    if window.query != query {
                        window.query = query;
                        self.refresh(hwnd);
                    }
                }
            }
            Event::SearchVisibility(hwnd, open) => self.set_search_open(hwnd, open),
            Event::Tab(hwnd, tab) => {
                // Tab selection preserves the panel's temporary hover expansion.
                if let Some(index) = self.panel_index(hwnd) {
                    if tab < self.settings.windows[index].tabs.as_ref().unwrap().len()
                        && tab != self.settings.windows[index].active_tab_index
                    {
                        self.settings.windows[index].active_tab_index = tab;
                        self.tabs(hwnd);
                        self.refresh(hwnd);
                        self.changed();
                    }
                }
            }
            Event::Activate(hwnd, item) if hwnd == self.main as usize => {
                if let Some(panel) = self.settings.windows.get_mut(item) {
                    panel.is_hidden = false;
                    let id = panel.panel_id.clone();
                    if let Some((hwnd, _)) = self.windows.iter().find(|(_, window)| window.id == id)
                    {
                        unsafe {
                            ShowWindow(*hwnd as _, SW_SHOW);
                            SetForegroundWindow(*hwnd as _);
                        }
                    }
                    self.changed();
                }
            }
            Event::Activate(hwnd, item) => {
                if let Some(row) = self.rows(hwnd).get(item) {
                    if row.parent_navigation {
                        self.command(hwnd, UP)?;
                    } else {
                        self.activate(hwnd, &row.path.clone(), row.directory)?;
                    }
                }
            }
            Event::Column(hwnd, column) => {
                if let Some(window) = self.windows.get_mut(&hwnd) {
                    window.descending = window.sort == column && !window.descending;
                    window.sort = column;
                    self.refresh(hwnd);
                }
            }
            Event::Context(hwnd, point) => self.context(hwnd, point)?,
            Event::Hover(hwnd) => self.hover(hwnd),
            Event::Drop(hwnd, paths, copy, shift, folder) => {
                self.drop_files(hwnd, paths, copy, shift, folder)?
            }
            Event::Hotkey(1) => self.toggle(),
            Event::Hotkey(2) => {
                // A queued WM_HOTKEY may arrive after a quick tap was released.
                let held = hotkey_held(&self.settings.global_shortcuts.foreground_panels_hotkey);
                if held {
                    self.activate_foreground_panel();
                }
                self.set_foreground(held);
            }
            Event::Hotkey(_) => {}
            Event::Timer(SAVE_TIMER) => {
                unsafe {
                    KillTimer(self.main, SAVE_TIMER);
                }
                self.capture_geometry();
                self.store.save(&self.settings)?;
            }
            Event::Timer(FOREGROUND_TIMER) => {
                if !hotkey_held(&self.settings.global_shortcuts.foreground_panels_hotkey) {
                    self.set_foreground(false);
                }
            }
            Event::Timer(SMOKE_TIMER) => {
                if let Some(path) = &self.smoke {
                    let rows: usize = self.windows.keys().map(|hwnd| self.rows(*hwnd).len()).sum();
                    let panels: Vec<_> = self.windows.keys().filter_map(|hwnd| self.panel_index(*hwnd).map(|index| serde_json::json!({"title": self.settings.windows[index].panel_title, "rows": self.rows(*hwnd).len(), "query": self.windows[hwnd].query, "zoom": self.settings.windows[index].zoom, "image_pixels": images::spec(*hwnd as _).edge}))).collect();
                    let (thumbnails, icons) = images::counts();
                    let metadata = METADATA.with(|cache| cache.borrow().entries.len());

                    let result = serde_json::json!({"ui": "Win32", "runtime": "Rust", "text_renderer": if text_render::available() { "DirectWrite" } else { "GDI" }, "active_animations": motion::active_count(), "animations_enabled": motion::enabled(), "windows": self.windows.len() + 1, "visible_rows": rows, "elapsed_ms": self.started.elapsed().as_millis(), "pid": std::process::id(), "panels": panels, "thumbnails": thumbnails, "metadata": metadata, "icons": icons, "errors": self.smoke_errors});
                    crate::config::atomic_write(path, &serde_json::to_vec_pretty(&result).unwrap())
                        .map_err(|e| e.to_string())?;
                }
                self.exiting = true;
            }
            Event::Timer(SORT_TIMER) => {
                unsafe {
                    KillTimer(self.main, SORT_TIMER);
                }
                if self.settings.desktop_auto_sort.auto_sort_enabled {
                    self.sort_desktop()?;
                }
            }
            Event::Timer(UPDATE_TIMER) => {
                unsafe {
                    KillTimer(self.main, UPDATE_TIMER);
                }
                if self.settings.auto_check_updates {
                    // Set the retry before spawning so even a worker-start failure retries.
                    unsafe {
                        SetTimer(self.main, UPDATE_TIMER, crate::update::RETRY_MS, None);
                    }
                    self.check_updates(false)?;
                }
            }
            Event::Timer(_) => {}
            Event::Tray(code) => {
                if code == WM_LBUTTONUP || code == NIN_SELECT {
                    unsafe {
                        ShowWindow(self.main, SW_SHOW);
                        SetForegroundWindow(self.main);
                    }
                } else if code == WM_RBUTTONUP || code == WM_CONTEXTMENU {
                    if let Some(command) = popup(
                        self.main,
                        None,
                        &[
                            (OPEN, "Open DesktopPlus".into()),
                            (TOGGLE, "Show / hide panels".into()),
                            (EXIT, "Exit".into()),
                        ],
                    ) {
                        self.command(self.main as usize, command)?;
                    }
                }
            }
        }
        Ok(())
    }
    fn activate(&mut self, hwnd: usize, path: &Path, directory: bool) -> Result<(), String> {
        if let Some(index) = self.panel_index(hwnd) {
            if directory
                && !self.settings.windows[index]
                    .active()
                    .open_folders_externally
                && !self.settings.windows[index].active().pinned()
                && !self.settings.windows[index]
                    .active()
                    .panel_type
                    .eq_ignore_ascii_case("RecycleBin")
            {
                self.settings.windows[index].active_mut().folder_path =
                    path.to_string_lossy().into_owned();
                set_text(control(hwnd as _, SEARCH), "");
                self.configure();
                self.refresh(hwnd);
                self.changed();
                return Ok(());
            }
        }
        shell::open(hwnd as _, path)
    }
    fn context(&mut self, hwnd: usize, point: POINT) -> Result<(), String> {
        if unsafe { GetKeyState(VK_SHIFT as i32) } < 0
            || self.panel_index(hwnd).is_some_and(|index| {
                self.settings.windows[index]
                    .active()
                    .panel_type
                    .eq_ignore_ascii_case("RecycleBin")
            })
        {
            if let Some(path) = self.selected_paths(hwnd).first() {
                shell::context_menu(hwnd as _, path, point)?;
                self.refresh(hwnd);
                return Ok(());
            }
        }
        let mut items = if hwnd == self.main as usize {
            vec![
                (OPEN, "Show panel"),
                (HIDE_PANEL, "Hide panel"),
                (PANEL_SETTINGS, "Panel settings"),
                (COLUMNS, "Details columns…"),
                (REMOVE_PANEL, "Remove panel"),
                (0, ""),
                (NEW_FOLDER, "New folder panel"),
                (NEW_LIST, "New pinned list"),
                (NEW_RECYCLE, "New Recycle Bin panel"),
            ]
        } else {
            vec![
                (OPEN, "Open"),
                (REVEAL, "Show in Explorer"),
                (COPY, "Copy\tCtrl+C"),
                (CUT, "Cut\tCtrl+X"),
                (PASTE, "Paste\tCtrl+V"),
                (RENAME, "Rename\tF2"),
                (DELETE, "Delete\tDel"),
                (PROPERTIES, "Properties"),
                (0, ""),
                (UP, "Parent folder"),
                (HOME, "Home folder"),
                (NEW_DIRECTORY, "New folder"),
                (REFRESH, "Refresh\tF5"),
                (ADD_TAB, "Add tab"),
                (RENAME_TAB, "Rename tab"),
                (CLOSE_TAB, "Close tab"),
                (DETACH_TAB, "Detach tab"),
                (MERGE_TAB, "Merge into another panel"),
                (TAB_LEFT, "Move tab left"),
                (TAB_RIGHT, "Move tab right"),
                (PANEL_SETTINGS, "Panel settings"),
                (VIEW, "View"),
                (COLUMNS, "Details columns…"),
                (WINDOWS_MENU, "Windows context menu…"),
            ]
        };
        if hwnd != self.main as usize {
            if let Some(index) = self.panel_index(hwnd) {
                let kind = &self.settings.windows[index].active().panel_type;
                if matches!(kind.as_str(), "Folder" | "List" | "RecycleBin") {
                    items.extend([(0, ""), (EMPTY_PANEL, empty_panel::caption(kind))]);
                }
            }
        }
        if let Some(command) = popup(
            hwnd as _,
            Some(point),
            &items
                .into_iter()
                .map(|(id, label)| (id, label.into()))
                .collect::<Vec<_>>(),
        ) {
            self.command(hwnd, command)?;
        }
        Ok(())
    }
    fn set_search_open(&mut self, hwnd: usize, open: bool) {
        let changed = VIEWS.with(|views| {
            let mut views = views.borrow_mut();
            let Some(view) = views.get_mut(&hwnd) else {
                return false;
            };
            if view.search_open == open {
                return false;
            }
            view.search_open = open;
            true
        });
        if !changed {
            return;
        }
        let search = control(hwnd as _, SEARCH);
        if !open {
            chrome::block_search_hover(hwnd as _);
            if !text(search).is_empty() {
                set_text(search, "");
            }
        }
        layout_panel_header(hwnd as _);
        unsafe {
            if open {
                SetFocus(search);
            } else if GetFocus() == search {
                SetFocus(control(hwnd as _, LIST));
            }
            chrome::invalidate_header(hwnd as _);
        }
    }
    fn command(&mut self, mut hwnd: usize, command: u16) -> Result<(), String> {
        if hwnd == self.main as usize
            && matches!(
                command,
                OPEN | HIDE_PANEL | REMOVE_PANEL | PANEL_SETTINGS | COLUMNS
            )
        {
            if let Some(index) = selected(self.main).first().copied() {
                if let Some(panel) = self.settings.windows.get(index) {
                    if let Some((window, _)) = self
                        .windows
                        .iter()
                        .find(|(_, window)| window.id == panel.panel_id)
                    {
                        hwnd = *window;
                    }
                }
            }
            if hwnd == self.main as usize && command != OPEN {
                return Ok(());
            }
        }
        match command {
            NEW_RECYCLE => {
                let mut panel = Panel {
                    panel_title: "Recycle Bin".into(),
                    panel_type: "RecycleBin".into(),
                    ..Panel::default()
                };
                panel.normalize();
                self.settings.windows.push(panel.clone());
                self.open_panel(&panel)?;
                self.changed();
            }
            EMPTY_PANEL => {
                let Some(index) = self.panel_index(hwnd) else {
                    return Ok(());
                };
                let tab = self.settings.windows[index].active().clone();
                if tab.panel_type.eq_ignore_ascii_case("RecycleBin") {
                    return self.command(hwnd, EMPTY_RECYCLE);
                }
                let contents = empty_panel::Contents::read(&tab)?;
                if !empty_panel::confirm(hwnd as _, &tab, &contents)? {
                    return Ok(());
                }
                match contents {
                    empty_panel::Contents::Folder(sources) => {
                        self.worker
                            .sender
                            .send(Job::Transfer {
                                owner: hwnd,
                                action: shell::FileAction::Recycle,
                                sources,
                                destination: None,
                            })
                            .map_err(|error| error.to_string())?;
                    }
                    empty_panel::Contents::Pinned(references) => {
                        self.settings.windows[index]
                            .active_mut()
                            .pinned_items
                            .retain(|path| !references.contains(path));
                        self.refresh(hwnd);
                        self.changed();
                    }
                }
            }
            EMPTY_RECYCLE => {
                unsafe {
                    SHEmptyRecycleBinW(hwnd as _, null(), 0);
                }
                for hwnd in self.windows.keys().copied().collect::<Vec<_>>() {
                    self.refresh(hwnd);
                }
            }
            WINDOWS_MENU => {
                if let Some(path) = self.selected_paths(hwnd).first() {
                    let mut point = POINT::default();
                    unsafe {
                        GetCursorPos(&mut point);
                    }
                    shell::context_menu(hwnd as _, path, point)?;
                    self.refresh(hwnd);
                }
            }
            NEW_FOLDER | NEW_LIST => {
                let mut panel = if command == NEW_FOLDER {
                    let Some(path) = shell::choose_folder(self.main)? else {
                        return Ok(());
                    };
                    Panel::folder(&path)
                } else {
                    let Some(values) = dialog::edit(
                        self.main,
                        "New pinned list",
                        vec![Field::Text("Panel name".into(), "Pinned files".into())],
                    ) else {
                        return Ok(());
                    };
                    let mut panel = Panel {
                        panel_title: values[0].clone(),
                        panel_type: "List".into(),
                        ..Panel::default()
                    };
                    panel.normalize();
                    panel
                };
                panel.left += (self.settings.windows.len() % 8) as f64 * 30.0;
                panel.top += (self.settings.windows.len() % 8) as f64 * 30.0;
                panel.normalize();
                self.settings.windows.push(panel.clone());
                self.open_panel(&panel)?;
                self.configure();
                self.changed();
            }
            TOGGLE => self.toggle(),
            EXIT => self.exiting = true,
            OVERVIEW => dashboard::select(self.main, OVERVIEW),
            GENERAL | THEMES | SHORTCUTS => self.open_preference(command)?,
            LAYOUT_BUILDER => {
                if self.panel_index(hwnd).is_some() {
                    self.panel_layout_builder(hwnd)?;
                } else {
                    self.layout_builder()?;
                }
            }
            1 | 2 if hwnd == self.main as usize && dashboard::active(self.main) != OVERVIEW => unsafe {
                SendMessageW(
                    control(self.main, dashboard::form_id(dashboard::active(self.main))),
                    WM_COMMAND,
                    command as usize,
                    0,
                );
            },
            OPEN => {
                if selected(hwnd as _)
                    .first()
                    .and_then(|i| self.rows(hwnd).get(*i).map(|row| row.parent_navigation))
                    .unwrap_or(false)
                {
                    return self.command(hwnd, UP);
                }
                if hwnd == self.main as usize {
                    unsafe {
                        ShowWindow(self.main, SW_SHOW);
                        SetForegroundWindow(self.main);
                    }
                } else if let Some(path) = self.selected_paths(hwnd).first() {
                    self.activate(hwnd, path, path.is_dir())?;
                } else {
                    unsafe {
                        ShowWindow(hwnd as _, SW_SHOW);
                        SetForegroundWindow(hwnd as _);
                    }
                    if let Some(index) = self.panel_index(hwnd) {
                        self.settings.windows[index].is_hidden = false;
                        self.changed();
                    }
                }
            }
            REFRESH => self.refresh(hwnd),
            COPY | CUT => {
                shell::clipboard_set(hwnd as _, &self.selected_paths(hwnd), command == CUT)?
            }
            PASTE => {
                let (files, cut) = shell::clipboard_get(hwnd as _)?;
                self.drop_files(hwnd, files, !cut, cut, None)?;
            }
            REVEAL | PROPERTIES => {
                if let Some(path) = self.selected_paths(hwnd).first() {
                    if command == REVEAL {
                        shell::reveal(hwnd as _, path)?;
                    } else {
                        shell::properties(hwnd as _, path)?;
                    }
                }
            }
            DELETE => {
                let paths = self.selected_paths(hwnd);
                if paths.is_empty() {
                    return Ok(());
                }
                let Some(index) = self.panel_index(hwnd) else {
                    return Ok(());
                };
                if self.settings.windows[index].active().pinned() {
                    self.settings.windows[index]
                        .active_mut()
                        .pinned_items
                        .retain(|path| !paths.contains(&PathBuf::from(path)));
                    self.refresh(hwnd);
                    self.changed();
                } else {
                    let _ = self.worker.sender.send(Job::Transfer {
                        owner: hwnd,
                        action: shell::FileAction::Recycle,
                        sources: paths,
                        destination: None,
                    });
                }
            }
            RENAME => {
                if let Some(path) = self.selected_paths(hwnd).first() {
                    if let Some(values) = dialog::edit(
                        hwnd as _,
                        "Rename item",
                        vec![Field::Text(
                            "Name".into(),
                            path.file_name()
                                .unwrap_or_default()
                                .to_string_lossy()
                                .into_owned(),
                        )],
                    ) {
                        let name = values[0].trim();
                        if name.is_empty()
                            || name.contains(['\\', '/', ':'])
                            || name == "."
                            || name == ".."
                        {
                            return Err("Enter a valid filename.".into());
                        }
                        let target = path.with_file_name(name);
                        if target != *path && target.exists() {
                            return Err("An item with that name already exists.".into());
                        }
                        fs_rename(path, &target)?;
                        self.refresh(hwnd);
                    }
                }
            }
            UP | HOME => {
                if let Some(index) = self.panel_index(hwnd) {
                    let tab = self.settings.windows[index].active_mut();
                    let target = if command == HOME {
                        Some(PathBuf::from(&tab.default_folder_path))
                    } else {
                        Path::new(&tab.folder_path).parent().map(Path::to_owned)
                    };
                    if let Some(target) = target {
                        if target.is_dir() {
                            tab.folder_path = target.to_string_lossy().into_owned();
                            self.configure();
                            self.refresh(hwnd);
                            self.changed();
                        }
                    }
                }
            }
            NEW_DIRECTORY => {
                if let Some(index) = self.panel_index(hwnd) {
                    if let Some(values) = dialog::edit(
                        hwnd as _,
                        "New folder",
                        vec![Field::Text("Folder name".into(), "New folder".into())],
                    ) {
                        if values[0].trim().is_empty()
                            || values[0].contains(['\\', '/', ':'])
                            || matches!(values[0].as_str(), "." | "..")
                        {
                            return Err("Enter a valid folder name.".into());
                        }
                        std::fs::create_dir(
                            Path::new(&self.settings.windows[index].active().folder_path)
                                .join(&values[0]),
                        )
                        .map_err(|e| e.to_string())?;
                        self.refresh(hwnd);
                    }
                }
            }
            ADD_TAB => {
                if let Some(index) = self.panel_index(hwnd) {
                    if let Some(path) = shell::choose_folder(hwnd as _)? {
                        let panel = &mut self.settings.windows[index];
                        panel.tabs.as_mut().unwrap().push(Tab::folder(&path));
                        panel.active_tab_index = panel.tabs.as_ref().unwrap().len() - 1;
                        self.tabs(hwnd);
                        self.configure();
                        self.refresh(hwnd);
                        self.changed();
                    }
                }
            }
            RENAME_TAB => {
                if let Some(index) = self.panel_index(hwnd) {
                    if let Some(values) = dialog::edit(
                        hwnd as _,
                        "Rename tab",
                        vec![Field::Text(
                            "Tab name".into(),
                            self.settings.windows[index].active().tab_name.clone(),
                        )],
                    ) {
                        self.settings.windows[index].active_mut().tab_name = values[0].clone();
                        self.tabs(hwnd);
                        self.changed();
                    }
                }
            }
            CLOSE_TAB | DETACH_TAB | TAB_LEFT | TAB_RIGHT | MERGE_TAB => {
                self.tab_command(hwnd, command)?
            }
            VIEW => {
                if let Some(index) = self.panel_index(hwnd) {
                    if let Some(choice) = popup(
                        hwnd as _,
                        None,
                        &[
                            (1, "Details".into()),
                            (2, "Icons".into()),
                            (3, "Photos".into()),
                        ],
                    ) {
                        self.settings.windows[index].active_mut().view_mode = match choice {
                            2 => "icons",
                            3 => "photos",
                            _ => "details",
                        }
                        .into();
                        self.tabs(hwnd);
                        self.refresh(hwnd);
                        self.changed();
                    }
                }
            }
            FIT_CONTENT => {
                if !self.fit_contents(hwnd, true) {
                    self.fit_contents(hwnd, false);
                }
            }
            COLLAPSE => {
                if let Some(window) = self.windows.get_mut(&hwnd) {
                    window.hover_held_open = false;
                }
                if self
                    .windows
                    .get(&hwnd)
                    .is_some_and(|window| window.hover_restore.is_some())
                {
                    self.restore_hover(hwnd);
                    return Ok(());
                }
                if let Some(index) = self.panel_index(hwnd) {
                    self.set_panel_collapsed(hwnd, !self.settings.windows[index].is_collapsed);
                }
            }
            HIDE_PANEL => {
                if let Some(index) = self.panel_index(hwnd) {
                    self.settings.windows[index].is_hidden = true;
                    unsafe {
                        ShowWindow(hwnd as _, SW_HIDE);
                    }
                    self.changed();
                }
            }
            REMOVE_PANEL => {
                if let Some(index) = self.panel_index(hwnd) {
                    self.backup("Before removing panel")?;
                    self.settings.windows.remove(index);
                    self.windows.remove(&hwnd);
                    unsafe {
                        DestroyWindow(hwnd as _);
                    }
                    self.configure();
                    self.changed();
                }
            }
            SEARCH_TOGGLE => {
                let open =
                    VIEWS.with(|views| views.borrow().get(&hwnd).is_some_and(|v| v.search_open));
                self.set_search_open(hwnd, !open);
            }
            PANEL_SETTINGS => self.panel_dialog(hwnd)?,
            COLUMNS => self.columns_dialog(hwnd),
            LAYOUTS => self.layouts()?,
            BACKUPS => self.backups()?,
            AUTO_SORT | SORT_NOW => self.auto_sort(command == SORT_NOW)?,
            HELP => {
                shell::open(
                    self.main,
                    Path::new("https://github.com/Koala280/DesktopPlus"),
                )?;
            }
            _ => {}
        }
        Ok(())
    }
    fn drop_files(
        &mut self,
        hwnd: usize,
        files: Vec<PathBuf>,
        copy: bool,
        shift: bool,
        folder: Option<PathBuf>,
    ) -> Result<(), String> {
        if files.is_empty() {
            return Ok(());
        }
        let Some(index) = self.panel_index(hwnd) else {
            return Ok(());
        };
        let tab = self.settings.windows[index].active_mut();
        if folder.is_none() && tab.pinned() {
            for file in files {
                let file = file.to_string_lossy().into_owned();
                if !tab
                    .pinned_items
                    .iter()
                    .any(|path| path.eq_ignore_ascii_case(&file))
                {
                    tab.pinned_items.push(file);
                }
            }
            self.refresh(hwnd);
            self.changed();
        } else if folder.is_none() && tab.panel_type.eq_ignore_ascii_case("RecycleBin") {
            let _ = self.worker.sender.send(Job::Transfer {
                owner: hwnd,
                action: shell::FileAction::Recycle,
                sources: files,
                destination: None,
            });
        } else if copy && shift {
            let folder = folder.unwrap_or_else(|| PathBuf::from(&tab.folder_path));
            for file in files {
                let target = folder.join(format!(
                    "{}.lnk",
                    file.file_name().unwrap_or_default().to_string_lossy()
                ));
                if target.exists() {
                    return Err(format!("{} already exists.", target.display()));
                }
                shell::create_shortcut(&file, &target)?;
            }
            self.refresh(hwnd);
        } else {
            let _ = self.worker.sender.send(Job::Transfer {
                owner: hwnd,
                action: if shift && !copy {
                    shell::FileAction::Move
                } else {
                    shell::FileAction::Copy
                },
                sources: files,
                destination: Some(folder.unwrap_or_else(|| PathBuf::from(&tab.folder_path))),
            });
        }
        Ok(())
    }
    fn tab_command(&mut self, hwnd: usize, command: u16) -> Result<(), String> {
        let Some(index) = self.panel_index(hwnd) else {
            return Ok(());
        };
        let panel = &self.settings.windows[index];
        let active = panel.active_tab_index;
        let count = panel.tabs.as_ref().unwrap().len();
        if command == CLOSE_TAB && count == 1 {
            return Ok(());
        }
        if matches!(command, CLOSE_TAB | DETACH_TAB | MERGE_TAB) {
            self.backup("Before changing tabs")?;
        }
        if command == DETACH_TAB {
            let tab = self.settings.windows[index].active().clone();
            let mut panel = Panel {
                panel_title: tab.tab_name.clone(),
                tabs: Some(vec![tab]),
                ..Panel::default()
            };
            panel.normalize();
            self.settings.windows.push(panel.clone());
            self.open_panel(&panel)?;
            if count > 1 {
                self.settings.windows[index]
                    .tabs
                    .as_mut()
                    .unwrap()
                    .remove(active);
                self.settings.windows[index].active_tab_index = active.min(count - 2);
            } else {
                self.settings.windows.remove(index);
                self.windows.remove(&hwnd);
                unsafe {
                    DestroyWindow(hwnd as _);
                }
            }
        } else if command == MERGE_TAB {
            let choices: Vec<_> = self
                .settings
                .windows
                .iter()
                .enumerate()
                .filter(|(i, _)| *i != index)
                .map(|(i, panel)| (1000 + i as u16, panel.panel_title.clone()))
                .collect();
            if let Some(target) = popup(hwnd as _, None, &choices) {
                let target = (target - 1000) as usize;
                let tab = self.settings.windows[index].active().clone();
                self.settings.windows[target]
                    .tabs
                    .as_mut()
                    .unwrap()
                    .push(tab);
                self.settings.windows[target].active_tab_index =
                    self.settings.windows[target].tabs.as_ref().unwrap().len() - 1;
                if count > 1 {
                    self.settings.windows[index]
                        .tabs
                        .as_mut()
                        .unwrap()
                        .remove(active);
                    self.settings.windows[index].active_tab_index = active.min(count - 2);
                } else {
                    self.settings.windows.remove(index);
                    self.windows.remove(&hwnd);
                    unsafe {
                        DestroyWindow(hwnd as _);
                    }
                }
            }
        } else {
            let panel = &mut self.settings.windows[index];
            if command == CLOSE_TAB {
                panel.tabs.as_mut().unwrap().remove(active);
                panel.active_tab_index = active.min(count - 2);
            } else {
                let target = if command == TAB_LEFT {
                    active.saturating_sub(1)
                } else {
                    (active + 1).min(count - 1)
                };
                panel.tabs.as_mut().unwrap().swap(active, target);
                panel.active_tab_index = target;
            }
        }
        for hwnd in self.windows.keys().copied().collect::<Vec<_>>() {
            self.tabs(hwnd);
            self.refresh(hwnd);
        }
        self.configure();
        self.changed();
        Ok(())
    }
    fn panel_dialog(&mut self, hwnd: usize) -> Result<(), String> {
        let Some(index) = self.panel_index(hwnd) else {
            return Ok(());
        };
        let mut editor = panel_options::Editor::new(&self.settings.windows[index], &self.settings);
        let pages = std::mem::take(&mut editor.pages);
        let subtitle = format!(
            "{} · {}",
            self.settings.windows[index].panel_title,
            self.settings.windows[index].active().tab_name
        );
        // Keep hover expansion while the modal form owns the panel. Queued hover
        // events resume normal pointer tracking once the settings close.
        if let Some(answer) = form::show(hwnd as _, "Panel settings", &subtitle, pages, &|values| {
            editor.validate(values)
        }) {
            editor.apply(&mut self.settings.windows[index], &answer.values);
            self.apply_panel_theme(hwnd);
            self.tabs(hwnd);
            self.configure();
            self.refresh(hwnd);
            self.changed();
            if let Some(action) = answer.action {
                if matches!(action, panel_options::FIT_WIDTH | panel_options::FIT_HEIGHT) {
                    if let Some(window) = self.windows.get_mut(&hwnd) {
                        window.fit_after_load = Some(action == panel_options::FIT_WIDTH);
                    }
                } else {
                    self.command(hwnd, action)?;
                }
            }
        }
        Ok(())
    }
    fn relabel(&self) {
        crate::locale::configure(&self.settings.language, &self.store.data.join("Languages"));
        set_text(
            control(self.main, TITLE),
            &crate::locale::text("Your workspace"),
        );
        set_text(
            control(self.main, SUBTITLE),
            &crate::locale::text("Your desktop, organized."),
        );
        for (id, caption) in [(NEW_FOLDER, "+ Folder panel"), (NEW_LIST, "+ Pinned list")]
            .into_iter()
            .chain(dashboard::NAVIGATION.iter().copied())
        {
            set_text(control(self.main, id as i32), &crate::locale::text(caption));
        }
        for command in dashboard::PREFERENCES {
            form::relabel(control(self.main, dashboard::form_id(*command)));
        }
        for hwnd in self.windows.keys() {
            for (id, caption) in [
                (UP, "Parent folder"),
                (HOME, "Home folder"),
                (VIEW, "View"),
                (PANEL_SETTINGS, "Panel settings"),
                (COLLAPSE, "Collapse / expand"),
                (HIDE_PANEL, "Hide panel"),
                (SEARCH_TOGGLE, "Search files and folders…"),
                (MOVE_PANEL, "Move panel"),
            ] {
                set_text(
                    control(*hwnd as _, id as i32),
                    &crate::locale::text(caption),
                );
            }
            self.tabs(*hwnd);
        }
        for hwnd in std::iter::once(self.main as usize).chain(self.windows.keys().copied()) {
            let captions: Vec<_> = if hwnd == self.main as usize {
                vec!["Panel", "Folder / source", "Tabs", "Visible"]
            } else {
                crate::metadata::COLUMNS
                    .iter()
                    .map(|(_, caption, _)| *caption)
                    .collect()
            };
            for (index, caption) in captions.iter().enumerate() {
                let mut caption = wide(crate::locale::text(caption));
                let column = LVCOLUMNW {
                    mask: LVCF_TEXT,
                    pszText: caption.as_mut_ptr(),
                    ..unsafe { std::mem::zeroed() }
                };
                unsafe {
                    SendMessageW(
                        control(hwnd as _, LIST),
                        LVM_SETCOLUMNW,
                        index,
                        &column as *const _ as isize,
                    );
                }
            }
            unsafe {
                InvalidateRect(hwnd as _, null(), 1);
            }
        }
    }
    fn apply_theme(&self) {
        // Replace fonts only after controls have stopped referencing them.
        let new = Palette::new(&self.settings.appearance);
        for hwnd in std::iter::once(self.main as usize).chain(self.windows.keys().copied()) {
            unsafe {
                let mut child = GetWindow(hwnd as _, GW_CHILD);
                while !child.is_null() {
                    SendMessageW(
                        child,
                        WM_SETFONT,
                        if GetDlgCtrlID(child) == TITLE {
                            new.heading as usize
                        } else {
                            new.font as usize
                        },
                        1,
                    );
                    child = GetWindow(child, GW_HWNDNEXT);
                }
            }
        }
        PALETTE.with(|palette| *palette.borrow_mut() = new);
        for hwnd in std::iter::once(self.main as usize).chain(self.windows.keys().copied()) {
            refresh_theme(hwnd as _);
        }
        for command in dashboard::PREFERENCES {
            form::restyle(control(self.main, dashboard::form_id(*command)));
        }
        for hwnd in self.windows.keys() {
            if let Some(index) = self.panel_index(*hwnd) {
                VIEWS.with(|views| {
                    if let Some(view) = views.borrow_mut().get_mut(hwnd) {
                        view.options = panel_options::Options::resolved(
                            &self.settings.windows[index],
                            &self.settings.appearance,
                        );
                    }
                });
                layout(*hwnd as _);
            }
            self.apply_panel_theme(*hwnd);
        }
    }
    fn layouts(&mut self) -> Result<(), String> {
        let mut items = vec![(1, "Save current layout…".into())];
        items.extend(
            self.settings
                .layouts
                .iter()
                .enumerate()
                .map(|(i, layout)| (1000 + i as u16, layout.name.clone())),
        );
        if let Some(choice) = popup(self.main, None, &items) {
            if choice == 1 {
                if let Some(values) = dialog::edit(
                    self.main,
                    "Save layout",
                    vec![Field::Text("Layout name".into(), "My layout".into())],
                ) {
                    self.capture_geometry();
                    let layout = Layout {
                        name: values[0].clone(),
                        panels: self.settings.windows.clone(),
                        appearance: self.settings.appearance.clone(),
                        ..Layout::default()
                    };
                    if let Some(existing) = self
                        .settings
                        .layouts
                        .iter_mut()
                        .find(|layout| layout.name.eq_ignore_ascii_case(&values[0]))
                    {
                        *existing = layout;
                    } else {
                        self.settings.layouts.push(layout);
                    }
                    self.changed();
                }
            } else if let Some(layout) =
                self.settings.layouts.get((choice - 1000) as usize).cloned()
            {
                self.backup("Before applying layout")?;
                self.settings.windows = layout.panels;
                self.settings.appearance = layout.appearance;
                self.reopen()?;
            }
        }
        Ok(())
    }
    fn reopen(&mut self) -> Result<(), String> {
        for (hwnd, window) in self.windows.drain() {
            window.gate.fetch_add(1, Ordering::Relaxed);
            unsafe {
                DestroyWindow(hwnd as _);
            }
        }
        self.settings.normalize();
        // Layouts and restores replace the settings underlying the cached forms.
        let page = dashboard::active(self.main);
        for command in dashboard::PREFERENCES {
            unsafe {
                DestroyWindow(control(self.main, dashboard::form_id(*command)));
            }
        }
        self.apply_theme();
        self.relabel();
        if page != OVERVIEW {
            self.open_preference(page)?;
        }
        for panel in self.settings.windows.clone() {
            self.open_panel(&panel)?;
        }
        self.configure();
        self.changed();
        Ok(())
    }
    fn backups(&mut self) -> Result<(), String> {
        let directory = self.store.data.join("Backups");
        let history_directory = directory.clone();
        let history = dialog::work(self.main, "Loading backups", move || {
            let mut history = Vec::new();
            for path in backup::list(&history_directory)?.into_iter().take(1000) {
                let label = backup::read(&path)
                    .map(|backup| format!("{} — {}", backup.created, backup.reason))
                    .unwrap_or_else(|_| "Unreadable backup".into());
                history.push((path, label));
            }
            Ok(history)
        })?;
        let mut items = vec![
            (1, "Create backup".into()),
            (2, "Open backup folder".into()),
        ];
        for (i, (_, label)) in history.iter().enumerate() {
            items.push((1000 + i as u16, label.clone()));
        }
        match popup(self.main, None, &items) {
            Some(1) => {
                self.backup("Manual backup")?;
                self.status("Backup created.");
            }
            Some(2) => {
                std::fs::create_dir_all(&directory).map_err(|e| e.to_string())?;
                shell::open(self.main, &directory)?;
            }
            Some(choice) if choice >= 1000 => {
                if let Some((path, _)) = history.get((choice - 1000) as usize) {
                    match popup(
                        self.main,
                        None,
                        &[
                            (1, "Restore this backup".into()),
                            (2, "Delete this backup".into()),
                        ],
                    ) {
                        Some(1)
                            if confirm(
                                self.main,
                                "Restore this layout and its recorded file moves? A safety backup will be created first.",
                            ) =>
                        {
                            let path = path.clone();
                            let directory = directory.clone();
                            let data = self.store.data.clone();
                            let settings = self.settings.clone();
                            let (mut restore, skipped) =
                                dialog::work(self.main, "Restoring backup", move || {
                                    let restore = backup::read(&path)?;
                                    backup::create(
                                        &directory,
                                        &settings,
                                        "Before restore",
                                        Vec::new(),
                                        Some(&path),
                                    )?;
                                    let skipped = backup::restore(&restore, &data)?;
                                    Ok((restore, skipped))
                                })?;
                            restore.settings.normalize();
                            self.settings = restore.settings;
                            self.reopen()?;
                            if !skipped.is_empty() {
                                let report = self.store.data.join("Backups/last-restore.txt");
                                crate::config::atomic_write(
                                    &report,
                                    skipped.join("\r\n").as_bytes(),
                                )
                                .map_err(|e| e.to_string())?;
                                message(
                                    self.main,
                                    &format!(
                                        "{} file moves were skipped. Details: {}",
                                        skipped.len(),
                                        report.display()
                                    ),
                                );
                            }
                        }
                        Some(2)
                            if confirm(
                                self.main,
                                "Delete this backup? Your files will not be deleted.",
                            ) =>
                        {
                            let path = path.clone();
                            dialog::work(self.main, "Deleting backup", move || {
                                std::fs::remove_file(path).map_err(|e| e.to_string())
                            })?;
                        }
                        _ => {}
                    }
                }
            }
            _ => {}
        }
        Ok(())
    }
    fn auto_sort(&mut self, run: bool) -> Result<(), String> {
        if run {
            return self.sort_desktop();
        }
        let choices = [
            (1, "Sort desktop now".into()),
            (2, "Edit rules".into()),
            (
                3,
                if self.settings.desktop_auto_sort.auto_sort_enabled {
                    "Disable automatic sorting"
                } else {
                    "Enable automatic sorting"
                }
                .into(),
            ),
        ];
        match popup(self.main, None, &choices) {
            Some(1) => self.sort_desktop()?,
            Some(2) => {
                let mut rules: Vec<_> = self
                    .settings
                    .desktop_auto_sort
                    .rules
                    .iter()
                    .enumerate()
                    .map(|(i, rule)| {
                        (
                            1000 + i as u16,
                            format!(
                                "{} → {}{}",
                                rule.rule_name,
                                rule.target_panel_name,
                                if rule.enabled { "" } else { " (disabled)" }
                            ),
                        )
                    })
                    .collect();
                rules.push((1, "Add rule…".into()));
                if let Some(choice) = popup(self.main, None, &rules) {
                    let index = if choice == 1 {
                        self.settings.desktop_auto_sort.rules.len()
                    } else {
                        (choice - 1000) as usize
                    };
                    let rule = self
                        .settings
                        .desktop_auto_sort
                        .rules
                        .get(index)
                        .cloned()
                        .unwrap_or_default();
                    if let Some(values) = dialog::edit(
                        self.main,
                        "Sort rule",
                        vec![
                            Field::Text("Rule name".into(), rule.rule_name.clone()),
                            Field::Text(
                                "Extensions (space separated, for example .pdf .docx)".into(),
                                rule.extensions.join(" "),
                            ),
                            Field::Text("Target panel".into(), rule.target_panel_name.clone()),
                            Field::Check("Enabled".into(), rule.enabled),
                            Field::Check("Match folders".into(), rule.match_folders),
                            Field::Check("Catch all other files".into(), rule.catch_all),
                        ],
                    ) {
                        let mut rule = rule;
                        rule.rule_name = values[0].clone();
                        rule.extensions = values[1]
                            .split_whitespace()
                            .map(|ext| format!(".{}", ext.trim_start_matches('.').to_lowercase()))
                            .collect();
                        rule.target_panel_name = values[2].clone();
                        rule.enabled = values[3] == "1";
                        rule.match_folders = values[4] == "1";
                        rule.catch_all = values[5] == "1";
                        if index < self.settings.desktop_auto_sort.rules.len() {
                            self.settings.desktop_auto_sort.rules[index] = rule;
                        } else {
                            self.settings.desktop_auto_sort.rules.push(rule);
                        }
                        self.changed();
                    }
                }
            }
            Some(3) => {
                self.settings.desktop_auto_sort.auto_sort_enabled =
                    !self.settings.desktop_auto_sort.auto_sort_enabled;
                self.configure();
                self.changed();
            }
            _ => {}
        }
        Ok(())
    }
    fn sort_desktop(&mut self) -> Result<(), String> {
        if self.sorting {
            return Ok(());
        }
        self.capture_geometry();
        self.sorting = true;
        self.status("Sorting desktop…");
        self.worker
            .sender
            .send(Job::Sort {
                settings: Box::new(self.settings.clone()),
                roots: vec![shell::desktop()],
                storage: crate::config::local_data().join("AutoSortStorage"),
                backups: self.store.data.join("Backups"),
            })
            .map_err(|e| e.to_string())
    }
}

fn fs_rename(from: &Path, to: &Path) -> Result<(), String> {
    if unsafe {
        windows_sys::Win32::Storage::FileSystem::MoveFileExW(
            wide(from).as_ptr(),
            wide(to).as_ptr(),
            0,
        )
    } == 0
    {
        Err(shell::error_text())
    } else {
        Ok(())
    }
}
fn hotkey(value: &str) -> Option<(u32, u32)> {
    let mut modifiers = 0;
    let mut key = None;
    for part in value.split('+').map(str::trim) {
        match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => modifiers |= MOD_CONTROL,
            "alt" => modifiers |= MOD_ALT,
            "shift" => modifiers |= MOD_SHIFT,
            "win" | "windows" => modifiers |= MOD_WIN,
            value if value.len() == 1 && value.as_bytes()[0].is_ascii_alphanumeric() => {
                key = Some(value.as_bytes()[0].to_ascii_uppercase() as u32)
            }
            value if value.starts_with('f') => {
                let number: u32 = value[1..].parse().ok()?;
                if !(1..=24).contains(&number) {
                    return None;
                }
                key = Some(VK_F1 as u32 + number - 1);
            }
            _ => return None,
        }
    }
    key.map(|key| (modifiers, key))
}
fn hotkey_held(value: &str) -> bool {
    hotkey_held_with(value, |key| unsafe { GetAsyncKeyState(key as i32) } < 0)
}
fn hotkey_held_with(value: &str, pressed: impl Fn(u32) -> bool) -> bool {
    let Some((modifiers, key)) = hotkey(value) else {
        return false;
    };
    if !pressed(key) {
        return false;
    }
    [
        (MOD_CONTROL, VK_CONTROL),
        (MOD_ALT, VK_MENU),
        (MOD_SHIFT, VK_SHIFT),
    ]
    .iter()
    .all(|(modifier, key)| modifiers & modifier == 0 || pressed(*key as u32))
        && (modifiers & MOD_WIN == 0 || pressed(VK_LWIN as u32) || pressed(VK_RWIN as u32))
}
