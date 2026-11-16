use super::*;

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
        control(
            control(self.page(command), 801),
            1000 + if command == THEMES { index + 6 } else { index },
        )
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
    let editor = layout_builder::Editor::new(&Settings::default(), None);
    let valid = editor.values();
    let index = |key| (0..valid.len()).find(|i| editor.key(*i) == key).unwrap();
    assert!(editor.validate(&valid).is_ok());
    for size in ["NaN", "inf", "8", "25", "text"] {
        let mut values = valid.clone();
        values[index("item_size")] = size.into();
        assert!(editor.validate(&values).is_err());
    }
    let mut values = valid.clone();
    values[index("font")] = " ".into();
    assert!(editor.validate(&values).is_err());
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

fn scroll_info(body: HWND) -> SCROLLINFO {
    let mut info = SCROLLINFO {
        cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
        fMask: SIF_ALL,
        ..unsafe { std::mem::zeroed() }
    };
    assert_ne!(unsafe { GetScrollInfo(body, SB_VERT, &mut info) }, 0);
    info
}

#[test]
fn wheel_over_a_closed_choice_scrolls_the_form_without_changing_its_value() {
    let mut workspace = Workspace::new();
    workspace.0.open_preference(GENERAL).unwrap();
    let body = control(workspace.page(GENERAL), 801);
    let choice = workspace.field(GENERAL, 0);
    unsafe {
        let selected = SendMessageW(choice, CB_GETCURSEL, 0, 0);
        SendMessageW(
            choice,
            WM_MOUSEWHEEL,
            (120u16.wrapping_neg() as usize) << 16,
            0,
        );
        assert_eq!(scroll_info(body).nPos, 48);
        assert_eq!(SendMessageW(choice, CB_GETCURSEL, 0, 0), selected);
    }
}

#[test]
fn workspace_scroll_preserves_small_wheel_movements_and_drafts() {
    let mut workspace = Workspace::new();
    workspace.0.open_preference(THEMES).unwrap();
    let body = control(workspace.page(THEMES), 801);
    set_text(workspace.field(THEMES, 0), "#abcdef");
    let wheel = |delta: i16| unsafe {
        SendMessageW(body, WM_MOUSEWHEEL, (delta as u16 as usize) << 16, 0);
    };
    // Five tiny touchpad deltas must add up to two logical pixels.
    for _ in 0..5 {
        wheel(-1);
    }
    assert_eq!(scroll_info(body).nPos, 2);
    wheel(5);
    assert_eq!(scroll_info(body).nPos, 0);
    wheel(-30);
    assert_eq!(scroll_info(body).nPos, 12);
    wheel(-90);
    assert_eq!(scroll_info(body).nPos, 48);
    unsafe {
        SendMessageW(body, form::SCROLL_TO, 10_000, 0);
    }
    let info = scroll_info(body);
    assert_eq!(info.nPos, info.nMax + 1 - info.nPage as i32);
    wheel(-120);
    assert_eq!(scroll_info(body).nPos, info.nPos);
    wheel(120);
    assert_eq!(scroll_info(body).nPos, (info.nPos - 48).max(0));
    unsafe {
        SendMessageW(body, form::SCROLL_TO, 0, 0);
    }
    assert_eq!(scroll_info(body).nPos, 0);
    assert_eq!(text(workspace.field(THEMES, 0)), "#abcdef");
    workspace.0.open_preference(GENERAL).unwrap();
    workspace.0.open_preference(THEMES).unwrap();
    assert_eq!(text(workspace.field(THEMES, 0)), "#abcdef");
}

/// Uses only the affected settings viewport. Input is deliberately not pumped
/// between scroll steps: a busy wheel queue must not defer painting the fields.
#[test]
#[ignore = "Focused visual check of synchronous workspace scrolling"]
fn workspace_scroll_paints_before_returning_to_input_queue() {
    let mut workspace = Workspace::new();
    unsafe {
        ShowWindow(workspace.0.main, SW_SHOWNOACTIVATE);
    }
    for command in [GENERAL, THEMES] {
        workspace.0.open_preference(command).unwrap();
        let body = control(workspace.page(command), 801);
        unsafe {
            RedrawWindow(
                workspace.0.main,
                null(),
                null_mut(),
                RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW,
            );
        }
        for offset in [32, 64, 96, 24, 0, 10_000, 48, 0] {
            unsafe {
                SendMessageW(body, form::SCROLL_TO, offset, 0);
                assert_eq!(
                    GetUpdateRect(body, null_mut(), 0),
                    0,
                    "page {command}, offset {offset}: background paint is still queued"
                );
                let mut child = GetWindow(body, GW_CHILD);
                while !child.is_null() {
                    let mut area = RECT::default();
                    let mut viewport = RECT::default();
                    let mut visible = RECT::default();
                    GetWindowRect(child, &mut area);
                    GetWindowRect(body, &mut viewport);
                    if IsWindowVisible(child) != 0
                        && IntersectRect(&mut visible, &area, &viewport) != 0
                    {
                        assert_eq!(
                            GetUpdateRect(child, null_mut(), 0),
                            0,
                            "page {command}, offset {offset}: field {} paint is still queued",
                            GetDlgCtrlID(child)
                        );
                    }
                    child = GetWindow(child, GW_HWNDNEXT);
                }
            }
        }
    }
}

/// Opt-in preview of only the changed workspace; no panels or system integrations.
#[test]
fn overview_switch_and_actions_follow_visibility_and_selection() {
    let mut workspace = Workspace::new();
    workspace.0.settings.windows = vec![
        Panel::folder(Path::new("C:\\Downloads")),
        Panel::folder(Path::new("C:\\Projects")),
    ];
    workspace.0.update_main();
    let main = workspace.0.main;
    let list = control(main, LIST);
    unsafe {
        assert_eq!(IsWindowEnabled(control(main, SHOW_PANEL as i32)), 0);
        assert_eq!(IsWindowEnabled(control(main, PANEL_SETTINGS as i32)), 0);
        let state = LVITEMW {
            state: LVIS_SELECTED | LVIS_FOCUSED,
            stateMask: LVIS_SELECTED | LVIS_FOCUSED,
            ..std::mem::zeroed()
        };
        SendMessageW(list, LVM_SETITEMSTATE, 0, &state as *const _ as isize);
        assert_ne!(IsWindowEnabled(control(main, HIDE_PANEL as i32)), 0);
        assert_ne!(IsWindowEnabled(control(main, PANEL_SETTINGS as i32)), 0);
        let mut cell = RECT {
            left: LVIR_BOUNDS as i32,
            top: 3,
            ..Default::default()
        };
        assert_ne!(
            SendMessageW(list, LVM_GETSUBITEMRECT, 0, &mut cell as *mut _ as isize),
            0
        );
        let mut hit = LVHITTESTINFO {
            pt: POINT {
                x: cell.left + 20,
                y: cell.top + 16,
            },
            ..std::mem::zeroed()
        };
        assert_eq!(
            SendMessageW(list, LVM_SUBITEMHITTEST, 0, &mut hit as *mut _ as isize),
            0
        );
        assert_eq!(hit.iSubItem, 3);
        let mut notification = NMITEMACTIVATE {
            hdr: NMHDR {
                hwndFrom: list,
                idFrom: LIST as usize,
                code: NM_CLICK,
            },
            iItem: hit.iItem,
            iSubItem: hit.iSubItem,
            ..std::mem::zeroed()
        };
        SendMessageW(
            main,
            WM_NOTIFY,
            LIST as usize,
            &notification as *const _ as isize,
        );
        // The second notification of a double-click must not toggle back.
        notification.hdr.code = NM_DBLCLK;
        SendMessageW(
            main,
            WM_NOTIFY,
            LIST as usize,
            &notification as *const _ as isize,
        );
    }
    workspace.events();
    assert!(workspace.0.settings.windows[0].is_hidden);
    assert!(!workspace.0.settings.windows[1].is_hidden);
    unsafe {
        assert_ne!(IsWindowEnabled(control(main, SHOW_PANEL as i32)), 0);
        assert_eq!(IsWindowEnabled(control(main, HIDE_PANEL as i32)), 0);
        SendMessageW(list, WM_KEYDOWN, VK_SPACE as usize, 0);
    }
    workspace.events();
    assert!(!workspace.0.settings.windows[0].is_hidden);
    workspace.0.toggle();
    VIEWS.with(|views| {
        assert!(
            views.borrow()[&(main as usize)]
                .main
                .iter()
                .all(|row| !row.visible)
        )
    });
    workspace.0.toggle();
    VIEWS.with(|views| {
        assert!(
            views.borrow()[&(main as usize)]
                .main
                .iter()
                .all(|row| row.visible)
        )
    });
}

#[test]
#[ignore = "Focused visual review of the overview visibility controls only"]
fn preview_overview_visibility() {
    let destination = PathBuf::from(std::env::var_os("DESKTOPPLUS_OVERVIEW_PREVIEW").unwrap());
    std::fs::create_dir_all(&destination).unwrap();
    let mut workspace = Workspace::new();
    for (name, hidden) in [
        ("development", false),
        ("Downloads", true),
        ("FL Projects", false),
        ("Gaming", false),
    ] {
        let mut panel = Panel::folder(&PathBuf::from("C:\\Users\\Demo").join(name));
        panel.is_hidden = hidden;
        if name == "development" {
            panel.tabs.as_mut().unwrap().extend([
                Tab {
                    tab_name: "DesktopPlus".into(),
                    is_hidden: true,
                    ..Tab::default()
                },
                Tab {
                    tab_name: "Notizen".into(),
                    ..Tab::default()
                },
            ]);
        }
        workspace.0.settings.windows.push(panel);
    }
    workspace.0.update_main();
    unsafe {
        let state = LVITEMW {
            state: LVIS_SELECTED,
            stateMask: LVIS_SELECTED,
            ..std::mem::zeroed()
        };
        SendMessageW(
            control(workspace.0.main, LIST),
            LVM_SETITEMSTATE,
            1,
            &state as *const _ as isize,
        );
        for (name, width, height) in [("overview", 1080, 740), ("overview-minimum", 820, 500)] {
            SetWindowPos(
                workspace.0.main,
                HWND_TOPMOST,
                40,
                40,
                width,
                height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
            let deadline = Instant::now() + std::time::Duration::from_millis(350);
            while Instant::now() < deadline {
                let mut message = MSG::default();
                while PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) != 0 {
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
            capture_preview(workspace.0.main, &destination.join(format!("{name}.bmp")));
        }
        unsafe extern "system" fn capture_tab_dialog(owner: HWND, _: u32, timer: usize, _: u32) {
            unsafe {
                let dialog = GetActiveWindow();
                if text(dialog) != crate::locale::text("Manage tabs") {
                    return;
                }
                let path = PathBuf::from(std::env::var_os("DESKTOPPLUS_OVERVIEW_PREVIEW").unwrap())
                    .join("tab-visibility.bmp");
                capture_preview(dialog, &path);
                KillTimer(owner, timer);
                PostMessageW(dialog, WM_COMMAND, IDCANCEL as usize, 0);
            }
        }
        SetTimer(workspace.0.main, 77, 600, Some(capture_tab_dialog));
        let panel = &workspace.0.settings.windows[0];
        let answer = form::show(
            workspace.0.main,
            "Manage tabs",
            "development · Ausgeblendete Tabs bleiben erhalten.",
            tabs::visibility_pages(panel),
            &|_| Ok(()),
        );
        assert!(answer.is_none());
    }
}

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

/// Only the changed panel materials and the layout editor; isolated settings.
#[test]
#[ignore = "Focused visual review of the layout builder and glass materials"]
fn preview_layout_builder_and_glass() {
    use crate::model::GlassMode;
    let destination = std::env::var_os("DESKTOPPLUS_LAYOUT_PREVIEW")
        .map(PathBuf::from)
        .unwrap();
    std::fs::create_dir_all(&destination).unwrap();
    let mut workspace = Workspace::new();
    unsafe {
        OleInitialize(null());
        let instance = GetModuleHandleW(null());
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: wide(CLASS).as_ptr(),
            ..std::mem::zeroed()
        });
        unsafe extern "system" fn backdrop(
            hwnd: HWND,
            message: u32,
            w: WPARAM,
            l: LPARAM,
        ) -> LRESULT {
            if message == WM_PAINT {
                chrome::paint(hwnd, None, |dc, bounds| {
                    chrome::fill(dc, &bounds, color("#D6A477", 0));
                    for i in 0..8 {
                        chrome::fill(
                            dc,
                            &RECT {
                                left: i * 150,
                                top: 0,
                                right: i * 150 + 90,
                                bottom: bounds.bottom,
                            },
                            if i % 2 == 0 {
                                color("#6D789E", 0)
                            } else {
                                color("#759F88", 0)
                            },
                        );
                    }
                });
                return 0;
            }
            unsafe { DefWindowProcW(hwnd, message, w, l) }
        }
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(backdrop),
            hInstance: instance,
            lpszClassName: wide("DesktopPlus.GlassPreviewBackdrop").as_ptr(),
            ..std::mem::zeroed()
        });
        let background = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            wide("DesktopPlus.GlassPreviewBackdrop").as_ptr(),
            null(),
            WS_POPUP,
            40,
            40,
            1120,
            380,
            null_mut(),
            null_mut(),
            instance,
            null(),
        );
        ShowWindow(background, SW_SHOW);
        SetForegroundWindow(background);
        SetWindowPos(background, HWND_BOTTOM, 40, 40, 1120, 380, SWP_NOACTIVATE);
        for (index, mode) in [GlassMode::Frosted, GlassMode::Clear, GlassMode::Solid]
            .into_iter()
            .enumerate()
        {
            let mut panel = Panel::folder(&std::env::current_dir().unwrap());
            panel.panel_title = GlassMode::captions()[mode.index()].to_string();
            panel.active_mut().view_mode = if mode == GlassMode::Clear {
                "details"
            } else {
                "icons"
            }
            .into();
            panel.view_mode = panel.active().view_mode.clone();
            panel.width = 340.0;
            panel.height = 300.0;
            panel.left = 60.0 + index as f64 * 365.0;
            panel.top = 70.0;
            panel.appearance_overrides.glass_mode = Some(mode);
            panel.appearance_overrides.glass_opacity = Some(42);
            workspace.0.settings.windows.push(panel.clone());
            workspace.0.open_panel(&panel).unwrap();
        }
        for hwnd in workspace.0.windows.keys() {
            SetWindowLongPtrW(*hwnd as _, GWLP_HWNDPARENT, background as isize);
            SetWindowPos(
                *hwnd as _,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
        }
        let until = Instant::now() + std::time::Duration::from_secs(3);
        while Instant::now() < until {
            let mut msg = MSG::default();
            while PeekMessageW(&mut msg, null_mut(), 0, 0, PM_REMOVE) != 0 {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            workspace.events();
            workspace.0.outputs();
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        let frosted = workspace
            .0
            .windows
            .iter()
            .find(|(_, w)| w.id == workspace.0.settings.windows[0].panel_id)
            .map(|(h, _)| *h)
            .unwrap();
        SetForegroundWindow(frosted as _);
        for hwnd in workspace.0.windows.keys() {
            assert_ne!(IsWindowVisible(*hwnd as _), 0);
            SetWindowPos(
                *hwnd as _,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
            RedrawWindow(
                *hwnd as _,
                null(),
                null_mut(),
                RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW,
            );
        }
        let frosted = workspace
            .0
            .windows
            .iter()
            .find(|(_, w)| w.id == workspace.0.settings.windows[0].panel_id)
            .map(|(h, _)| *h)
            .unwrap();
        SetForegroundWindow(frosted as _);
        std::thread::sleep(std::time::Duration::from_millis(400));
        for (index, hwnd) in workspace.0.windows.keys().enumerate() {
            let mut area = RECT::default();
            GetWindowRect(*hwnd as _, &mut area);
            let mut cloaked = 0u32;
            windows_sys::Win32::Graphics::Dwm::DwmGetWindowAttribute(
                *hwnd as _,
                windows_sys::Win32::Graphics::Dwm::DWMWA_CLOAKED as _,
                (&mut cloaked as *mut u32).cast(),
                4,
            );
            println!(
                "panel {index}: {},{} {}x{}, cloak={cloaked}, style={:x}, ex={:x}, front={:?}",
                area.left,
                area.top,
                area.right - area.left,
                area.bottom - area.top,
                GetWindowLongPtrW(*hwnd as _, GWL_STYLE),
                GetWindowLongPtrW(*hwnd as _, GWL_EXSTYLE),
                GetWindow(*hwnd as _, GW_HWNDPREV)
            );
            capture_preview(
                *hwnd as _,
                &destination.join(format!("glass-panel-{index}.bmp")),
            );
        }
        capture_preview(background, &destination.join("glass.bmp"));
        for hwnd in workspace.0.windows.keys() {
            ShowWindow(*hwnd as _, SW_HIDE);
            SetWindowLongPtrW(*hwnd as _, GWLP_HWNDPARENT, 0);
        }
        DestroyWindow(background);
        unsafe extern "system" fn close_preview(_: HWND, _: u32, _: usize, _: u32) {
            unsafe {
                let dialog = FindWindowW(
                    wide("DesktopPlus.Rust.PanelSettings").as_ptr(),
                    wide("Layout-Builder").as_ptr(),
                );
                if !dialog.is_null() {
                    let destination =
                        PathBuf::from(std::env::var_os("DESKTOPPLUS_LAYOUT_PREVIEW").unwrap());
                    capture_preview(dialog, &destination.join("builder.bmp"));
                    SendMessageW(dialog, WM_COMMAND, 701, 0);
                    RedrawWindow(
                        dialog,
                        null(),
                        null_mut(),
                        RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW,
                    );
                    capture_preview(dialog, &destination.join("builder-glass.bmp"));
                    PostMessageW(dialog, WM_CLOSE, 0, 0);
                }
            }
        }
        let timer = SetTimer(null_mut(), 0, 1200, Some(close_preview));
        workspace.0.layout_builder().unwrap();
        KillTimer(null_mut(), timer);
        let handles: Vec<_> = workspace.0.windows.keys().copied().collect();
        for hwnd in handles {
            DestroyWindow(hwnd as _);
        }
    }
}

unsafe fn capture_preview(hwnd: HWND, path: &std::path::Path) {
    unsafe {
        windows_sys::Win32::Graphics::Dwm::DwmFlush();
        let mut area = RECT::default();
        GetWindowRect(hwnd, &mut area);
        let width = area.right - area.left;
        let height = area.bottom - area.top;
        let dc = GetDC(null_mut());
        let memory = CreateCompatibleDC(dc);
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: 40,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut pixels = null_mut();
        let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut pixels, null_mut(), 0);
        let old = SelectObject(memory, bitmap);
        BitBlt(
            memory,
            0,
            0,
            width,
            height,
            dc,
            area.left,
            area.top,
            SRCCOPY | CAPTUREBLT,
        );
        GdiFlush();
        let count = (width * height * 4) as usize;
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"BM");
        bytes.extend_from_slice(&((54 + count) as u32).to_le_bytes());
        bytes.extend_from_slice(&[0; 4]);
        bytes.extend_from_slice(&54u32.to_le_bytes());
        bytes.extend_from_slice(std::slice::from_raw_parts(
            (&info.bmiHeader as *const BITMAPINFOHEADER).cast::<u8>(),
            40,
        ));
        bytes.extend_from_slice(std::slice::from_raw_parts(pixels.cast::<u8>(), count));
        std::fs::write(path, bytes).unwrap();
        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(null_mut(), dc);
    }
}
