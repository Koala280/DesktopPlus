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
            let mut panel = Panel {
                is_hidden,
                ..Panel::default()
            };
            panel.normalize();
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
fn overview_visibility_changes_only_the_requested_panels_and_survives_serialization() {
    let mut windows = Windows::new();
    let original_tabs = serde_json::to_value(&windows.app.settings.windows[0].tabs).unwrap();
    windows.app.event(Event::PanelVisibility(0)).unwrap();
    unsafe {
        assert_eq!(IsWindowVisible(windows.visible), 0);
        assert_eq!(IsWindowVisible(windows.hidden), 0);
    }
    assert!(windows.app.settings.windows[0].is_hidden);
    windows.app.event(Event::PanelVisibility(0)).unwrap();
    unsafe {
        assert_ne!(IsWindowVisible(windows.visible), 0);
        assert_eq!(IsWindowVisible(windows.hidden), 0);
    }
    let saved: Settings =
        serde_json::from_slice(&serde_json::to_vec(&windows.app.settings).unwrap()).unwrap();
    assert!(!saved.windows[0].is_hidden);
    assert!(saved.windows[1].is_hidden);
    assert_eq!(
        serde_json::to_value(&saved.windows[0].tabs).unwrap(),
        original_tabs
    );
}

#[test]
fn hidden_tabs_map_native_selection_to_the_correct_source_and_reorder_visible_neighbors() {
    let mut windows = Windows::new();
    let hwnd = windows.visible as usize;
    unsafe {
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_TAB_CLASSES,
        });
    }
    let control = child(windows.visible, "SysTabControl32", "", TABS, 0);
    windows.app.settings.windows[0].tabs = Some(
        (0..4)
            .map(|i| Tab {
                tab_name: format!("Tab {i}"),
                panel_type: "List".into(),
                pinned_items: vec![format!("C:\\item-{i}.txt")],
                ..Tab::default()
            })
            .collect(),
    );
    windows
        .app
        .apply_tab_visibility(0, &[false, true, false, true]);
    assert_eq!(windows.app.settings.windows[0].active_tab_index, 1);
    unsafe {
        assert_eq!(SendMessageW(control, TCM_GETITEMCOUNT, 0, 0), 2);
        assert_eq!(SendMessageW(control, TCM_GETCURSEL, 0, 0), 0);
    }
    windows.app.event(Event::Tab(hwnd, 1)).unwrap();
    assert_eq!(windows.app.settings.windows[0].active_tab_index, 3);
    assert_eq!(
        windows.app.settings.windows[0].active().pinned_items,
        ["C:\\item-3.txt"]
    );
    windows.app.tab_command(hwnd, TAB_LEFT).unwrap();
    assert_eq!(windows.app.settings.windows[0].active_tab_index, 1);
    assert_eq!(windows.app.settings.windows[0].active().tab_name, "Tab 3");
    windows.app.command(hwnd, HIDE_TAB).unwrap();
    assert_eq!(windows.app.settings.windows[0].active_tab_index, 3);
    assert_eq!(windows.app.settings.windows[0].active().tab_name, "Tab 1");
    assert_eq!(unsafe { SendMessageW(control, TCM_GETITEMCOUNT, 0, 0) }, 1);
    let original = serde_json::to_value(&windows.app.settings.windows[0].tabs).unwrap();
    windows.app.apply_tab_visibility(0, &[true]);
    assert_eq!(
        serde_json::to_value(&windows.app.settings.windows[0].tabs).unwrap(),
        original
    );
}

#[test]
fn all_hidden_tabs_hide_the_panel_and_can_be_restored_without_losing_tabs() {
    let mut windows = Windows::new();
    windows.app.settings.windows[0]
        .tabs
        .as_mut()
        .unwrap()
        .push(Tab::default());
    windows.app.apply_tab_visibility(0, &[false, false]);
    assert_eq!(unsafe { IsWindowVisible(windows.visible) }, 0);
    assert!(windows.app.settings.windows[0].hidden());
    assert!(!windows.app.settings.windows[0].is_hidden);
    windows.app.toggle();
    windows.app.toggle();
    assert_eq!(unsafe { IsWindowVisible(windows.visible) }, 0);
    windows.app.apply_tab_visibility(0, &[false, true]);
    assert_ne!(unsafe { IsWindowVisible(windows.visible) }, 0);
    assert_eq!(windows.app.settings.windows[0].active_tab_index, 1);
    windows.app.apply_tab_visibility(0, &[false, false]);
    windows.app.event(Event::PanelVisibility(0)).unwrap();
    assert_ne!(unsafe { IsWindowVisible(windows.visible) }, 0);
    assert_eq!(
        windows.app.settings.windows[0]
            .visible_tab_indices()
            .collect::<Vec<_>>(),
        vec![1]
    );
    assert_eq!(
        windows.app.settings.windows[0].tabs.as_ref().unwrap().len(),
        2
    );
    windows.app.set_panel_visibility(&[0], false);
    windows.app.apply_tab_visibility(0, &[true, true]);
    assert_eq!(unsafe { IsWindowVisible(windows.visible) }, 0);
}

#[test]
fn showing_one_globally_hidden_panel_keeps_the_others_hidden() {
    let mut windows = Windows::new();
    windows.app.set_panel_visibility(&[0, 1], true);
    windows.app.toggle();
    assert!(windows.app.hidden);
    windows.app.event(Event::PanelVisibility(1)).unwrap();
    assert!(!windows.app.hidden);
    assert!(windows.app.settings.windows[0].is_hidden);
    assert!(!windows.app.settings.windows[1].is_hidden);
    unsafe {
        assert_eq!(IsWindowVisible(windows.visible), 0);
        assert_ne!(IsWindowVisible(windows.hidden), 0);
    }
    windows.app.toggle();
    windows.app.toggle();
    unsafe {
        assert_eq!(IsWindowVisible(windows.visible), 0);
        assert_ne!(IsWindowVisible(windows.hidden), 0);
    }
}

#[test]
fn overview_commands_apply_to_the_whole_selection_and_ignore_empty_selection() {
    let mut windows = Windows::new();
    let main = windows.app.main;
    unsafe {
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_LISTVIEW_CLASSES,
        });
    }
    let list = child(main, "SysListView32", "", LIST, LVS_REPORT | LVS_OWNERDATA);
    unsafe {
        SendMessageW(list, LVM_SETITEMCOUNT, 2, 0);
    }
    let select = |value| unsafe {
        let item = LVITEMW {
            stateMask: LVIS_SELECTED,
            state: if value { LVIS_SELECTED } else { 0 },
            ..std::mem::zeroed()
        };
        SendMessageW(
            list,
            LVM_SETITEMSTATE,
            usize::MAX,
            &item as *const _ as isize,
        );
    };
    select(true);
    for (command, visible) in [
        (SHOW_PANEL, true),
        (HIDE_PANEL, false),
        (TOGGLE_SELECTED, true),
        (TOGGLE_SELECTED, false),
    ] {
        windows.app.command(main as usize, command).unwrap();
        for (index, hwnd) in [windows.visible, windows.hidden].into_iter().enumerate() {
            assert_eq!(windows.app.settings.windows[index].is_hidden, !visible);
            assert_eq!(unsafe { IsWindowVisible(hwnd) } != 0, visible);
        }
    }
    select(false);
    windows.app.command(main as usize, SHOW_PANEL).unwrap();
    assert!(
        windows
            .app
            .settings
            .windows
            .iter()
            .all(|panel| panel.is_hidden)
    );
}

#[test]
fn foreground_only_raises_unhidden_panels_and_release_restores_desktop_stacking() {
    let mut windows = Windows::new();
    let original = serde_json::to_value(&windows.app.settings).unwrap();
    for _ in 0..3 {
        windows.app.set_foreground(true);
        windows.app.set_foreground(true);
        for (hwnd, raised) in [(windows.visible, true), (windows.hidden, false)] {
            unsafe {
                assert_eq!(IsWindowVisible(hwnd) != 0, raised);
                assert_eq!(
                    GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST as isize != 0,
                    raised
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
fn foreground_keeps_globally_hidden_panels_hidden() {
    let mut windows = Windows::new();
    windows.app.toggle();
    assert!(windows.app.hidden);
    windows.app.set_foreground(true);
    unsafe {
        for hwnd in [windows.visible, windows.hidden] {
            assert_eq!(IsWindowVisible(hwnd), 0);
            assert_eq!(
                GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST as isize,
                0
            );
        }
    }
    windows.app.set_foreground(false);
    windows.assert_released(true);
    assert!(windows.app.hidden);
}

#[test]
fn foreground_anchor_only_selects_visible_unhidden_panels() {
    let windows = Windows::new();
    let visible = windows.visible as usize;
    let hidden = windows.hidden as usize;
    assert_eq!(windows.app.foreground_panel(visible), Some(visible));
    assert_eq!(windows.app.foreground_panel(hidden), Some(visible));
    assert_eq!(windows.app.foreground_panel(0), Some(visible));

    // Saved visibility still applies if the native visibility is out of sync.
    unsafe {
        ShowWindow(windows.hidden, SW_SHOWNOACTIVATE);
    }
    assert_eq!(windows.app.foreground_panel(hidden), Some(visible));
    unsafe {
        ShowWindow(windows.visible, SW_HIDE);
    }
    assert_eq!(windows.app.foreground_panel(visible), None);
    assert_eq!(windows.app.foreground_panel(hidden), None);
}

#[test]
fn foreground_does_not_activate_any_panel_when_all_are_hidden() {
    for globally_hidden in [false, true] {
        let mut windows = Windows::new();
        if globally_hidden {
            windows.app.toggle();
        } else {
            for panel in &mut windows.app.settings.windows {
                panel.is_hidden = true;
            }
            unsafe {
                ShowWindow(windows.visible, SW_HIDE);
            }
        }
        windows.app.activate_foreground_panel();
        assert_eq!(windows.app.foreground_restore, None);
        unsafe {
            assert_eq!(IsWindowVisible(windows.visible), 0);
            assert_eq!(IsWindowVisible(windows.hidden), 0);
        }
        windows.app.set_foreground(true);
        windows.app.set_foreground(false);
        windows.assert_released(true);
    }
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
