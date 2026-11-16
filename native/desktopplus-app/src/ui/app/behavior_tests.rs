use super::*;

// Hidden native windows exercise the real command/animation/persistence paths.
// Pointer containment is supplied directly, without moving the user's mouse.
struct Fixture {
    app: App,
    hwnd: usize,
}
impl Fixture {
    fn new(hover: bool, collapse: &str) -> Self {
        let create = || unsafe {
            let hwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                wide("STATIC").as_ptr(),
                wide("DesktopPlus collapse test").as_ptr(),
                WS_POPUP,
                100,
                100,
                440,
                PANEL_HEADER_HEIGHT,
                null_mut(),
                null_mut(),
                GetModuleHandleW(null()),
                null(),
            );
            assert!(!hwnd.is_null());
            hwnd
        };
        let main = create();
        let hwnd = create() as usize;
        unsafe { SetWindowLongPtrW(hwnd as _, GWLP_USERDATA, 2) };
        let store = Store::new(Some(
            std::env::temp_dir()
                .join(format!("desktopplus-collapse-{}", crate::model::new_id()))
                .join("settings.json"),
        ));
        let mut panel = Panel {
            is_collapsed: true,
            is_hidden: true,
            height: PANEL_HEADER_HEIGHT as f64,
            expanded_height: 400.0,
            ..Panel::default()
        };
        panel.normalize();
        panel_options::Options {
            hover,
            collapse: collapse.into(),
            ..Default::default()
        }
        .write(&mut panel);
        let window = PanelWindow {
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
        };
        Self {
            app: App {
                main,
                worker: Worker::start(main as usize, store.data.join("SearchIndex")),
                store,
                settings: Settings {
                    windows: vec![panel],
                    ..Default::default()
                },
                preference_languages: Vec::new(),
                updater: crate::update::Updater::default(),
                windows: HashMap::from([(hwnd, window)]),
                hidden: false,
                foreground: false,
                foreground_restore: None,
                exiting: false,
                tray: unsafe { std::mem::zeroed() },
                smoke: None,
                started: Instant::now(),
                sorting: false,
                smoke_errors: Vec::new(),
            },
            hwnd,
        }
    }
    fn bounds(&self) -> RECT {
        let mut bounds = RECT::default();
        unsafe { GetWindowRect(self.hwnd as _, &mut bounds) };
        motion::destination(self.hwnd as _).unwrap_or(bounds)
    }
    fn pointer(&mut self, inside: bool) {
        let mut bounds = RECT::default();
        unsafe { GetWindowRect(self.hwnd as _, &mut bounds) };
        self.app.hover_at(self.hwnd, inside, bounds);
    }
    fn double_click(&mut self, message: u32) {
        unsafe {
            window_proc(self.hwnd as _, message, HTCAPTION as usize, 20 << 16);
        }
        let events = EVENTS.with(|events| std::mem::take(&mut *events.borrow_mut()));
        assert!(
            events
                .iter()
                .any(|event| matches!(event, Event::HeaderDoubleClick(hwnd) if *hwnd == self.hwnd))
        );
        for event in events {
            self.app.event(event).unwrap();
        }
    }
    fn assert_height(&self, height: i32) {
        let bounds = self.bounds();
        assert_eq!(bounds.bottom - bounds.top, height);
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        PANEL_PALETTES.with(|palettes| palettes.borrow_mut().remove(&self.hwnd));
        glass::release(self.hwnd as _);
        unsafe {
            chrome::release(self.hwnd as _);
            chrome::release(self.app.main);
            DestroyWindow(self.hwnd as _);
            DestroyWindow(self.app.main);
        }
        EVENTS.with(|events| events.borrow_mut().clear());
    }
}

#[test]
fn details_column_widths_roundtrip_without_special_handling_for_the_name_column() {
    let mut fixture = Fixture::new(false, "button");
    unsafe {
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_LISTVIEW_CLASSES,
        });
    }
    let columns: Vec<_> = crate::metadata::COLUMNS
        .iter()
        .map(|(_, caption, width)| (*caption, *width))
        .collect();
    let list = create_list(fixture.hwnd as _, &columns);
    for name_width in [160, 540, 840] {
        let expected = [name_width, 96, 120, 148];
        for (column, width) in expected.iter().enumerate() {
            set_column_width(list, column, *width, true);
        }
        fixture.app.capture_columns(fixture.hwnd);
        let saved = fixture.app.settings.windows[0].active().extra["MetadataWidths"].clone();
        for width in [360, 1020] {
            unsafe { MoveWindow(list, 0, 0, width, 400, 0) };
            fixture.app.apply_columns(fixture.hwnd);
            for (column, expected) in expected.iter().enumerate() {
                assert_eq!(
                    unsafe { SendMessageW(list, LVM_GETCOLUMNWIDTH, column, 0) },
                    *expected as isize
                );
            }
            fixture.app.capture_columns(fixture.hwnd);
            assert_eq!(
                fixture.app.settings.windows[0].active().extra["MetadataWidths"],
                saved
            );
        }
    }
}

#[test]
fn panel_theme_switch_replaces_colors_in_nested_controls_without_tinting_main() {
    let mut fixture = Fixture::new(false, "both");
    fixture.app.settings.appearance.glass_mode = crate::model::GlassMode::Solid;
    let panel = fixture.hwnd as HWND;
    let body = child(panel, "STATIC", "", 801, 0);
    let label = child(body, "STATIC", "Theme", 2000, 0);
    let owned = unsafe {
        CreateWindowExW(
            0,
            wide("STATIC").as_ptr(),
            null(),
            WS_POPUP,
            0,
            0,
            200,
            100,
            panel,
            null_mut(),
            GetModuleHandleW(null()),
            null(),
        )
    };
    assert!(!owned.is_null());
    let owned_body = child(owned, "STATIC", "", 801, 0);
    let global = with_palette(fixture.app.main, |p| (p.background, p.accent));
    fixture.app.settings.presets = vec![
        serde_json::json!({"Name": "Forest", "Settings": {
            "BackgroundColor": "#103020", "HeaderColor": "#205030", "AccentColor": "#80EEA0"
        }}),
        serde_json::json!({"Name": "Paper", "Settings": {
            "BackgroundColor": "#FAF0E0", "HeaderColor": "#E0D0C0", "AccentColor": "#904020"
        }}),
    ];
    for (name, background, header, accent) in [
        ("Forest", "#103020", "#205030", "#80EEA0"),
        ("Paper", "#FAF0E0", "#E0D0C0", "#904020"),
        ("Forest", "#103020", "#205030", "#80EEA0"),
    ] {
        for window in [panel, body, label] {
            motion::color(window, 90, 0);
        }
        fixture.app.settings.windows[0].preset_name = name.into();
        fixture.app.apply_panel_theme(fixture.hwnd);
        for window in [panel, body, label, owned, owned_body] {
            assert_eq!(
                with_palette(window, |p| (p.background, p.surface, p.accent)),
                (color(background, 0), color(header, 0), color(accent, 0)),
                "{name}: {window:?} must use only the selected panel palette"
            );
        }
        for window in [panel, body, label] {
            assert_eq!(
                motion::color(window, 90, color(accent, 0)),
                color(accent, 0)
            );
        }
        assert_eq!(
            with_palette(fixture.app.main, |p| (p.background, p.accent)),
            global
        );
    }
    for name in ["", "Missing preset"] {
        fixture.app.settings.windows[0].preset_name = name.into();
        fixture.app.apply_panel_theme(fixture.hwnd);
        for window in [panel, label, owned_body] {
            assert_eq!(with_palette(window, |p| (p.background, p.accent)), global);
        }
    }
    // A saved global appearance controls the main window; selecting a green
    // panel preset must neither recolor it nor overwrite that saved setting.
    fixture.app.settings.appearance.background_color = "#202327".into();
    fixture.app.settings.windows[0].preset_name = "Forest".into();
    fixture.app.apply_theme();
    assert_eq!(
        with_palette(fixture.app.main, |p| p.background),
        color("#202327", 0)
    );
    assert_eq!(with_palette(label, |p| p.background), color("#103020", 0));
    fixture.app.store.save(&fixture.app.settings).unwrap();
    let saved = fixture.app.store.load(false).unwrap();
    assert_eq!(saved.appearance.background_color, "#202327");
    assert_eq!(saved.windows[0].preset_name, "Forest");
    unsafe {
        chrome::release(body);
        chrome::release(owned);
        DestroyWindow(owned);
    }
}

#[test]
fn header_double_click_pins_hover_expansion_and_restores_hover_on_release() {
    for message in [WM_NCLBUTTONDBLCLK, WM_LBUTTONDBLCLK] {
        let mut fixture = Fixture::new(true, "doubleclick");
        fixture.pointer(true);
        assert!(fixture.app.windows[&fixture.hwnd].hover_restore.is_some());
        fixture.double_click(message);
        fixture.pointer(false); // A leave event already queued before the click.
        fixture.assert_height(400);
        assert!(!fixture.app.settings.windows[0].is_collapsed);
        assert!(fixture.app.windows[&fixture.hwnd].hover_restore.is_none());
        fixture.app.capture_geometry();
        let saved: Settings =
            serde_json::from_value(serde_json::to_value(&fixture.app.settings).unwrap()).unwrap();
        assert!(!saved.windows[0].is_collapsed);
        assert_eq!(saved.windows[0].expanded_height, 400.0);

        fixture.double_click(message);
        fixture.pointer(true); // Still over the header: do not reopen immediately.
        assert!(fixture.app.settings.windows[0].is_collapsed);
        assert!(fixture.app.windows[&fixture.hwnd].hover_restore.is_none());
        fixture.assert_height(chrome::scale(fixture.hwnd as _, PANEL_HEADER_HEIGHT));
        motion::cancel_resize(fixture.hwnd as _);
        fixture.pointer(false);
        fixture.pointer(true);
        assert!(fixture.app.windows[&fixture.hwnd].hover_restore.is_some());
        fixture.assert_height(400);
        fixture.pointer(false);
        fixture.assert_height(chrome::scale(fixture.hwnd as _, PANEL_HEADER_HEIGHT));
    }
}

#[test]
fn first_caption_click_does_not_turn_double_click_into_a_collapse() {
    let mut fixture = Fixture::new(true, "both");
    fixture.pointer(true);
    // Windows may finish a caption move loop for the first click, promoting
    // hover expansion before the actual double-click message is delivered.
    motion::cancel_resize(fixture.hwnd as _);
    fixture.app.event(Event::Geometry(fixture.hwnd)).unwrap();
    fixture.double_click(WM_NCLBUTTONDBLCLK);
    fixture.pointer(false);
    fixture.assert_height(400);
    assert!(!fixture.app.settings.windows[0].is_collapsed);
}

#[test]
fn header_double_click_can_pin_before_the_first_hover_tick() {
    let mut fixture = Fixture::new(true, "both");
    fixture.double_click(WM_LBUTTONDBLCLK);
    fixture.pointer(false);
    fixture.assert_height(400);
    assert!(!fixture.app.settings.windows[0].is_collapsed);
}

#[test]
fn double_click_without_hover_toggles_and_button_only_mode_ignores_it() {
    for message in [WM_NCLBUTTONDBLCLK, WM_LBUTTONDBLCLK] {
        let mut fixture = Fixture::new(false, "both");
        fixture.double_click(message);
        fixture.assert_height(400);
        fixture.double_click(message);
        fixture.assert_height(chrome::scale(fixture.hwnd as _, PANEL_HEADER_HEIGHT));

        let mut fixture = Fixture::new(true, "button");
        fixture.pointer(true);
        fixture.double_click(message);
        fixture.pointer(false);
        assert!(fixture.app.settings.windows[0].is_collapsed);
        fixture.assert_height(PANEL_HEADER_HEIGHT);
    }
}

#[test]
fn collapse_button_still_closes_temporary_hover_expansion() {
    let mut fixture = Fixture::new(true, "both");
    fixture.pointer(true);
    fixture.app.command(fixture.hwnd, COLLAPSE).unwrap();
    fixture.assert_height(PANEL_HEADER_HEIGHT);
    assert!(fixture.app.settings.windows[0].is_collapsed);
    assert!(fixture.app.windows[&fixture.hwnd].hover_restore.is_none());
}
