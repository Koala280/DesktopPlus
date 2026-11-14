use super::*;

#[test]
fn resizing_reuses_layout_and_updates_wrapping_and_alignment() {
    let mut engine = TextEngine::new().unwrap();
    unsafe {
        let screen = GetDC(null_mut());
        let dc = CreateCompatibleDC(screen);
        let bitmap = CreateCompatibleBitmap(screen, 640, 128);
        ReleaseDC(null_mut(), screen);
        let previous = SelectObject(dc, bitmap);
        let font = GetStockObject(DEFAULT_GUI_FONT) as HFONT;
        let text = "A folder with several words in its name";
        let flags = DT_CENTER | DT_WORDBREAK | DT_END_ELLIPSIS;
        let mut narrow_lines = 0;
        let mut identity: *mut c_void = null_mut();
        for width in [80, 400, 120, 400, 80] {
            let rect = RECT {
                right: width,
                bottom: 100,
                ..Default::default()
            };
            chrome::fill(dc, &rect, 0);
            engine.draw(dc, text, rect, font, 0xffffff, flags).unwrap();
            assert_eq!(
                engine.layouts.len(),
                1,
                "resizing must not evict other labels"
            );
            let layout = &engine.layouts.values().next().unwrap().0;
            if identity.is_null() {
                identity = layout.as_raw();
            }
            assert_eq!(identity, layout.as_raw());
            assert_eq!(layout.GetMaxWidth(), width as f32);
            assert_eq!(layout.GetTextAlignment(), DWRITE_TEXT_ALIGNMENT_CENTER);
            let mut metrics = DWRITE_TEXT_METRICS::default();
            layout.GetMetrics(&mut metrics).unwrap();
            if width == 80 {
                narrow_lines = metrics.lineCount;
            }
            if width == 400 {
                assert!(metrics.lineCount < narrow_lines);
            }
        }
        let rect = RECT {
            right: 100,
            bottom: 24,
            ..Default::default()
        };
        engine.draw(dc, text, rect, font, 0xffffff, flags).unwrap();
        assert_eq!(
            engine.layouts.values().next().unwrap().0.GetMaxHeight(),
            24.0
        );
        // Different formatting must retain a separate layout.
        engine
            .draw(dc, text, rect, font, 0xffffff, DT_SINGLELINE)
            .unwrap();
        assert_eq!(engine.layouts.len(), 2);
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}
