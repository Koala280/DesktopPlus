//! Opt-in, offscreen render of the changed header only; no application or user settings.
use super::*;

#[test]
#[ignore = "Render only the five panel headers to local review images"]
fn render_header_styles() {
    let destination = std::env::var_os("DESKTOPPLUS_HEADER_PREVIEW")
        .map(PathBuf::from)
        .expect("Set DESKTOPPLUS_HEADER_PREVIEW to the output directory");
    std::fs::create_dir_all(&destination).unwrap();
    unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_TAB_CLASSES | ICC_STANDARD_CLASSES,
        });
        PALETTE.with(|palette| {
            *palette.borrow_mut() = Palette::new(&Theme {
                background_color: "#202326".into(),
                header_color: "#262A2E".into(),
                accent_color: "#95BAFF".into(),
                ..Theme::default()
            })
        });
        for (index, style) in HeaderStyle::ALL.into_iter().enumerate() {
            let hwnd = CreateWindowExW(
                0,
                wide("STATIC").as_ptr(),
                wide("FL Projects").as_ptr(),
                WS_POPUP,
                0,
                0,
                744,
                120,
                null_mut(),
                null_mut(),
                GetModuleHandleW(null()),
                null(),
            );
            assert!(!hwnd.is_null());
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 2);
            let mut view = paint_tests::header_view(style);
            view.options.close = false;
            view.options.collapse = "doubleclick".into();
            VIEWS.with(|views| views.borrow_mut().insert(hwnd as usize, view));
            let width = scale(hwnd, 744);
            let height = scale(hwnd, 120);
            SetWindowPos(
                hwnd,
                null_mut(),
                0,
                0,
                width,
                height,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
            );
            let tabs = child(
                hwnd,
                "SysTabControl32",
                "",
                TABS,
                TCS_SINGLELINE | TCS_FIXEDWIDTH | TCS_FOCUSNEVER,
            );
            setup_tabs(tabs);
            for (i, name) in ["Music", "FL Projects"].into_iter().enumerate() {
                let mut caption = wide(name);
                let item = TCITEMW {
                    mask: TCIF_TEXT,
                    pszText: caption.as_mut_ptr(),
                    ..Default::default()
                };
                SendMessageW(tabs, TCM_INSERTITEMW, i, &item as *const _ as isize);
            }
            SendMessageW(tabs, TCM_SETCURSEL, 1, 0);
            for (id, caption) in [
                (UP, "Back"),
                (SEARCH_TOGGLE, "Search"),
                (PANEL_SETTINGS, "Settings"),
            ] {
                button(hwnd, caption, id);
            }
            child(hwnd, "STATIC", "Music / FL Projects", SOURCE, 0);
            super::super::layout_panel_header(hwnd);
            for single in [false, true] {
                VIEWS.with(|views| {
                    views
                        .borrow_mut()
                        .get_mut(&(hwnd as usize))
                        .unwrap()
                        .tab_count = if single { 1 } else { 2 }
                });
                let dc = CreateCompatibleDC(null_mut());
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
                let bitmap =
                    CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut pixels, null_mut(), 0);
                assert!(!bitmap.is_null());
                let old = SelectObject(dc, bitmap);
                frame(hwnd, Some(dc));
                let header = header_layout(hwnd, 744);
                if !single {
                    let saved = SaveDC(dc);
                    SetViewportOrgEx(
                        dc,
                        scale(hwnd, header.identity_left),
                        scale(hwnd, header.identity_top),
                        null_mut(),
                    );
                    draw_tabs(tabs, Some(dc));
                    RestoreDC(dc, saved);
                }
                for (id, x) in [
                    (UP, header.up),
                    (SEARCH_TOGGLE, header.search_button),
                    (PANEL_SETTINGS, header.settings),
                ] {
                    if let Some(x) = x {
                        let saved = SaveDC(dc);
                        SetViewportOrgEx(
                            dc,
                            scale(hwnd, x),
                            scale(hwnd, header.button_top),
                            null_mut(),
                        );
                        draw_button(&DRAWITEMSTRUCT {
                            CtlType: ODT_BUTTON,
                            CtlID: id as u32,
                            hwndItem: control(hwnd, id as i32),
                            hDC: dc,
                            rcItem: RECT {
                                left: 0,
                                top: 0,
                                right: scale(hwnd, 26),
                                bottom: scale(hwnd, 28),
                            },
                            ..Default::default()
                        });
                        RestoreDC(dc, saved);
                    }
                }
                GdiFlush();
                let count = (width * height * 4) as usize;
                let mut bytes = Vec::with_capacity(54 + count);
                bytes.extend_from_slice(b"BM");
                bytes.extend_from_slice(&((54 + count) as u32).to_le_bytes());
                bytes.extend_from_slice(&[0; 4]);
                bytes.extend_from_slice(&54u32.to_le_bytes());
                bytes.extend_from_slice(std::slice::from_raw_parts(
                    (&info.bmiHeader as *const BITMAPINFOHEADER).cast::<u8>(),
                    40,
                ));
                bytes.extend_from_slice(std::slice::from_raw_parts(pixels.cast::<u8>(), count));
                std::fs::write(
                    destination.join(format!(
                        "{}-{}.bmp",
                        index + 1,
                        if single { "title" } else { "tabs" }
                    )),
                    bytes,
                )
                .unwrap();
                SelectObject(dc, old);
                DeleteObject(bitmap);
                DeleteDC(dc);
            }
            release(hwnd);
            DestroyWindow(hwnd);
            VIEWS.with(|views| views.borrow_mut().remove(&(hwnd as usize)));
        }
    }
}
