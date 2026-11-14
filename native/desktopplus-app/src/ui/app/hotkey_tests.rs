use super::*;

#[test]
fn releasing_any_required_key_ends_the_foreground_chord() {
    for (chord, keys) in [
        ("Alt + G", vec![VK_MENU, b'G' as u16]),
        (
            "Ctrl + Alt + Shift + F12",
            vec![VK_CONTROL, VK_MENU, VK_SHIFT, VK_F12],
        ),
        ("Win + G", vec![VK_LWIN, b'G' as u16]),
        ("Win + G", vec![VK_RWIN, b'G' as u16]),
    ] {
        assert!(hotkey_held_with(chord, |key| keys.contains(&(key as u16))));
        for released in &keys {
            assert!(
                !hotkey_held_with(chord, |key| {
                    key != *released as u32 && keys.contains(&(key as u16))
                }),
                "{chord} must end when key {released} is released"
            );
        }
    }
    assert!(!hotkey_held_with("", |_| true));
}

// Exercise actual Win32 visibility and stacking without opening the application
// or sending keyboard input. All test windows stay outside the visible desktop.
struct Windows {
    app: App,
    visible: HWND,
    hidden: HWND,
    covering: HWND,
}
impl Windows {
    fn new() -> Self {
        let create = || unsafe {
            let hwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                wide("STATIC").as_ptr(),
                wide("DesktopPlus hotkey test").as_ptr(),
                WS_POPUP,
                -32000,
                -32000,
                20,
                20,
                null_mut(),
                null_mut(),
                GetModuleHandleW(null()),
                null(),
            );
            assert!(!hwnd.is_null());
            hwnd
        };
        let main = create();
        let visible = create();
        let hidden = create();
        let covering = create();
        let directory =
            std::env::temp_dir().join(format!("desktopplus-hotkey-{}", crate::model::new_id()));
        let store = Store::new(Some(directory.join("settings.json")));
        let mut app = App {
            main,
            worker: Worker::start(main as usize, store.data.join("SearchIndex")),
            store,
            settings: Settings::default(),
            preference_languages: Vec::new(),
            updater: crate::update::Updater::default(),
            windows: HashMap::new(),
            hidden: false,
            foreground: false,
            foreground_restore: None,
            exiting: false,
            tray: unsafe { std::mem::zeroed() },
            smoke: Some(directory.join("unused.json")),
            started: Instant::now(),
            sorting: false,
            smoke_errors: Vec::new(),
        };
        for (hwnd, is_hidden) in [(visible, false), (hidden, true)] {
            let panel = Panel {
                is_hidden,
                ..Panel::default()
            };
            app.windows.insert(
                hwnd as usize,
                PanelWindow {
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
                },
            );
            app.settings.windows.push(panel);
        }
        unsafe {
            ShowWindow(visible, SW_SHOWNOACTIVATE);
            ShowWindow(covering, SW_SHOWNOACTIVATE);
            SetWindowPos(
                visible,
                HWND_BOTTOM,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
        }
        Self {
            app,
            visible,
            hidden,
            covering,
        }
    }
    fn assert_released(&self, hidden: bool) {
        assert!(!self.app.foreground);
        unsafe {
            assert_eq!(IsWindowVisible(self.visible) != 0, !hidden);
            assert_eq!(IsWindowVisible(self.hidden), 0);
            for hwnd in [self.visible, self.hidden] {
                assert_eq!(
                    GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST as isize,
                    0
                );
            }
            // Removing TOPMOST alone fails this assertion: the panel would
            // still be ahead of the ordinary covering window.
            let mut above = GetWindow(self.visible, GW_HWNDPREV);
            while !above.is_null() && above != self.covering {
                above = GetWindow(above, GW_HWNDPREV);
            }
            assert_eq!(above, self.covering);
        }
    }
}
impl Drop for Windows {
    fn drop(&mut self) {
        self.app.set_foreground(false);
        unsafe {
            for hwnd in [self.visible, self.hidden, self.covering, self.app.main] {
                DestroyWindow(hwnd);
            }
        }
    }
}

#[test]
fn foreground_release_restores_hidden_panels_and_desktop_stacking() {
    let mut windows = Windows::new();
    let original = serde_json::to_value(&windows.app.settings).unwrap();
    for _ in 0..3 {
        windows.app.set_foreground(true);
        windows.app.set_foreground(true);
        for hwnd in [windows.visible, windows.hidden] {
            unsafe {
                assert_ne!(IsWindowVisible(hwnd), 0);
                assert_ne!(
                    GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST as isize,
                    0
                );
            }
        }
        windows.app.set_foreground(false);
        windows.assert_released(false);
    }
    assert_eq!(
        serde_json::to_value(&windows.app.settings).unwrap(),
        original
    );
}

#[test]
fn foreground_release_restores_globally_hidden_panels() {
    let mut windows = Windows::new();
    windows.app.toggle();
    assert!(windows.app.hidden);
    windows.app.set_foreground(true);
    windows.app.set_foreground(false);
    windows.assert_released(true);
    assert!(windows.app.hidden);
}

#[test]
fn shortcut_changes_and_visibility_toggle_cancel_foreground() {
    let mut windows = Windows::new();
    windows.app.set_foreground(true);
    windows.app.register_hotkeys();
    windows.assert_released(false);
    windows.app.set_foreground(true);
    windows.app.toggle();
    windows.assert_released(true);
}

#[test]
fn released_or_stale_hotkey_events_do_not_leave_panels_raised() {
    let mut windows = Windows::new();
    // An unconfigured chord is deterministically released, independent of any
    // physical keyboard input while the unit tests are running.
    windows
        .app
        .settings
        .global_shortcuts
        .foreground_panels_hotkey
        .clear();
    windows.app.set_foreground(true);
    windows.app.event(Event::Timer(FOREGROUND_TIMER)).unwrap();
    windows.assert_released(false);
    windows.app.event(Event::Hotkey(2)).unwrap();
    windows.assert_released(false);
    windows.app.event(Event::Timer(FOREGROUND_TIMER)).unwrap();
    windows.assert_released(false);
}

#[test]
fn release_restores_previous_focus_only_while_a_panel_is_still_active() {
    let mut windows = Windows::new();
    let previous = windows.covering as usize;
    windows.app.foreground_restore = Some(previous);
    assert_eq!(
        windows
            .app
            .take_foreground_restore(windows.visible as usize),
        Some(previous)
    );
    assert_eq!(windows.app.foreground_restore, None);

    // If the user switches applications during the hold, keep their new focus.
    windows.app.foreground_restore = Some(windows.app.main as usize);
    assert_eq!(windows.app.take_foreground_restore(previous), None);
    assert_eq!(windows.app.foreground_restore, None);

    // Do not raise a panel again when it was already active before the hold.
    windows.app.foreground_restore = Some(windows.visible as usize);
    assert_eq!(
        windows
            .app
            .take_foreground_restore(windows.visible as usize),
        None
    );
}

#[test]
fn repeated_foreground_activation_preserves_the_original_focus_target() {
    let mut windows = Windows::new();
    windows.app.set_foreground(true);
    let previous = windows.covering as usize;
    windows.app.foreground_restore = Some(previous);
    windows.app.activate_foreground_panel();
    assert_eq!(windows.app.foreground_restore, Some(previous));
}

#[test]
fn unchanged_search_keeps_the_listing_generation() {
    let mut windows = Windows::new();
    windows.app.settings.windows[0].normalize();
    let edit = child(windows.visible, "EDIT", "", SEARCH, ES_AUTOHSCROLL as u32);
    let hwnd = windows.visible as usize;
    for (query, expected_generation) in [
        ("", 0),
        ("   ", 0),
        ("notes", 1),
        ("notes", 1),
        (" notes ", 1),
        ("", 2),
        ("", 2),
    ] {
        set_text(edit, query);
        windows.app.event(Event::Search(hwnd)).unwrap();
        assert_eq!(windows.app.windows[&hwnd].generation, expected_generation);
    }
}

#[test]
fn clicking_search_does_not_request_dismissal_but_escape_does() {
    let windows = Windows::new();
    let edit = child(
        windows.visible,
        "EDIT",
        "notes",
        SEARCH,
        ES_AUTOHSCROLL as u32,
    );
    chrome::setup_search(edit);
    EVENTS.with(|events| events.borrow_mut().clear());
    unsafe {
        SendMessageW(edit, WM_LBUTTONDOWN, 1, (5 << 16) | 5);
        SendMessageW(edit, WM_LBUTTONUP, 0, (5 << 16) | 5);
    }
    assert!(EVENTS.with(|events| {
        !events
            .borrow()
            .iter()
            .any(|event| matches!(event, Event::SearchVisibility(_, false)))
    }));
    assert_eq!(text(edit), "notes");
    unsafe {
        SendMessageW(edit, WM_KEYDOWN, VK_ESCAPE as usize, 0);
    }
    assert!(EVENTS.with(|events| {
        events.borrow().iter().any(|event|
        matches!(event, Event::SearchVisibility(hwnd, false) if *hwnd == windows.visible as usize))
    }));
    EVENTS.with(|events| events.borrow_mut().clear());
}
