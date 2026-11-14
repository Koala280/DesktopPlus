use super::*;

#[test]
fn builder_applies_theme_and_panel_overrides_by_stable_id() {
    let mut settings = Settings {
        windows: vec![Panel::default(), Panel::default()],
        ..Settings::default()
    };
    let ids: Vec<_> = settings
        .windows
        .iter()
        .map(|panel| panel.panel_id.clone())
        .collect();
    settings.windows.swap(0, 1);
    apply_header_styles(
        &mut settings,
        &ids,
        &["2".into(), "0".into(), "5".into(), "0".into()],
    );
    assert_eq!(settings.appearance.header_style, HeaderStyle::Segmented);
    assert_eq!(settings.windows[0].header_style, None);
    assert_eq!(
        settings.windows[1].header_style,
        Some(HeaderStyle::Floating)
    );
    apply_header_styles(
        &mut settings,
        &ids,
        &["0".into(), "1".into(), "5".into(), "4".into()],
    );
    assert_eq!(settings.appearance.header_style, HeaderStyle::Line);
    assert!(
        settings
            .windows
            .iter()
            .all(|panel| panel.header_style.is_none())
    );
}

struct Workspace(App);
impl Workspace {
    fn new() -> Self {
        let directory =
            std::env::temp_dir().join(format!("desktopplus-tabs-{}", crate::model::new_id()));
        let store = Store::new(Some(directory.join("settings.json")));
        let settings = Settings {
            language: "de".into(),
            close_behavior: "Exit".into(),
            ..Settings::default()
        };
        crate::locale::configure(&settings.language, &store.data.join("Languages"));
        unsafe {
            InitCommonControlsEx(&INITCOMMONCONTROLSEX {
                dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
                dwICC: ICC_LISTVIEW_CLASSES | ICC_STANDARD_CLASSES,
            });
            let class = wide("DesktopPlus.WorkspaceTest");
            let instance = GetModuleHandleW(null());
            RegisterClassW(&WNDCLASSW {
                lpfnWndProc: Some(window_proc),
                hInstance: instance,
                hCursor: LoadCursorW(null_mut(), IDC_ARROW),
                lpszClassName: class.as_ptr(),
                ..std::mem::zeroed()
            });
            let main = CreateWindowExW(
                WS_EX_CONTROLPARENT,
                class.as_ptr(),
                wide("DesktopPlus — Tabs").as_ptr(),
                WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                40,
                40,
                1020,
                720,
                null_mut(),
                null_mut(),
                instance,
                null(),
            );
            assert!(!main.is_null());
            SetWindowLongPtrW(main, GWLP_USERDATA, 1);
            theme_frame(main);
            dashboard::create(main);
            let worker = Worker::start(main as usize, store.data.join("SearchIndex"));
            Self(App {
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
                tray: std::mem::zeroed(),
                smoke: Some(directory.join("unused.json")),
                started: Instant::now(),
                sorting: false,
                smoke_errors: Vec::new(),
            })
        }
    }
    fn page(&self, command: u16) -> HWND {
        control(self.0.main, dashboard::form_id(command))
    }
    fn field(&self, command: u16, index: i32) -> HWND {
        control(control(self.page(command), 801), 1000 + index)
    }
    fn click(&mut self, command: u16, id: i32) {
        unsafe {
            SendMessageW(self.page(command), WM_COMMAND, id as usize, 0);
        }
        self.events();
    }
    fn events(&mut self) {
        loop {
            let event = EVENTS.with(|events| events.borrow_mut().pop_front());
            let Some(event) = event else { break };
            self.0.event(event).unwrap();
        }
    }
}
impl Drop for Workspace {
    fn drop(&mut self) {
        unsafe {
            DestroyWindow(self.0.main);
        }
        VIEWS.with(|views| views.borrow_mut().remove(&(self.0.main as usize)));
        EVENTS.with(|events| events.borrow_mut().clear());
    }
}

#[test]
fn tabs_keep_drafts_and_save_or_cancel_only_their_own_values() {
    let mut workspace = Workspace::new();
    workspace.0.open_preference(THEMES).unwrap();
    set_text(workspace.field(THEMES, 0), "invalid");
    workspace.click(THEMES, IDOK);
    assert!(!text(control(workspace.page(THEMES), 802)).is_empty());
    assert_ne!(workspace.0.settings.appearance.background_color, "invalid");
    workspace.0.open_preference(SHORTCUTS).unwrap();
    set_text(workspace.field(SHORTCUTS, 0), "Ctrl + Alt + J");
    workspace.click(SHORTCUTS, IDOK);
    assert_eq!(
        workspace.0.settings.global_shortcuts.hide_panels_hotkey,
        "Ctrl + Alt + J"
    );
    workspace.0.open_preference(THEMES).unwrap();
    assert_eq!(text(workspace.field(THEMES, 0)), "invalid");
    workspace.click(THEMES, IDCANCEL);
    assert_eq!(
        text(workspace.field(THEMES, 0)),
        workspace.0.settings.appearance.background_color
    );
    set_text(workspace.field(THEMES, 0), "#123456");
    workspace.click(THEMES, IDOK);
    assert_eq!(workspace.0.settings.appearance.background_color, "#123456");
    set_text(workspace.field(THEMES, 0), "#abcdef");
    workspace.0.open_preference(GENERAL).unwrap();
    let english = workspace
        .0
        .preference_languages
        .iter()
        .position(|(code, _)| code == "en")
        .unwrap();
    unsafe {
        SendMessageW(workspace.field(GENERAL, 0), CB_SETCURSEL, english, 0);
    }
    workspace.click(GENERAL, IDOK);
    assert_eq!(workspace.0.settings.language, "en");
    assert_eq!(text(control(workspace.0.main, GENERAL as i32)), "General");
    assert_eq!(text(workspace.field(THEMES, 0)), "#abcdef");
    assert_eq!(workspace.0.settings.appearance.background_color, "#123456");
    workspace
        .0
        .command(workspace.0.main as usize, OVERVIEW)
        .unwrap();
    assert_eq!(dashboard::active(workspace.0.main), OVERVIEW);
}

#[test]
fn appearance_rejects_invalid_font_and_shortcuts_reject_invalid_keys() {
    let valid: Vec<String> = ["#123456", "#112233", "#ffffff", "#abcdef", "Segoe UI", "14"]
        .into_iter()
        .map(str::to_owned)
        .collect();
    assert!(validate(THEMES, &valid).is_ok());
    for size in ["NaN", "inf", "8", "25", "text"] {
        let mut values = valid.clone();
        values[5] = size.into();
        assert!(validate(THEMES, &values).is_err());
    }
    let mut values = valid;
    values[4] = " ".into();
    assert!(validate(THEMES, &values).is_err());
    assert!(validate(SHORTCUTS, &["Ctrl + Alt + H".into(), "F4".into()]).is_ok());
    assert!(validate(SHORTCUTS, &["Ctrl + invalid".into(), "F4".into()]).is_err());
}

#[test]
fn general_update_toggle_saves_and_cancel_restores_the_draft() {
    let mut workspace = Workspace::new();
    workspace.0.open_preference(GENERAL).unwrap();
    unsafe {
        SendMessageW(
            workspace.field(GENERAL, 3),
            BM_SETCHECK,
            BST_CHECKED as usize,
            0,
        );
    }
    workspace.click(GENERAL, IDOK);
    assert!(workspace.0.settings.auto_check_updates);
    workspace.0.store.save(&workspace.0.settings).unwrap();
    assert!(workspace.0.store.load(false).unwrap().auto_check_updates);
    unsafe {
        SendMessageW(
            workspace.field(GENERAL, 3),
            BM_SETCHECK,
            BST_UNCHECKED as usize,
            0,
        );
    }
    workspace.click(GENERAL, IDCANCEL);
    assert!(workspace.0.settings.auto_check_updates);
    assert_eq!(
        unsafe { SendMessageW(workspace.field(GENERAL, 3), BM_GETCHECK, 0, 0) },
        BST_CHECKED as isize
    );
}

/// Opt-in preview of only the changed workspace; no panels or system integrations.
#[test]
#[ignore = "Focused visual check of the workspace tabs"]
fn preview_workspace_tabs() {
    let mut workspace = Workspace::new();
    unsafe {
        ShowWindow(workspace.0.main, SW_SHOWNOACTIVATE);
    }
    println!("Workspace preview PID: {}", std::process::id());
    let deadline = Instant::now() + std::time::Duration::from_secs(50);
    while !workspace.0.exiting && Instant::now() < deadline {
        unsafe {
            let mut message = MSG::default();
            while PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) != 0 {
                if IsDialogMessageW(workspace.0.main, &message) == 0 {
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            }
        }
        workspace.events();
        std::thread::sleep(std::time::Duration::from_millis(10));
    }
}
