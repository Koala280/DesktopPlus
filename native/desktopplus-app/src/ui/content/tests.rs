use super::*;
use windows_sys::Win32::System::SystemServices::MK_LBUTTON;

struct Panel {
    hwnd: HWND,
    list: HWND,
}

impl Panel {
    fn new() -> Self {
        unsafe {
            InitCommonControlsEx(&INITCOMMONCONTROLSEX {
                dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
                dwICC: ICC_LISTVIEW_CLASSES,
            });
            let class = wide("DesktopPlus.ContentTest");
            let instance = GetModuleHandleW(null());
            RegisterClassW(&WNDCLASSW {
                lpfnWndProc: Some(window_proc),
                hInstance: instance,
                lpszClassName: class.as_ptr(),
                ..Default::default()
            });
            // The parent is never shown: exercise real virtual-list callbacks
            // and rendering without launching or interacting with the app.
            let hwnd = CreateWindowExW(
                0,
                class.as_ptr(),
                null(),
                WS_POPUP | WS_CLIPCHILDREN,
                0,
                0,
                740,
                480,
                null_mut(),
                null_mut(),
                instance,
                2usize as _,
            );
            assert!(!hwnd.is_null());
            VIEWS.with(|views| {
                views.borrow_mut().insert(
                    hwnd as usize,
                    ViewData {
                        source: None,
                        rows: Arc::default(),
                        main: Vec::new(),
                        is_main: false,
                        workspace_page: OVERVIEW,
                        photos: true,
                        details: false,
                        single_click: false,
                        columns: [false; 10],
                        collapsed: false,
                        tab_count: 2,
                        kind: "Folder".into(),
                        loaded: false,
                        searching: false,
                        search_open: false,
                        options: Default::default(),
                        hover_tracking: false,
                        zoom: 1.0,
                    },
                )
            });
            let list = create_list(
                hwnd,
                &crate::metadata::COLUMNS
                    .iter()
                    .map(|(_, caption, width)| (*caption, *width))
                    .collect::<Vec<_>>(),
            );
            let panel = Self { hwnd, list };
            panel.mode("photos");
            MoveWindow(list, 0, 0, 720, 400, 0);
            panel
        }
    }

    fn mode(&self, mode: &str) {
        VIEWS.with(|views| {
            let mut views = views.borrow_mut();
            let view = views.get_mut(&(self.hwnd as usize)).unwrap();
            view.photos = mode == "photos";
            view.details = mode == "details";
        });
        unsafe {
            SendMessageW(
                self.list,
                LVM_SETVIEW,
                if mode == "details" {
                    LV_VIEW_DETAILS
                } else {
                    LV_VIEW_ICON
                } as usize,
                0,
            );
        }
        images::configure(self.hwnd);
    }

    fn populate(&self, tab: &Tab) {
        prepare(self.hwnd, tab);
        let rows = (0..180)
            .map(|i| Row {
                path: PathBuf::from(format!("{}\\{i}.png", tab.folder_path)),
                name: format!("Photo {i}").encode_utf16().collect(),
                kind: Vec::new(),
                directory: false,
                size: 0,
                modified: 0,
                created: 0,
                hidden: false,
                parent_navigation: false,
            })
            .collect();
        VIEWS.with(|views| {
            let mut views = views.borrow_mut();
            let view = views.get_mut(&(self.hwnd as usize)).unwrap();
            view.rows = Arc::new(rows);
            view.loaded = true;
        });
        unsafe {
            SendMessageW(self.list, LVM_SETITEMCOUNT, 180, 0);
            let item = LVITEMW {
                stateMask: LVIS_SELECTED | LVIS_FOCUSED,
                state: LVIS_SELECTED | LVIS_FOCUSED,
                ..std::mem::zeroed()
            };
            SendMessageW(self.list, LVM_SETITEMSTATE, 3, &item as *const _ as isize);
            SendMessageW(self.list, LVM_ENSUREVISIBLE, 90, 0);
            SetPropW(self.list, wide("DPRowHot").as_ptr(), 4usize as _);
        }
    }

    fn count(&self) -> isize {
        unsafe { SendMessageW(self.list, LVM_GETITEMCOUNT, 0, 0) }
    }
}

impl Drop for Panel {
    fn drop(&mut self) {
        unsafe {
            DestroyWindow(self.hwnd);
        }
        chrome::release(self.hwnd);
        EVENTS.with(|events| events.borrow_mut().clear());
    }
}

impl Panel {
    fn details_columns(&self) {
        self.mode("details");
        VIEWS.with(|views| {
            views
                .borrow_mut()
                .get_mut(&(self.hwnd as usize))
                .unwrap()
                .columns = [
                true, false, true, true, true, false, false, false, false, false,
            ];
        });
        for i in 0..10 {
            set_column_width(
                self.list,
                i,
                if i == 0 { 240 } else { 116 },
                matches!(i, 0 | 2 | 3 | 4),
            );
        }
        chrome::set_sort(self.hwnd, 0, false);
    }
}

#[test]
fn details_columns_resize_independently_and_keep_their_width_when_the_panel_resizes() {
    let panel = Panel::new();
    panel.details_columns();
    panel.populate(&Tab {
        view_mode: "details".into(),
        ..Tab::default()
    });
    unsafe {
        let widths = || {
            (0..10)
                .map(|i| SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, i, 0))
                .collect::<Vec<_>>()
        };
        for changed in [0, 2, 3, 4] {
            for desired in [96, 420, 860, 116] {
                let mut expected = widths();
                expected[changed] = desired;
                SendMessageW(panel.list, LVM_SETCOLUMNWIDTH, changed, desired);
                assert_eq!(widths(), expected, "only the dragged column may change");
                for width in [960, 420, 740, 960] {
                    MoveWindow(panel.list, 0, 0, width, 400, 0);
                    assert_eq!(widths(), expected, "panel resize must preserve every width");
                    assert_eq!(selected(panel.hwnd), vec![3]);
                }
            }
        }
        let header = SendMessageW(panel.list, LVM_GETHEADER, 0, 0) as HWND;
        for column in [0, 2, 3, 4] {
            let before = widths();
            let notification = NMHEADERW {
                hdr: NMHDR {
                    hwndFrom: header,
                    idFrom: 0,
                    code: HDN_DIVIDERDBLCLICKW,
                },
                iItem: column as i32,
                ..std::mem::zeroed()
            };
            SendMessageW(panel.list, WM_NOTIFY, 0, &notification as *const _ as isize);
            let after = widths();
            assert!(after[column] > 0);
            for i in 0..10 {
                if i != column {
                    assert_eq!(after[i], before[i]);
                }
            }
            MoveWindow(panel.list, 0, 0, 420, 400, 0);
            assert_eq!(widths(), after, "explicit autosizing must remain stable");
        }
        chrome::set_sort(panel.hwnd, 3, true);
        for column in 0..10 {
            let mut item = HDITEMW {
                mask: HDI_FORMAT,
                ..std::mem::zeroed()
            };
            SendMessageW(header, HDM_GETITEMW, column, &mut item as *mut _ as isize);
            assert_eq!(
                item.fmt & (HDF_SORTUP | HDF_SORTDOWN),
                if column == 3 { HDF_SORTDOWN } else { 0 }
            );
        }
    }
}

#[test]
fn details_buffer_clears_trailing_space_and_preserves_native_row_geometry() {
    let panel = Panel::new();
    panel.details_columns();
    let tab = Tab {
        view_mode: "details".into(),
        ..Tab::default()
    };
    panel.populate(&tab);
    VIEWS.with(|views| {
        let mut views = views.borrow_mut();
        let view = views.get_mut(&(panel.hwnd as usize)).unwrap();
        let rows = Arc::make_mut(&mut view.rows);
        rows.truncate(7);
        for (index, (row, name)) in rows
            .iter_mut()
            .zip([
                "Comedy-Shorts-Analyse-2026",
                "Instrument-Referenz",
                "MediaHuman",
                "Tails-7.16",
                "THE LIFE",
                "1031 (7) (1).mp4",
                "Projektunterlagen November.pdf",
            ])
            .enumerate()
        {
            row.name = name.encode_utf16().collect();
            row.directory = index < 5;
            row.size = 351_902_720;
            row.created = 134_300_000_000_000_000;
            row.modified = row.created;
            row.path = PathBuf::from(if row.directory {
                "C:\\Windows"
            } else {
                "C:\\Windows\\explorer.exe"
            });
        }
    });
    unsafe {
        SendMessageW(panel.list, LVM_SETITEMCOUNT, 7, 0);
        SendMessageW(panel.list, LVM_ENSUREVISIBLE, 0, 0);
        assert_eq!(
            SendMessageW(panel.list, LVM_GETTOPINDEX, 0, 0),
            0,
            "short results must reset the old scroll offset"
        );
        let initialized = windows_sys::Win32::System::Com::CoInitializeEx(
            null(),
            windows_sys::Win32::System::Com::COINIT_APARTMENTTHREADED as u32,
        ) >= 0;
        let rows = VIEWS.with(|views| views.borrow()[&(panel.hwnd as usize)].rows.clone());
        for row in rows.iter() {
            let spec = images::spec(panel.hwnd);
            images::insert(
                spec,
                row.path.clone(),
                row.modified,
                shell::image(&row.path, spec),
                true,
            );
        }
        if initialized {
            windows_sys::Win32::System::Com::CoUninitialize();
        }
        let dc = CreateCompatibleDC(null_mut());
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: 40,
                biWidth: 720,
                biHeight: -400,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut data = null_mut();
        let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut data, null_mut(), 0);
        assert!(!bitmap.is_null());
        let old = SelectObject(dc, bitmap);
        let pixels = std::slice::from_raw_parts_mut(data as *mut u32, 720 * 400);
        let mut row = RECT {
            left: LVIR_BOUNDS as i32,
            ..Default::default()
        };
        SendMessageW(panel.list, LVM_GETITEMRECT, 0, &mut row as *mut _ as isize);
        assert_eq!(row.top, chrome::content_scale(panel.hwnd, 38));
        assert!(row.bottom - row.top >= chrome::content_scale(panel.hwnd, 36));
        for glass_mode in [
            crate::model::GlassMode::Solid,
            crate::model::GlassMode::Clear,
        ] {
            glass::apply(
                panel.hwnd,
                &Theme {
                    glass_mode,
                    glass_opacity: 72,
                    ..Theme::default()
                },
            );
            pixels.fill(0xffff00ff);
            SendMessageW(panel.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
            GdiFlush();
            let mut client = RECT::default();
            GetClientRect(panel.list, &mut client);
            let x = client.right as usize - 1;
            let y = ((row.top + row.bottom) / 2) as usize;
            assert_ne!(
                pixels[y * 720 + x],
                0xffff00ff,
                "right edge must be painted"
            );
            assert_eq!(
                pixels[y * 720 + x],
                pixels[370 * 720 + x],
                "rows and trailing space must have identical surface opacity"
            );
            assert_eq!(
                pixels[370 * 720 + x],
                pixels[370 * 720 + 4],
                "no native separator may remain below the rows"
            );
        }
        glass::release(panel.hwnd);
        chrome::fill(
            dc,
            &RECT {
                right: 720,
                bottom: 400,
                ..Default::default()
            },
            with_palette(panel.hwnd, |p| p.background),
        );
        SendMessageW(panel.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        let header = SendMessageW(panel.list, LVM_GETHEADER, 0, 0) as HWND;
        SendMessageW(header, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        GdiFlush();
        if let Some(path) = std::env::var_os("DESKTOPPLUS_DETAILS_CAPTURE") {
            let mut bytes = Vec::new();
            bytes.extend_from_slice(b"BM");
            bytes.extend_from_slice(&(54u32 + 720 * 400 * 4).to_le_bytes());
            bytes.extend_from_slice(&0u32.to_le_bytes());
            bytes.extend_from_slice(&54u32.to_le_bytes());
            bytes.extend_from_slice(std::slice::from_raw_parts(
                (&info.bmiHeader as *const BITMAPINFOHEADER).cast::<u8>(),
                40,
            ));
            bytes.extend_from_slice(std::slice::from_raw_parts(data as *const u8, 720 * 400 * 4));
            std::fs::write(path, bytes).unwrap();
        }
        // Column reordering and horizontal scrolling must retain the native
        // hit target even though the entire row is now painted together.
        let order = [0, 4, 3, 2, 1, 5, 6, 7, 8, 9];
        SendMessageW(
            panel.list,
            LVM_SETCOLUMNORDERARRAY,
            order.len(),
            order.as_ptr() as isize,
        );
        SendMessageW(panel.list, LVM_SCROLL, 120, 0);
        let mut hit = LVHITTESTINFO {
            pt: POINT {
                x: 40,
                y: (row.top + row.bottom) / 2,
            },
            ..std::mem::zeroed()
        };
        assert_eq!(
            SendMessageW(panel.list, LVM_HITTEST, 0, &mut hit as *mut _ as isize),
            0
        );
        SendMessageW(panel.list, WM_KEYDOWN, VK_END as usize, 0);
        assert_eq!(
            SendMessageW(
                panel.list,
                LVM_GETNEXTITEM,
                usize::MAX,
                LVNI_FOCUSED as isize
            ),
            6
        );
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}

#[test]
fn details_scrollbars_and_corner_share_the_panel_material_and_stay_inside_the_control() {
    let panel = Panel::new();
    panel.details_columns();
    panel.populate(&Tab {
        view_mode: "details".into(),
        ..Tab::default()
    });
    unsafe {
        SendMessageW(panel.list, LVM_SETCOLUMNWIDTH, 0, 560);
        let dc = CreateCompatibleDC(null_mut());
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: 40,
                biWidth: 760,
                biHeight: -440,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut data = null_mut();
        let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut data, null_mut(), 0);
        assert!(!bitmap.is_null());
        let old = SelectObject(dc, bitmap);
        let pixels = std::slice::from_raw_parts_mut(data as *mut u32, 760 * 440);
        for mode in [
            crate::model::GlassMode::Solid,
            crate::model::GlassMode::Clear,
        ] {
            glass::apply(
                panel.hwnd,
                &Theme {
                    glass_mode: mode,
                    glass_opacity: 72,
                    ..Theme::default()
                },
            );
            for offset in [0, 120, 2000, -2000] {
                let top = SendMessageW(panel.list, LVM_GETTOPINDEX, 0, 0);
                SendMessageW(panel.list, LVM_SCROLL, offset as usize, 0);
                assert_eq!(
                    SendMessageW(panel.list, LVM_GETTOPINDEX, 0, 0),
                    top,
                    "horizontal scrolling must not move rows vertically"
                );
                let mut scroll = SCROLLINFO {
                    cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
                    fMask: SIF_ALL,
                    ..std::mem::zeroed()
                };
                GetScrollInfo(panel.list, SB_HORZ, &mut scroll);
                if offset == 120 {
                    assert!(scroll.nPos > 0);
                }
                if offset == 2000 {
                    assert_eq!(scroll.nPos, scroll.nMax + 1 - scroll.nPage as i32);
                }
                if offset == -2000 {
                    assert_eq!(scroll.nPos, 0);
                }
                let header = SendMessageW(panel.list, LVM_GETHEADER, 0, 0) as HWND;
                let mut column = RECT::default();
                SendMessageW(header, HDM_GETITEMRECT, 0, &mut column as *mut _ as isize);
                MapWindowPoints(header, panel.list, (&mut column as *mut RECT).cast(), 2);
                assert_eq!(
                    column.left, -scroll.nPos,
                    "header and rows must share the native scroll offset"
                );
                let widths: Vec<_> = (0..10)
                    .map(|i| SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, i, 0))
                    .collect();
                assert_eq!(widths[0], 560);
                assert_eq!(widths[2..5], [116; 3]);
                pixels.fill(0xffff00ff);
                SendMessageW(
                    panel.list,
                    WM_PRINT,
                    dc as usize,
                    (PRF_CLIENT | PRF_NONCLIENT | PRF_CHILDREN) as isize,
                );
                GdiFlush();
                let mut client = RECT::default();
                GetClientRect(panel.list, &mut client);
                let mut window = RECT::default();
                GetWindowRect(panel.list, &mut window);
                let width = (window.right - window.left) as usize;
                let height = (window.bottom - window.top) as usize;
                assert!(
                    client.right < width as i32 && client.bottom < height as i32,
                    "both scrollbars must be present"
                );
                let background = with_palette(panel.hwnd, |p| p.background);
                let rgb =
                    ((background & 255) << 16) | (background & 0xff00) | ((background >> 16) & 255);
                let expected = glass::for_window(panel.list).map_or(rgb, |g| g.pixel(rgb));
                for (x, y) in [
                    (width - 1, height - 1),
                    (client.right as usize + 2, height - 1),
                    (2, height - 1),
                    (width - 1, 2),
                ] {
                    let pixel = pixels[y * 760 + x];
                    if mode == crate::model::GlassMode::Solid {
                        assert_eq!(
                            pixel & 0xffffff,
                            expected & 0xffffff,
                            "track background at {x},{y}"
                        );
                    } else {
                        assert_eq!(
                            pixel, expected,
                            "corner and tracks must retain the same glass alpha: {x},{y}"
                        );
                    }
                }
                for y in 0..height {
                    assert!(
                        pixels[y * 760 + width..(y + 1) * 760]
                            .iter()
                            .all(|p| *p == 0xffff00ff),
                        "headers, rows and scrollbars must stay inside the panel at y={y}"
                    );
                }
                assert!(
                    pixels[height * 760..].iter().all(|p| *p == 0xffff00ff),
                    "nothing may paint below the list"
                );
                if mode == crate::model::GlassMode::Solid && offset == 120 {
                    if let Some(path) = std::env::var_os("DESKTOPPLUS_SCROLLBAR_CAPTURE") {
                        let mut bytes = Vec::new();
                        bytes.extend_from_slice(b"BM");
                        bytes.extend_from_slice(&(54u32 + 760 * 440 * 4).to_le_bytes());
                        bytes.extend_from_slice(&0u32.to_le_bytes());
                        bytes.extend_from_slice(&54u32.to_le_bytes());
                        bytes.extend_from_slice(std::slice::from_raw_parts(
                            (&info.bmiHeader as *const BITMAPINFOHEADER).cast::<u8>(),
                            40,
                        ));
                        bytes.extend_from_slice(std::slice::from_raw_parts(
                            data as *const u8,
                            760 * 440 * 4,
                        ));
                        std::fs::write(path, bytes).unwrap();
                    }
                }
            }
        }
        glass::release(panel.hwnd);
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}

#[test]
fn details_row_hover_and_reveal_repaints_do_not_erase_column_headers() {
    let panel = Panel::new();
    panel.details_columns();
    panel.populate(&Tab {
        view_mode: "details".into(),
        ..Tab::default()
    });
    unsafe {
        SendMessageW(panel.list, LVM_ENSUREVISIBLE, 0, 0);
        let header = SendMessageW(panel.list, LVM_GETHEADER, 0, 0) as HWND;
        let dc = CreateCompatibleDC(null_mut());
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: 40,
                biWidth: 720,
                biHeight: -400,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut data = null_mut();
        let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut data, null_mut(), 0);
        assert!(!bitmap.is_null());
        let old = SelectObject(dc, bitmap);
        let pixels = std::slice::from_raw_parts_mut(data as *mut u32, 720 * 400);
        let height = chrome::content_scale(panel.hwnd, 38) as usize;
        // Exercise individual intermediate paints, not just a completed
        // WM_PRINT tree: the latter repaints the header last and hides flicker.
        for (index, point) in [(60 << 16) | 50, (140 << 16) | 50, (220 << 16) | 300]
            .into_iter()
            .enumerate()
        {
            MoveWindow(panel.list, 0, 0, 720, 160 + index as i32 * 120, 0);
            pixels.fill(0xffff00ff);
            SendMessageW(
                header,
                WM_MOUSEMOVE,
                0,
                (18 << 16) | (50 + index as isize * 240),
            );
            SendMessageW(header, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
            GdiFlush();
            let before = pixels[..height * 720].to_vec();
            assert!(
                before.iter().any(|p| *p != 0xffff00ff),
                "fixture must paint its header"
            );
            SendMessageW(panel.list, WM_MOUSEMOVE, 0, point);
            SendMessageW(panel.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
            GdiFlush();
            assert!(
                pixels[..height * 720] == before,
                "a list repaint erased the separately painted column header"
            );
            assert_ne!(
                pixels[80 * 720 + 12],
                0xffff00ff,
                "rows must still repaint below the header"
            );
            SendMessageW(panel.list, WM_MOUSELEAVE, 0, 0);
            SendMessageW(panel.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
            GdiFlush();
            assert!(
                pixels[..height * 720] == before,
                "hover exit must also leave captions intact"
            );
        }
        // A redraw transaction may update sort/column state synchronously.
        // Its header must preserve the last complete frame along with the rows.
        let update = Update::new(panel.hwnd);
        chrome::set_sort(panel.hwnd, 3, true);
        pixels.fill(0xff123456);
        SendMessageW(header, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        SendMessageW(panel.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        GdiFlush();
        assert!(pixels.iter().all(|p| *p == 0xff123456));
        drop(update);
        SendMessageW(header, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        GdiFlush();
        assert!(pixels[..height * 720].iter().any(|p| *p != 0xff123456));
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}

#[test]
fn empty_panel_status_stays_hidden_after_layout_and_messages_respect_collapse() {
    let panel = Panel::new();
    let status = child(panel.hwnd, "STATIC", "Loading…", STATUS, 0);
    let visible = || unsafe { GetWindowLongPtrW(status, GWL_STYLE) as u32 & WS_VISIBLE != 0 };
    let collapse = |collapsed| {
        VIEWS.with(|views| {
            views
                .borrow_mut()
                .get_mut(&(panel.hwnd as usize))
                .unwrap()
                .collapsed = collapsed;
        });
        layout(panel.hwnd);
    };
    layout(panel.hwnd);
    assert!(visible());

    set_text(status, "");
    assert!(!visible());
    layout(panel.hwnd);
    assert!(
        !visible(),
        "resizing must not restore the empty status strip"
    );
    collapse(true);
    collapse(false);
    assert!(!visible());

    for message in ["Loading…", "Folder unavailable", "125%"] {
        set_text(status, message);
        assert!(visible());
        assert_eq!(text(status), message);
        collapse(true);
        assert!(!visible());
        set_text(status, "Updating search…");
        assert!(!visible(), "updates must not reveal a collapsed footer");
        collapse(false);
        assert!(visible());
        set_text(status, "");
        assert!(!visible());
    }
}

#[test]
fn clicking_panel_background_or_header_controls_clears_selection() {
    let panel = Panel::new();
    let tab = Tab::default();
    unsafe {
        MoveWindow(panel.list, 0, 48, 720, 350, 0);
        let header = child(panel.hwnd, "BUTTON", "Tab", TABS, 0);
        MoveWindow(header, 120, 4, 180, 36, 0);
    }
    for mode in ["icons", "photos", "details"] {
        panel.mode(mode);
        for (message, w, l) in [
            (WM_LBUTTONDOWN, 0, (20 << 16) | 10),
            (WM_PARENTNOTIFY, WM_LBUTTONDOWN as usize, (20 << 16) | 150),
            (WM_PARENTNOTIFY, WM_RBUTTONDOWN as usize, (20 << 16) | 150),
        ] {
            panel.populate(&tab);
            unsafe { SendMessageW(panel.hwnd, message, w, l) };
            assert!(selected(panel.hwnd).is_empty(), "{mode}: {message}");
            unsafe {
                assert_eq!(
                    SendMessageW(
                        panel.list,
                        LVM_GETNEXTITEM,
                        usize::MAX,
                        LVNI_FOCUSED as isize
                    ),
                    -1
                );
                assert_eq!(SendMessageW(panel.list, LVM_GETSELECTIONMARK, 0, 0), -1);
                assert!(GetPropW(panel.list, wide("DPRowHot").as_ptr()).is_null());
            }
        }
    }
}

#[test]
fn panel_deactivation_clears_selection_but_item_clicks_and_menus_preserve_it() {
    let panel = Panel::new();
    panel.populate(&Tab::default());
    unsafe {
        let item = LVITEMW {
            stateMask: LVIS_SELECTED,
            state: LVIS_SELECTED,
            ..std::mem::zeroed()
        };
        SendMessageW(panel.list, LVM_SETITEMSTATE, 5, &item as *const _ as isize);
    }
    for (message, w, l) in [
        (WM_MOUSELEAVE, 0, 0),
        (WM_ACTIVATE, WA_ACTIVE as usize, 0),
        (WM_PARENTNOTIFY, WM_LBUTTONDOWN as usize, (100 << 16) | 100),
        (WM_PARENTNOTIFY, WM_RBUTTONDOWN as usize, (100 << 16) | 100),
        (WM_ENTERMENULOOP, 1, 0),
        (WM_EXITMENULOOP, 1, 0),
    ] {
        unsafe { SendMessageW(panel.hwnd, message, w, l) };
        assert_eq!(selected(panel.hwnd), vec![3, 5], "message {message}");
    }
    unsafe { SendMessageW(panel.hwnd, WM_ACTIVATE, WA_INACTIVE as usize, 0) };
    assert!(selected(panel.hwnd).is_empty());
}

#[test]
fn hidden_columns_cannot_be_resized_or_autosized_back_into_view() {
    let panel = Panel::new();
    panel.mode("details");
    VIEWS.with(|views| {
        views
            .borrow_mut()
            .get_mut(&(panel.hwnd as usize))
            .unwrap()
            .columns[0] = true;
    });
    unsafe {
        let header = SendMessageW(panel.list, LVM_GETHEADER, 0, 0) as HWND;
        for column in 1..crate::metadata::COLUMNS.len() {
            assert!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, column, 0) > 0);
            set_column_width(panel.list, column, 180, false);
            assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, column, 0), 0);
            for width in [
                180,
                LVSCW_AUTOSIZE as isize,
                LVSCW_AUTOSIZE_USEHEADER as isize,
            ] {
                SendMessageW(panel.list, LVM_SETCOLUMNWIDTH, column, width);
                assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, column, 0), 0);
            }
            let item = HDITEMW {
                mask: HDI_WIDTH,
                cxy: 160,
                ..std::mem::zeroed()
            };
            SendMessageW(header, HDM_SETITEMW, column, &item as *const _ as isize);
            assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, column, 0), 0);
            for code in [HDN_BEGINTRACKA, HDN_BEGINTRACKW] {
                let notification = NMHEADERW {
                    hdr: NMHDR {
                        hwndFrom: header,
                        idFrom: 0,
                        code,
                    },
                    iItem: column as i32,
                    ..std::mem::zeroed()
                };
                assert_eq!(
                    SendMessageW(panel.list, WM_NOTIFY, 0, &notification as *const _ as isize),
                    1
                );
            }
        }
        // Resizing a visible neighbour and later enabling Title must still work.
        SendMessageW(panel.list, LVM_SETCOLUMNWIDTH, 0, 360);
        assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, 0, 0), 360);
        assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, 9, 0), 0);
        VIEWS.with(|views| {
            views
                .borrow_mut()
                .get_mut(&(panel.hwnd as usize))
                .unwrap()
                .columns[9] = true;
        });
        set_column_width(panel.list, 9, 180, true);
        assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, 9, 0), 180);
        SendMessageW(panel.list, LVM_SETCOLUMNWIDTH, 9, 220);
        assert_eq!(SendMessageW(panel.list, LVM_GETCOLUMNWIDTH, 9, 0), 220);
    }
}

#[test]
fn header_right_click_opens_only_column_settings_and_preserves_sort_clicks() {
    let panel = Panel::new();
    panel.mode("details");
    unsafe {
        let header = SendMessageW(panel.list, LVM_GETHEADER, 0, 0) as HWND;
        for (message, point) in [(WM_RBUTTONUP, (12 << 16) | 20), (WM_CONTEXTMENU, -1)] {
            EVENTS.with(|events| events.borrow_mut().clear());
            SendMessageW(header, message, 0, point);
            EVENTS.with(|events| {
                let events = events.borrow();
                assert_eq!(events.len(), 1);
                assert!(matches!(events.front(), Some(Event::Command(hwnd, COLUMNS)) if *hwnd == panel.hwnd as usize));
            });
        }
        EVENTS.with(|events| events.borrow_mut().clear());
        SendMessageW(header, WM_LBUTTONDOWN, MK_LBUTTON as usize, (12 << 16) | 20);
        SendMessageW(header, WM_LBUTTONUP, 0, (12 << 16) | 20);
        EVENTS.with(|events| {
            let events = events.borrow();
            assert!(events.iter().any(
                |event| matches!(event, Event::Column(hwnd, 0) if *hwnd == panel.hwnd as usize)
            ));
            assert!(
                !events
                    .iter()
                    .any(|event| matches!(event, Event::Command(_, COLUMNS)))
            );
        });
    }
}

#[test]
fn switching_tabs_clears_old_images_selection_and_hover_before_reconfiguring() {
    let panel = Panel::new();
    let photos = Tab {
        folder_path: "C:\\photos".into(),
        view_mode: "photos".into(),
        ..Tab::default()
    };
    for mode in ["details", "icons", "photos"] {
        panel.mode("photos");
        panel.populate(&photos);
        assert_eq!(panel.count(), 180);
        assert_eq!(selected(panel.hwnd), vec![3]);
        let target = Tab {
            folder_path: "C:\\other".into(),
            view_mode: mode.into(),
            ..Tab::default()
        };
        let update = Update::new(panel.hwnd);
        assert!(prepare(panel.hwnd, &target));
        VIEWS.with(|views| {
            let views = views.borrow();
            let view = &views[&(panel.hwnd as usize)];
            assert!(view.rows.is_empty());
            assert!(!view.loaded);
            assert!(view.photos, "old rows are cleared before the view changes");
        });
        assert_eq!(panel.count(), 0);
        assert!(selected(panel.hwnd).is_empty());
        unsafe {
            assert!(GetPropW(panel.list, wide("DPRowHot").as_ptr()).is_null());
            assert_eq!(
                SendMessageW(
                    panel.list,
                    LVM_GETNEXTITEM,
                    usize::MAX,
                    LVNI_FOCUSED as isize
                ),
                -1
            );
        }
        EVENTS.with(|events| events.borrow_mut().clear());
        panel.mode(mode);
        // A synchronous callback during layout must never request the old
        // photo as an icon in the new tab's image pool.
        let mut info: NMLVDISPINFOW = unsafe { std::mem::zeroed() };
        info.item.mask = LVIF_IMAGE;
        display_info(panel.hwnd, &mut info);
        assert!(EVENTS.with(|events| {
            !events
                .borrow()
                .iter()
                .any(|event| matches!(event, Event::Image(..)))
        }));
        drop(update);
    }
}

#[test]
fn refresh_and_rename_keep_content_but_navigation_and_view_changes_clear_it() {
    let panel = Panel::new();
    let mut tab = Tab {
        folder_path: "C:\\photos".into(),
        view_mode: "photos".into(),
        ..Tab::default()
    };
    panel.populate(&tab);
    assert!(!prepare(panel.hwnd, &tab));
    tab.tab_name = "Renamed".into();
    assert!(!prepare(panel.hwnd, &tab));
    assert_eq!(panel.count(), 180);
    assert_eq!(selected(panel.hwnd), vec![3]);
    tab.folder_path = "C:\\photos\\subfolder".into();
    assert!(prepare(panel.hwnd, &tab));
    assert_eq!(panel.count(), 0);
    panel.populate(&tab);
    tab.view_mode = "details".into();
    assert!(prepare(panel.hwnd, &tab));
    assert_eq!(panel.count(), 0);
}

#[test]
fn nested_updates_and_collapsed_lists_preserve_redraw_visibility() {
    let panel = Panel::new();
    unsafe extern "system" fn redraw_probe(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
        _: usize,
        data: usize,
    ) -> LRESULT {
        let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
        if message == WM_SETREDRAW && w != 0 {
            // Native layout is flushed here, before SendMessageW returns.
            unsafe { *(data as *mut bool) = updating(hwnd) };
        }
        result
    }
    let mut guarded = false;
    unsafe {
        SetWindowSubclass(
            panel.list,
            Some(redraw_probe),
            100,
            &mut guarded as *mut bool as usize,
        );
    }
    let visible = || unsafe { GetWindowLongPtrW(panel.list, GWL_STYLE) as u32 & WS_VISIBLE != 0 };
    assert!(visible());
    let outer = Update::new(panel.hwnd);
    assert!(updating(panel.list));
    drop(Update::new(panel.hwnd));
    assert!(updating(panel.list));
    drop(outer);
    assert!(!updating(panel.list));
    assert!(
        guarded,
        "deferred native layout must finish before painting"
    );
    assert!(visible());
    unsafe {
        RemoveWindowSubclass(panel.list, Some(redraw_probe), 100);
        ShowWindow(panel.list, SW_HIDE);
    }
    drop(Update::new(panel.hwnd));
    assert!(!visible());
}

#[test]
fn tab_switch_paints_complete_frames_without_reinterpreting_old_photos() {
    let panel = Panel::new();
    let photos = Tab {
        folder_path: "C:\\photos".into(),
        view_mode: "photos".into(),
        ..Tab::default()
    };
    panel.populate(&photos);
    unsafe {
        SendMessageW(panel.list, LVM_ENSUREVISIBLE, 0, 0);
        let spec = images::spec(panel.hwnd);
        let pixels = vec![0xff00ff00u32; (spec.edge * spec.height) as usize];
        let image = CreateBitmap(spec.edge, spec.height, 1, 32, pixels.as_ptr().cast());
        images::insert(
            spec,
            PathBuf::from("C:\\photos\\0.png"),
            0,
            Some(shell::Bitmap(image as isize)),
            false,
        );

        let dc = CreateCompatibleDC(null_mut());
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: 720,
                biHeight: -400,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut data = null_mut();
        let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut data, null_mut(), 0);
        assert!(!bitmap.is_null());
        let previous = SelectObject(dc, bitmap);
        let frame = |name: &str| {
            SendMessageW(panel.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
            GdiFlush();
            let pixels = std::slice::from_raw_parts(data as *const u32, 720 * 400);
            if let Some(folder) = std::env::var_os("DESKTOPPLUS_TAB_CAPTURE") {
                let folder = PathBuf::from(folder);
                std::fs::create_dir_all(&folder).unwrap();
                let mut bytes = Vec::new();
                bytes.extend_from_slice(b"BM");
                bytes.extend_from_slice(&(54u32 + 720 * 400 * 4).to_le_bytes());
                bytes.extend_from_slice(&0u32.to_le_bytes());
                bytes.extend_from_slice(&54u32.to_le_bytes());
                bytes.extend_from_slice(std::slice::from_raw_parts(
                    (&info.bmiHeader as *const BITMAPINFOHEADER).cast::<u8>(),
                    40,
                ));
                bytes.extend_from_slice(std::slice::from_raw_parts(
                    data as *const u8,
                    720 * 400 * 4,
                ));
                std::fs::write(folder.join(format!("{name}.bmp")), bytes).unwrap();
            }
            pixels
                .iter()
                .filter(|pixel| **pixel & 0xffffff == 0x00ff00)
                .count()
        };
        let green = frame("before");
        assert!(green > 20_000, "fixture must contain the loaded photo");
        let native_frame = || {
            let before = std::slice::from_raw_parts(data as *const u32, 720 * 400).to_vec();
            // Common controls can bypass WM_PAINT and custom-draw directly
            // during native arrangement. That path must not overlay new rows
            // or the loading state on the last complete frame.
            for stage in [CDDS_PREPAINT, CDDS_ITEMPREPAINT, CDDS_POSTPAINT] {
                let mut draw: NMLVCUSTOMDRAW = std::mem::zeroed();
                draw.nmcd.hdc = dc;
                draw.nmcd.dwDrawStage = stage;
                draw.nmcd.rc = RECT {
                    right: 720,
                    bottom: 400,
                    ..Default::default()
                };
                assert_eq!(
                    chrome::custom_list(panel.hwnd, &mut draw),
                    CDRF_SKIPDEFAULT as isize
                );
                GdiFlush();
                assert_eq!(
                    std::slice::from_raw_parts(data as *const u32, 720 * 400),
                    before,
                    "native arrangement must leave the presented frame untouched"
                );
            }
        };
        let target = Tab {
            folder_path: "C:\\other".into(),
            view_mode: "icons".into(),
            ..Tab::default()
        };
        let update = Update::new(panel.hwnd);
        prepare(panel.hwnd, &target);
        panel.mode("icons");
        // Explicit synchronous paints must retain the previous complete frame
        // until both the backing rows and native geometry are ready.
        assert_eq!(frame("during"), green);
        native_frame();
        drop(update);
        native_frame();
        assert_eq!(
            frame("after"),
            0,
            "old photos must not shrink into new-tab icons"
        );
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}
