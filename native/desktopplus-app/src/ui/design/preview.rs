//! Real panel chrome and list rendering, with inert sample files and no app commands.
use super::*;
use crate::model::Panel;

pub(super) fn create(owner: HWND, id: i32, desktop: bool) -> HWND {
    unsafe {
        let class = wide("DesktopPlus.Rust.DesignPreview");
        let instance = GetModuleHandleW(null());
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let mut monitor = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..std::mem::zeroed()
        };
        GetMonitorInfoW(
            MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST),
            &mut monitor,
        );
        let width = chrome::scale(owner, 520).min(monitor.rcWork.right - monitor.rcWork.left);
        let hwnd = CreateWindowExW(
            if desktop { WS_EX_TOOLWINDOW } else { 0 },
            class.as_ptr(),
            wide(crate::locale::text("My workspace")).as_ptr(),
            WS_CLIPCHILDREN
                | if desktop {
                    WS_POPUP | WS_THICKFRAME
                } else {
                    WS_CHILD | WS_VISIBLE
                },
            if desktop {
                monitor.rcWork.right - width - 24
            } else {
                0
            },
            if desktop { monitor.rcWork.top + 40 } else { 0 },
            width,
            chrome::scale(owner, 360),
            owner,
            if desktop {
                null_mut()
            } else {
                id as usize as _
            },
            instance,
            null(),
        );
        if hwnd.is_null() {
            return hwnd;
        }
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, 2);
        VIEWS.with(|views| {
            views.borrow_mut().insert(
                hwnd as usize,
                ViewData {
                    source: None,
                    rows: Arc::new(sample_rows(false)),
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
                    options: Default::default(),
                    hover_tracking: false,
                    zoom: 1.0,
                },
            )
        });
        let tabs = child(
            hwnd,
            "SysTabControl32",
            "",
            TABS,
            TCS_SINGLELINE | TCS_FIXEDWIDTH | TCS_FOCUSNEVER,
        );
        chrome::setup_tabs(tabs);
        for (index, name) in ["Projects", "Documents"].into_iter().enumerate() {
            let mut caption = wide(crate::locale::text(name));
            let item = TCITEMW {
                mask: TCIF_TEXT,
                pszText: caption.as_mut_ptr(),
                ..Default::default()
            };
            SendMessageW(tabs, TCM_INSERTITEMW, index, &item as *const _ as isize);
        }
        for (id, caption) in [
            (UP, "Parent folder"),
            (SEARCH_TOGGLE, "Search"),
            (PANEL_SETTINGS, "Settings"),
            (MOVE_PANEL, "Move"),
            (EMPTY_PANEL, "Clear"),
            (COLLAPSE, "Collapse / expand"),
            (HIDE_PANEL, "Close preview"),
        ] {
            button(hwnd, caption, id);
        }
        styled::edit(hwnd, "", SEARCH);
        SendMessageW(
            control(hwnd, SEARCH),
            EM_SETCUEBANNER,
            1,
            wide(crate::locale::text("Search…")).as_ptr() as isize,
        );
        child(hwnd, "STATIC", "Desktop / Projects", SOURCE, 0);
        child(hwnd, "STATIC", "", STATUS, 0);
        let list = create_list(hwnd, &[("Name", 250), ("Type", 110)]);
        // Keep native selection/scrolling, intercept operations before the app's subclass.
        SetWindowSubclass(list, Some(inert_list), 91, 0);
        SendMessageW(list, LVM_SETITEMCOUNT, 5, 0);
        hwnd
    }
}
fn sample_rows(parent: bool) -> Vec<Row> {
    let mut rows = Vec::new();
    if parent {
        rows.push(sample("..", true, true));
    }
    rows.extend([
        sample("Projects", true, false),
        sample("Documents", true, false),
        sample("Notes.txt", false, false),
        sample("Presentation.pdf", false, false),
        sample("Ideas.txt", false, false),
    ]);
    rows
}
fn sample(name: &str, directory: bool, parent_navigation: bool) -> Row {
    Row {
        path: PathBuf::from(format!("desktopplus-preview://{name}")),
        name: crate::locale::text(name).encode_utf16().collect(),
        kind: if directory { "Folder" } else { "File" }
            .encode_utf16()
            .collect(),
        directory,
        size: 4096,
        modified: 0,
        created: 0,
        hidden: false,
        parent_navigation,
    }
}

pub(super) fn update(hwnd: HWND, theme: &Theme, panel: &Panel, mode: usize, desktop: bool) {
    if hwnd.is_null() || unsafe { IsWindow(hwnd) } == 0 {
        return;
    }
    let mut preview_panel = panel.clone();
    preview_panel.header_style = Some(theme.header_style);
    preview_panel.appearance_overrides.header_alignment = Some(theme.header_alignment);
    let options = panel_options::Options::resolved(&preview_panel, theme);
    let rows = Arc::new(sample_rows(options.parent == "item"));
    let count = rows.len();
    VIEWS.with(|views| {
        if let Some(view) = views.borrow_mut().get_mut(&(hwnd as usize)) {
            view.details = mode != 3;
            view.options = options;
            view.rows = rows;
            view.collapsed = mode == 2;
            view.tab_count = if mode == 1 { 2 } else { 1 };
        }
    });
    PANEL_PALETTES.with(|palettes| {
        palettes
            .borrow_mut()
            .insert(hwnd as usize, Palette::new(theme))
    });
    set_text(hwnd, &crate::locale::text(&panel.panel_title));
    set_text(
        control(hwnd, STATUS),
        &format!("{count} {}", crate::locale::text("sample items")),
    );
    chrome::restyle(hwnd);
    motion::reset_colors(hwnd);
    motion::reset_colors(control(hwnd, LIST));
    theme_list(control(hwnd, LIST));
    unsafe {
        SendMessageW(
            control(hwnd, LIST),
            LVM_SETVIEW,
            if mode == 3 {
                LV_VIEW_ICON
            } else {
                LV_VIEW_DETAILS
            } as usize,
            0,
        );
    }
    images::configure(hwnd);
    load_sample_icons(hwnd);
    unsafe {
        SendMessageW(control(hwnd, LIST), LVM_SETITEMCOUNT, count, 0);
        SendMessageW(
            control(hwnd, LIST),
            LVM_SETITEMSTATE,
            2.min(count - 1),
            &LVITEMW {
                stateMask: LVIS_SELECTED,
                state: LVIS_SELECTED,
                ..std::mem::zeroed()
            } as *const _ as isize,
        );
    }
    if desktop {
        let mut bounds = RECT::default();
        unsafe {
            GetWindowRect(hwnd, &mut bounds);
            SetWindowPos(
                hwnd,
                null_mut(),
                0,
                0,
                bounds.right - bounds.left,
                chrome::scale(
                    hwnd,
                    if mode == 2 {
                        theme.header_style.height(true)
                    } else {
                        360
                    },
                ),
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
            );
        }
        glass::apply(hwnd, theme);
    } else {
        unsafe {
            SetPropW(
                hwnd,
                wide("DPPreviewRounded").as_ptr(),
                usize::from(theme.rounded_corners) as _,
            );
        }
    }
    layout(hwnd);
    unsafe {
        RedrawWindow(hwnd, null(), null_mut(), RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
}

// Stock icons keep the sample independent of the user's folders and image worker.
fn load_sample_icons(hwnd: HWND) {
    let rows = VIEWS.with(|views| views.borrow().get(&(hwnd as usize)).unwrap().rows.clone());
    let spec = images::spec(hwnd);
    for row in rows.iter() {
        if images::cached(hwnd, row) != 0 {
            continue;
        }
        unsafe {
            let mut info = SHSTOCKICONINFO {
                cbSize: std::mem::size_of::<SHSTOCKICONINFO>() as u32,
                ..std::mem::zeroed()
            };
            if SHGetStockIconInfo(
                if row.directory {
                    SIID_FOLDER
                } else {
                    SIID_DOCNOASSOC
                },
                SHGSI_ICON | SHGSI_SMALLICON,
                &mut info,
            ) < 0
            {
                continue;
            }
            let dc = CreateCompatibleDC(null_mut());
            let mut pixels = null_mut();
            let bitmap = CreateDIBSection(
                dc,
                &BITMAPINFO {
                    bmiHeader: BITMAPINFOHEADER {
                        biSize: 40,
                        biWidth: spec.edge,
                        biHeight: -spec.height,
                        biPlanes: 1,
                        biBitCount: 32,
                        biCompression: BI_RGB,
                        ..Default::default()
                    },
                    ..Default::default()
                },
                DIB_RGB_COLORS,
                &mut pixels,
                null_mut(),
                0,
            );
            if !bitmap.is_null() {
                let old = SelectObject(dc, bitmap);
                DrawIconEx(
                    dc,
                    0,
                    (spec.height - spec.edge) / 2,
                    info.hIcon,
                    spec.edge,
                    spec.edge,
                    0,
                    null_mut(),
                    DI_NORMAL,
                );
                SelectObject(dc, old);
                images::insert(
                    spec,
                    row.path.clone(),
                    0,
                    Some(shell::Bitmap(bitmap as isize)),
                    false,
                );
            }
            DestroyIcon(info.hIcon);
            DeleteDC(dc);
        }
    }
}
pub(super) fn layout(hwnd: HWND) {
    if hwnd.is_null() {
        return;
    }
    let (width, height) = dimensions(hwnd);
    if unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) as u32 & WS_CHILD } != 0 {
        unsafe {
            let rounded = !GetPropW(hwnd, wide("DPPreviewRounded").as_ptr()).is_null();
            let region = if rounded {
                CreateRoundRectRgn(
                    0,
                    0,
                    chrome::scale(hwnd, width) + 1,
                    chrome::scale(hwnd, height) + 1,
                    chrome::scale(hwnd, 18),
                    chrome::scale(hwnd, 18),
                )
            } else {
                CreateRectRgn(
                    0,
                    0,
                    chrome::scale(hwnd, width),
                    chrome::scale(hwnd, height),
                )
            };
            if SetWindowRgn(hwnd, region, 0) == 0 {
                DeleteObject(region);
            }
        }
    }
    layout_panel_header(hwnd);
    let collapsed = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .is_some_and(|v| v.collapsed)
    });
    show_control(hwnd, LIST, !collapsed);
    show_control(hwnd, STATUS, !collapsed);
    let top = chrome::header_height(hwnd) + 8;
    icon_grid::layout(hwnd, 8, top, width - 16, (height - top - 30).max(1));
    move_control(hwnd, STATUS, 14, height - 25, width - 28, 20);
    unsafe {
        SendMessageW(
            control(hwnd, LIST),
            LVM_SETCOLUMNWIDTH,
            0,
            chrome::scale(hwnd, (width - 126).max(70)) as isize,
        );
        SendMessageW(
            control(hwnd, LIST),
            LVM_SETCOLUMNWIDTH,
            1,
            chrome::scale(hwnd, 78) as isize,
        );
    }
}
unsafe extern "system" fn inert_list(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if matches!(
        message,
        WM_CONTEXTMENU | WM_RBUTTONDOWN | WM_RBUTTONUP | WM_LBUTTONDBLCLK | WM_KEYDOWN | WM_CHAR
    ) {
        return 0;
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}
unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    match message {
        WM_CLOSE => {
            unsafe {
                ShowWindow(hwnd, SW_HIDE);
            }
            return 0;
        }
        WM_NCCALCSIZE => return 0,
        WM_NCHITTEST if unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) as u32 & WS_POPUP } != 0 => {
            return chrome::hit_test(hwnd, l);
        }
        WM_SIZE => {
            layout(hwnd);
            return 0;
        }
        WM_GETMINMAXINFO => {
            unsafe {
                (*(l as *mut MINMAXINFO)).ptMinTrackSize = POINT {
                    x: chrome::scale(hwnd, 220),
                    y: chrome::scale(hwnd, chrome::header_height(hwnd)),
                };
            }
            return 0;
        }
        WM_DPICHANGED => unsafe {
            let r = &*(l as *const RECT);
            SetWindowPos(
                hwnd,
                null_mut(),
                r.left,
                r.top,
                r.right - r.left,
                r.bottom - r.top,
                SWP_NOZORDER | SWP_NOACTIVATE,
            );
            chrome::restyle(hwnd);
            images::configure(hwnd);
            load_sample_icons(hwnd);
            layout(hwnd);
            return 0;
        },
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::frame(hwnd, (message == WM_PRINTCLIENT).then_some(w as HDC));
            return 0;
        }
        WM_ERASEBKGND => return 1,
        WM_COMMAND if w as u16 == HIDE_PANEL => {
            if unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) as u32 & WS_POPUP } != 0 {
                unsafe {
                    ShowWindow(hwnd, SW_HIDE);
                }
            }
            return 0;
        }
        WM_COMMAND => return 0,
        WM_NOTIFY => {
            let header = unsafe { &*(l as *const NMHDR) };
            if header.code == NM_CUSTOMDRAW && header.idFrom == LIST as usize {
                return chrome::custom_list(hwnd, unsafe { &mut *(l as *mut NMLVCUSTOMDRAW) });
            }
            if header.code == LVN_GETDISPINFOW {
                display_info(hwnd, l as *mut _);
            }
            if header.code == TCN_SELCHANGE {
                unsafe {
                    InvalidateRect(control(hwnd, TABS), null(), 0);
                }
            }
            return 0;
        }
        WM_NCDESTROY => {
            glass::release(hwnd);
            icon_grid::forget(hwnd);
            chrome::release(hwnd);
            VIEWS.with(|views| views.borrow_mut().remove(&(hwnd as usize)));
            PANEL_PALETTES.with(|p| p.borrow_mut().remove(&(hwnd as usize)));
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT | WM_DRAWITEM => {
            return unsafe { window_proc(hwnd, message, w, l) };
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
