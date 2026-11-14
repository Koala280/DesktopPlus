//! Empty the active folder or pinned list, independently of search/view filters.
use super::*;
use crate::model::Tab;
use windows_sys::Win32::System::SystemServices::{SS_LEFT, SS_NOPREFIX};

pub(super) enum Contents {
    Folder(Vec<PathBuf>),
    Pinned(Vec<String>),
}
impl Contents {
    pub fn read(tab: &Tab) -> Result<Self, String> {
        if tab.pinned() {
            return Ok(Self::Pinned(tab.pinned_items.clone()));
        }
        let folder = PathBuf::from(&tab.folder_path);
        if !tab.panel_type.eq_ignore_ascii_case("Folder") || !folder.is_absolute() {
            return Err(crate::locale::text("No folder to empty."));
        }
        // Read direct children, not the displayed rows: those may be recursive
        // search results, filtered photos, or include a parent-navigation row.
        // Collect every result before asking; an enumeration error aborts all.
        let paths = std::fs::read_dir(&folder)
            .and_then(|entries| {
                entries
                    .map(|entry| entry.map(|entry| entry.path()))
                    .collect()
            })
            .map_err(|error| format!("{}: {error}", folder.display()))?;
        Ok(Self::Folder(paths))
    }
    fn len(&self) -> usize {
        match self {
            Self::Folder(paths) => paths.len(),
            Self::Pinned(paths) => paths.len(),
        }
    }
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

pub(super) fn caption(kind: &str) -> &'static str {
    if kind.eq_ignore_ascii_case("List") {
        "Clear pinned list…"
    } else if kind.eq_ignore_ascii_case("RecycleBin") {
        "Empty Recycle Bin…"
    } else {
        "Empty folder…"
    }
}

struct Confirmation {
    owner: HWND,
    answer: Option<bool>,
    source: Vec<u16>,
}

pub(super) fn confirm(owner: HWND, tab: &Tab, contents: &Contents) -> Result<bool, String> {
    let pinned = matches!(contents, Contents::Pinned(_));
    let title = crate::locale::text(if pinned {
        "Clear pinned list?"
    } else {
        "Empty folder?"
    });
    let source = if pinned {
        &tab.tab_name
    } else {
        &tab.folder_path
    };
    let body = crate::locale::text(if pinned {
        "Remove {count} references from this list. The original files are kept."
    } else {
        "Move all {count} items, including hidden items and subfolders, to the Recycle Bin."
    })
    .replace("{count}", &contents.len().to_string());
    let mut state = Confirmation {
        owner,
        answer: None,
        source: wide(source),
    };
    unsafe {
        let class = wide("DesktopPlus.Rust.EmptyPanel");
        let instance = GetModuleHandleW(null());
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let mut anchor = RECT::default();
        GetWindowRect(control(owner, EMPTY_PANEL as i32), &mut anchor);
        let monitor = MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST);
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..std::mem::zeroed()
        };
        GetMonitorInfoW(monitor, &mut info);
        let work = info.rcWork;
        // Reserve enough space for larger configured fonts as well as translations.
        let text_size = with_palette(owner, |p| p.size.max(15.0));
        let growth = (text_size / 15.0).max(1.0);
        let logical_width = (344.0 * growth).ceil() as i32;
        let logical_height = (216.0 * growth).ceil() as i32;
        let width = chrome::scale(owner, logical_width).min(work.right - work.left);
        let height = chrome::scale(owner, logical_height).min(work.bottom - work.top);
        let x = (anchor.right - width).clamp(work.left, work.right - width);
        let y = (anchor.bottom + chrome::scale(owner, 6)).clamp(work.top, work.bottom - height);
        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_CONTROLPARENT,
            class.as_ptr(),
            wide(&title).as_ptr(),
            WS_POPUP | WS_BORDER | WS_CLIPCHILDREN,
            x,
            y,
            width,
            height,
            owner,
            null_mut(),
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(shell::error_text());
        }
        SetWindowLongPtrW(
            hwnd,
            GWLP_USERDATA,
            &mut state as *mut Confirmation as isize,
        );
        theme_frame(hwnd);
        // Native static controls keep the complete source and explanation
        // available to accessibility tools, even if the path is ellipsized.
        for (id, value, style, top, lines) in [
            (TITLE, title.as_str(), SS_LEFT, 16, 24),
            (SOURCE, source.as_str(), SS_LEFT | SS_PATHELLIPSIS, 49, 24),
            (SUBTITLE, body.as_str(), SS_LEFT, 82, 73),
        ] {
            let label = CreateWindowExW(
                0,
                wide("STATIC").as_ptr(),
                wide(value).as_ptr(),
                WS_CHILD | WS_VISIBLE | SS_NOPREFIX | style,
                0,
                0,
                0,
                0,
                hwnd,
                id as usize as _,
                instance,
                null(),
            );
            SendMessageW(
                label,
                WM_SETFONT,
                chrome::font(owner, usize::from(id == TITLE)) as usize,
                0,
            );
            move_control(
                hwnd,
                id,
                16,
                (f64::from(top) * growth) as i32,
                logical_width - 32,
                (f64::from(lines) * growth) as i32,
            );
        }
        let cancel = button(hwnd, "Cancel", IDCANCEL as u16);
        let accept = button(hwnd, "Clear", IDOK as u16);
        EnableWindow(accept, i32::from(!contents.is_empty()));
        for handle in [cancel, accept] {
            SendMessageW(handle, WM_SETFONT, chrome::font(owner, 0) as usize, 0);
        }
        let button_width = (104.0 * growth) as i32;
        let button_height = (34.0 * growth) as i32;
        let top = logical_height - button_height - 16;
        move_control(
            hwnd,
            IDCANCEL,
            logical_width - 24 - button_width * 2,
            top,
            button_width,
            button_height,
        );
        move_control(
            hwnd,
            IDOK,
            logical_width - 16 - button_width,
            top,
            button_width,
            button_height,
        );
        chrome::tooltips(hwnd, &[SOURCE as u16]);
        EnableWindow(owner, 0);
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        SetFocus(cancel);
        let mut msg = MSG::default();
        while state.answer.is_none() {
            let result = GetMessageW(&mut msg, null_mut(), 0, 0);
            if result <= 0 {
                if result == 0 {
                    PostQuitMessage(msg.wParam as i32);
                }
                break;
            }
            if msg.message == WM_KEYDOWN && matches!(msg.wParam as u16, VK_ESCAPE | VK_RETURN) {
                state.answer = Some(msg.wParam == VK_RETURN as usize && GetFocus() == accept);
            } else if IsDialogMessageW(hwnd, &msg) == 0 {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        let restore_focus = GetForegroundWindow() == hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
        DestroyWindow(hwnd);
        EnableWindow(owner, 1);
        if restore_focus {
            SetForegroundWindow(owner);
            SetFocus(control(owner, EMPTY_PANEL as i32));
        }
        Ok(state.answer.unwrap_or(false))
    }
}

unsafe extern "system" fn window_proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let state = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *mut Confirmation;
    if message == WM_NCDESTROY {
        chrome::release(hwnd);
    }
    if state.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    let owner = unsafe { (*state).owner };
    match message {
        WM_COMMAND if matches!((w & 0xffff) as i32, IDOK | IDCANCEL) => {
            if (w & 0xffff) as i32 == IDOK && unsafe { IsWindowEnabled(control(hwnd, IDOK)) } == 0 {
                return 0;
            }
            unsafe {
                (*state).answer = Some((w & 0xffff) as i32 == IDOK);
            }
            return 0;
        }
        WM_CLOSE | WM_ACTIVATE if message == WM_CLOSE || (w & 0xffff) == WA_INACTIVE as usize => {
            unsafe {
                (*state).answer = Some(false);
            }
            return 0;
        }
        WM_ERASEBKGND => return 1,
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, area| {
                    with_palette(owner, |p| chrome::fill(dc, &area, p.surface));
                },
            );
            return 0;
        }
        WM_CTLCOLORSTATIC => {
            return with_palette(owner, |p| unsafe {
                SetTextColor(
                    w as HDC,
                    if GetDlgCtrlID(l as HWND) == TITLE {
                        p.text
                    } else {
                        p.muted
                    },
                );
                SetBkColor(w as HDC, p.surface);
                p.surface_brush as isize
            });
        }
        WM_NOTIFY if l != 0 => {
            let header = unsafe { &*(l as *const NMHDR) };
            if header.code == TTN_GETDISPINFOW {
                let info = unsafe { &mut *(l as *mut NMTTDISPINFOW) };
                info.lpszText = unsafe { (*state).source.as_mut_ptr() };
                return 0;
            }
        }
        WM_DRAWITEM if l != 0 => {
            let item = unsafe { &*(l as *const DRAWITEMSTRUCT) };
            chrome::draw_item(item, |draw| {
                with_palette(owner, |p| {
                    let destructive = draw.CtlID == IDOK as u32;
                    let enabled = unsafe { IsWindowEnabled(draw.hwndItem) } != 0;
                    let hot = chrome::is_hot(draw.hwndItem) || draw.itemState & ODS_SELECTED != 0;
                    let focused = chrome::keyboard_focus(draw.hwndItem);
                    let background = if !enabled {
                        p.surface
                    } else if destructive {
                        blend(
                            p.surface,
                            chrome::danger_color(p),
                            if hot { 22 } else { 12 },
                        )
                    } else if hot {
                        p.hover
                    } else {
                        p.surface
                    };
                    chrome::fill(draw.hDC, &draw.rcItem, p.surface);
                    let mut area = draw.rcItem;
                    unsafe {
                        InflateRect(&mut area, -1, -1);
                    }
                    chrome::rounded(
                        draw.hDC,
                        &area,
                        chrome::scale(hwnd, 10),
                        background,
                        if focused {
                            p.accent
                        } else if destructive {
                            background
                        } else {
                            p.border
                        },
                    );
                    chrome::label(
                        draw.hDC,
                        &text(draw.hwndItem),
                        area,
                        chrome::font(owner, 0),
                        if !enabled {
                            blend(p.surface, p.muted, 45)
                        } else if destructive {
                            chrome::danger_color(p)
                        } else {
                            p.text
                        },
                        DT_CENTER | DT_VCENTER | DT_SINGLELINE,
                    );
                })
            });
            return 1;
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let path =
                std::env::temp_dir().join(format!("desktopplus-empty-{}", crate::model::new_id()));
            std::fs::create_dir(&path).unwrap();
            Self(path)
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    #[test]
    fn folder_snapshot_ignores_filters_and_never_traverses_or_deletes_contents() {
        let fixture = Fixture::new();
        let nested = fixture.0.join("subfolder");
        std::fs::create_dir(&nested).unwrap();
        std::fs::write(nested.join("nested.txt"), "keep").unwrap();
        std::fs::write(fixture.0.join("visible.txt"), "keep").unwrap();
        let hidden = fixture.0.join("hidden.txt");
        std::fs::write(&hidden, "keep").unwrap();
        unsafe {
            use windows_sys::Win32::Storage::FileSystem::*;
            assert_ne!(
                SetFileAttributesW(wide(&hidden).as_ptr(), FILE_ATTRIBUTE_HIDDEN),
                0
            );
        }
        let mut tab = Tab::folder(&fixture.0);
        tab.show_hidden = false;
        tab.view_mode = "photos".into();
        let Contents::Folder(paths) = Contents::read(&tab).unwrap() else {
            panic!("folder expected")
        };
        assert_eq!(paths.len(), 3);
        assert!(
            paths
                .iter()
                .all(|path| path.parent() == Some(fixture.0.as_path()))
        );
        assert!(paths.contains(&hidden));
        assert!(paths.contains(&nested));
        assert_eq!(
            std::fs::read_to_string(nested.join("nested.txt")).unwrap(),
            "keep"
        );
        assert!(fixture.0.join("visible.txt").exists());
    }
    #[test]
    fn list_snapshot_keeps_missing_references_and_never_reads_or_deletes_files() {
        let fixture = Fixture::new();
        let original = fixture.0.join("original.txt");
        std::fs::write(&original, "original").unwrap();
        let tab = Tab {
            panel_type: "List".into(),
            pinned_items: vec![
                original.to_string_lossy().into_owned(),
                fixture.0.join("missing.txt").to_string_lossy().into_owned(),
            ],
            ..Tab::default()
        };
        let Contents::Pinned(references) = Contents::read(&tab).unwrap() else {
            panic!("list expected")
        };
        assert_eq!(references, tab.pinned_items);
        assert_eq!(std::fs::read_to_string(original).unwrap(), "original");
    }
    #[test]
    fn invalid_sources_abort_instead_of_falling_back_to_current_directory() {
        for path in ["", ".", "relative-folder"] {
            let tab = Tab {
                folder_path: path.into(),
                ..Tab::default()
            };
            assert!(Contents::read(&tab).is_err());
        }
        let fixture = Fixture::new();
        assert!(Contents::read(&Tab::folder(&fixture.0.join("missing"))).is_err());
        let mut tab = Tab::folder(&fixture.0);
        assert!(Contents::read(&tab).unwrap().is_empty());
        tab.panel_type = "RecycleBin".into();
        assert!(Contents::read(&tab).is_err());
    }

    #[test]
    fn header_actions_fit_narrow_panels_and_respect_collapse_and_visibility_preference() {
        let hwnd = 1usize as HWND;
        VIEWS.with(|views| {
            views.borrow_mut().insert(
                1,
                ViewData {
                    source: None,
                    rows: Arc::new(Vec::new()),
                    main: Vec::new(),
                    is_main: false,
                    workspace_page: OVERVIEW,
                    photos: false,
                    details: true,
                    single_click: false,
                    columns: [false; 10],
                    collapsed: false,
                    tab_count: 1,
                    kind: "Folder".into(),
                    loaded: true,
                    searching: false,
                    search_open: false,
                    options: panel_options::Options {
                        movement: "button".into(),
                        empty_button: true,
                        search: "button".into(),
                        ..Default::default()
                    },
                    hover_tracking: false,
                    zoom: 1.0,
                },
            );
        });
        for width in [220, 249, 300, 440, 640] {
            let header = chrome::header_layout(hwnd, width);
            assert!(header.empty.unwrap() >= header.search_button.unwrap() + 32);
            let mut buttons: Vec<_> = [
                header.settings,
                header.movement,
                header.up,
                header.search_button,
                header.empty,
                header.collapse,
                header.close,
            ]
            .into_iter()
            .flatten()
            .collect();
            buttons.sort_unstable();
            assert!(buttons[0] >= 0);
            assert!(buttons.last().unwrap() + 26 <= width);
            assert!(buttons.windows(2).all(|pair| pair[1] >= pair[0] + 26));
        }
        VIEWS.with(|views| views.borrow_mut().get_mut(&1).unwrap().collapsed = true);
        assert!(chrome::header_layout(hwnd, 220).empty.is_none());
        VIEWS.with(|views| {
            let mut views = views.borrow_mut();
            let view = views.get_mut(&1).unwrap();
            view.collapsed = false;
            view.options.empty_button = false;
        });
        for kind in ["Folder", "List", "RecycleBin"] {
            VIEWS.with(|views| {
                let mut views = views.borrow_mut();
                let view = views.get_mut(&1).unwrap();
                view.kind = kind.into();
                view.options.empty_button = false;
            });
            let hidden = chrome::header_layout(hwnd, 440);
            assert!(hidden.empty.is_none());
            VIEWS.with(|views| views.borrow_mut().get_mut(&1).unwrap().options.empty_button = true);
            let shown = chrome::header_layout(hwnd, 440);
            assert!(shown.empty.is_some());
            assert!(hidden.identity_width >= shown.identity_width);
        }
        VIEWS.with(|views| views.borrow_mut().remove(&1));
    }
}
