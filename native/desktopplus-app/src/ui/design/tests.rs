use super::*;

struct Studio(HWND);
impl Studio {
    fn new() -> Self {
        let mut settings = Settings::default();
        settings
            .windows
            .push(crate::model::Panel::folder(&std::env::temp_dir()));
        Self(create(null_mut(), &settings, None, false).unwrap())
    }
    fn field(&self, key: &str) -> HWND {
        let data = unsafe { &*state(self.0) };
        let i = (0..data.draft.values.len())
            .find(|i| data.draft.editor.key(*i) == key)
            .unwrap();
        control(control(self.0, BODY), 1000 + i as i32)
    }
}
impl Drop for Studio {
    fn drop(&mut self) {
        unsafe {
            DestroyWindow(self.0);
        }
    }
}

#[test]
fn native_fields_preview_save_and_inheritance_use_the_same_values() {
    let studio = Studio::new();
    set_text(studio.field("background"), "#123456");
    set_text(studio.field("opacity"), "45");
    update_preview(studio.0);
    assert_eq!(
        with_palette(control(studio.0, PREVIEW), |p| p.background),
        color("#123456", 0)
    );
    assert_ne!(
        unsafe {
            &(*state(studio.0))
                .draft
                .settings
                .appearance
                .background_color
        },
        "#123456"
    );
    unsafe {
        SendMessageW(control(studio.0, SCOPE), CB_SETCURSEL, 1, 0);
        command(studio.0, SCOPE as usize | ((CBN_SELCHANGE as usize) << 16));
    }
    assert_eq!(text(studio.field("background")), "");
    assert_eq!(
        with_palette(control(studio.0, PREVIEW), |p| p.background),
        color("#123456", 0)
    );
    set_text(studio.field("accent"), "#EFABCD");
    let size_id = unsafe { GetDlgCtrlID(studio.field("item_size")) };
    unsafe {
        let slider = control(control(studio.0, BODY), size_id + 3000);
        SendMessageW(slider, TBM_SETPOS, 1, 37);
        SendMessageW(
            control(studio.0, BODY),
            WM_HSCROLL,
            TB_THUMBTRACK as usize,
            slider as isize,
        );
    }
    assert_eq!(text(studio.field("item_size")), "18.5");
    update_preview(studio.0);
    assert_eq!(with_palette(control(studio.0, PREVIEW), |p| p.size), 18.5);
    let result = saved(studio.0).unwrap();
    let theme = result.panel_theme(&result.windows[0]);
    assert_eq!(theme.background_color, "#123456");
    assert_eq!(theme.accent_color, "#EFABCD");
    assert_eq!(theme.glass_opacity, 45);
    assert_eq!(theme.item_font_size, 18.5);
    assert!(
        result.windows[0]
            .appearance_overrides
            .background_color
            .is_none()
    );
    unsafe {
        SendMessageW(control(studio.0, PREVIEW_MODE), CB_SETCURSEL, 3, 0);
        command(
            studio.0,
            PREVIEW_MODE as usize | ((CBN_SELCHANGE as usize) << 16),
        );
        assert_eq!(
            SendMessageW(control(control(studio.0, PREVIEW), LIST), LVM_GETVIEW, 0, 0),
            LV_VIEW_ICON as isize
        );
    }
    set_text(studio.field("accent"), "#GGGGGG");
    update_preview(studio.0);
    assert_eq!(unsafe { IsWindowEnabled(control(studio.0, IDOK)) }, 0);
    assert_eq!(
        with_palette(control(studio.0, PREVIEW), |p| p.accent),
        color("#EFABCD", 0)
    );
    assert!(saved(studio.0).is_err());
}

#[test]
#[ignore = "Focused visual review of the design studio only"]
fn render_design_studio() {
    let destination = PathBuf::from(
        std::env::var_os("DESKTOPPLUS_DESIGN_PREVIEW").expect("Set DESKTOPPLUS_DESIGN_PREVIEW"),
    );
    std::fs::create_dir_all(&destination).unwrap();
    crate::locale::configure("de", &destination);
    unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
    let studio = Studio::new();
    unsafe {
        ShowWindow(studio.0, SW_SHOWNOACTIVATE);
    }
    for (page, name) in [
        (2, "colors"),
        (3, "typography"),
        (4, "buttons"),
        (1, "glass"),
    ] {
        command(studio.0, 700 + page);
        capture(studio.0, &destination.join(format!("{name}.bmp")));
    }
    unsafe {
        SendMessageW(control(studio.0, PREVIEW_MODE), CB_SETCURSEL, 3, 0);
        command(
            studio.0,
            PREVIEW_MODE as usize | ((CBN_SELCHANGE as usize) << 16),
        );
    }
    capture(studio.0, &destination.join("icons.bmp"));
    unsafe {
        SendMessageW(control(studio.0, PREVIEW_MODE), CB_SETCURSEL, 0, 0);
    }
    command(
        studio.0,
        PREVIEW_MODE as usize | ((CBN_SELCHANGE as usize) << 16),
    );
    unsafe {
        SendMessageW(control(studio.0, SCOPE), CB_SETCURSEL, 1, 0);
        command(studio.0, SCOPE as usize | ((CBN_SELCHANGE as usize) << 16));
    }
    command(studio.0, 702);
    capture(studio.0, &destination.join("panel.bmp"));
    unsafe {
        SetWindowPos(
            studio.0,
            null_mut(),
            0,
            0,
            chrome::scale(studio.0, 780),
            chrome::scale(studio.0, 640),
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
        );
    }
    capture(studio.0, &destination.join("compact.bmp"));
}
fn capture(hwnd: HWND, path: &std::path::Path) {
    unsafe {
        RedrawWindow(
            hwnd,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW,
        );
        let mut bounds = RECT::default();
        GetClientRect(hwnd, &mut bounds);
        let width = bounds.right;
        let height = bounds.bottom;
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
        let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut pixels, null_mut(), 0);
        assert!(!bitmap.is_null());
        let old = SelectObject(dc, bitmap);
        let screen = GetDC(hwnd);
        BitBlt(dc, 0, 0, width, height, screen, 0, 0, SRCCOPY);
        ReleaseDC(hwnd, screen);
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
        std::fs::write(path, bytes).unwrap();
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}
