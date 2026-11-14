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
        unsafe {
            DestroyWindow(self.hwnd as _);
            DestroyWindow(self.app.main);
        }
        EVENTS.with(|events| events.borrow_mut().clear());
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
