use super::*;

fn close(actual: f64, expected: f64) {
    assert!((actual - expected).abs() < 1e-8, "{actual} != {expected}");
}

#[test]
fn motion_starts_at_rest_moves_monotonically_and_finishes_exactly() {
    let start = Instant::now();
    for (from, to) in [(0.0, 16.0), (500.0, 48.0), (-1200.0, -600.0)] {
        let mut spring = Scalar::new(from, start);
        spring.retarget(to, start, true);
        assert_eq!(spring.sample(start), (from, 0.0));
        let mut previous = from;
        for ms in 1..=650 {
            let now = start + Duration::from_millis(ms);
            spring.advance(now, true);
            let value = spring.sample(now).0;
            assert!(value >= from.min(to) && value <= from.max(to));
            assert!((value - previous) * (to - from) >= 0.0);
            previous = value;
        }
        assert_eq!(spring.sample(start + CONTROL_DURATION), (to, 0.0));
        assert!(!spring.active());
    }
}

#[test]
fn reversal_preserves_position_and_velocity() {
    let start = Instant::now();
    let mut spring = Scalar::new(48.0, start);
    spring.retarget(600.0, start, true);
    for (ms, target) in [(50, 48.0), (85, 600.0), (120, 48.0)] {
        let now = start + Duration::from_millis(ms);
        let (position, velocity) = spring.sample(now);
        spring.retarget(target, now, true);
        let (new_position, new_velocity) = spring.sample(now);
        close(new_position, position);
        close(new_velocity, velocity);
        assert_eq!(spring.to, target);
    }
}

#[test]
fn identical_targets_do_not_restart_the_animation() {
    let start = Instant::now();
    let mut spring = Scalar::new(0.0, start);
    spring.retarget(150.0, start, true);
    let expected = spring;
    for ms in (1..200).step_by(7) {
        spring.retarget(150.0, start + Duration::from_millis(ms), true);
    }
    assert_eq!(spring.started, start);
    assert_eq!(
        spring.sample(start + Duration::from_millis(200)),
        expected.sample(start + Duration::from_millis(200))
    );
}

#[test]
fn sparse_and_high_refresh_frames_produce_the_same_motion() {
    let start = Instant::now();
    let mut spring = Scalar::new(0.0, start);
    spring.retarget(800.0, start, true);
    let expected = spring.sample(start + Duration::from_millis(200));
    for interval in [4, 7, 16, 33, 100] {
        let mut sampled = spring;
        for ms in (0..200).step_by(interval) {
            sampled.advance(start + Duration::from_millis(ms), true);
        }
        assert_eq!(sampled.sample(start + Duration::from_millis(200)), expected);
    }
}

#[test]
fn reduced_motion_and_long_stalls_snap_to_the_destination() {
    let start = Instant::now();
    let mut spring = Scalar::new(0.0, start);
    spring.retarget(800.0, start, false);
    assert_eq!(spring.sample(start), (800.0, 0.0));
    assert!(!spring.active());
    spring.retarget(0.0, start, true);
    spring.advance(start + Duration::from_millis(50), false);
    assert_eq!(spring.sample(start), (0.0, 0.0));
    spring.retarget(500.0, start, true);
    spring.advance(start + Duration::from_secs(10), true);
    assert_eq!(spring.sample(start + Duration::from_secs(10)), (500.0, 0.0));
}

#[test]
fn panel_motion_has_soft_endpoints_and_no_lingering_tail() {
    let now = Instant::now();
    let mut motion = Scalar::new(48.0, now);
    motion.retarget_for(460.0, now, true, PANEL_DURATION);
    assert_eq!(motion.sample(now), (48.0, 0.0));
    assert!(motion.sample(now + Duration::from_millis(16)).0 < 55.0);
    close(motion.sample(now + PANEL_DURATION / 2).0, 254.0);
    motion.advance(now + PANEL_DURATION, true);
    assert_eq!(motion.sample(now + PANEL_DURATION), (460.0, 0.0));
    assert!(!motion.active());
}

#[test]
fn colors_have_soft_endpoints_and_bounded_channels() {
    let start = Instant::now();
    assert_eq!(progress(start, start), 0.0);
    assert_eq!(progress(start, start + Duration::from_secs(1)), 1.0);
    assert!(progress(start, start + Duration::from_millis(1)) < 0.001);
    assert!(progress(start, start + Duration::from_millis(COLOR_MS - 1)) > 0.999);
    for ms in 0..=COLOR_MS {
        let color = mix(
            0x224466,
            0xaabbcc,
            progress(start, start + Duration::from_millis(ms)),
        );
        for (shift, lo, hi) in [(0, 0x66, 0xcc), (8, 0x44, 0xbb), (16, 0x22, 0xaa)] {
            assert!((lo..=hi).contains(&((color >> shift) & 255)));
        }
    }
}

#[test]
fn row_damage_is_combined_without_repainting_the_entire_list() {
    let mut dirty = Dirty::default();
    dirty.add(Damage::Area(RECT {
        left: 0,
        top: 30,
        right: 600,
        bottom: 60,
    }));
    dirty.add(Damage::Area(RECT {
        left: 0,
        top: 60,
        right: 600,
        bottom: 90,
    }));
    dirty.add(Damage::Scrollbars);
    let rect = dirty.area.unwrap();
    assert_eq!(
        (rect.left, rect.top, rect.right, rect.bottom),
        (0, 30, 600, 90)
    );
    assert!(!dirty.client);
    assert!(dirty.scrollbars);
}

#[test]
fn hidden_control_transitions_settle_and_destruction_releases_state() {
    unsafe {
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_STANDARD_CLASSES,
        });
        let hwnd = CreateWindowExW(
            0,
            wide("STATIC").as_ptr(),
            null(),
            0,
            0,
            0,
            20,
            20,
            HWND_MESSAGE,
            null_mut(),
            GetModuleHandleW(null()),
            null(),
        );
        assert!(!hwnd.is_null());
        let start = Instant::now();
        let mut state = attach(hwnd);
        state.colors.insert(
            0,
            Color {
                from: 0,
                to: 0xffffff,
                started: start,
                damage: Damage::Client,
            },
        );
        let mut spring = Scalar::new(0.0, start);
        spring.retarget(16.0, start, true);
        state.scalars.insert(0, spring);
        state.layout_scalars.insert(0, spring);
        STATES.with(|s| s.borrow_mut().insert(hwnd as usize, state));
        assert_eq!(active_count(), 1);
        tick(false);
        assert_eq!(active_count(), 0);
        assert_eq!(color(hwnd, 0, 0xffffff), 0xffffff);
        assert_eq!(scalar(hwnd, 0, 16.0), 16.0);
        assert_eq!(layout_scalar(hwnd, 0, 16.0), 16.0);
        DestroyWindow(hwnd);
        assert!(STATES.with(|s| !s.borrow().contains_key(&(hwnd as usize))));
    }
}

/// Opt-in check of just panel motion, tabs and a switch. No user data,
/// filesystem worker, tray, settings writes or other application windows.
#[test]
#[ignore = "Focused visual check of animation rendering"]
fn preview_panel_motion() {
    unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_LISTVIEW_CLASSES | ICC_TAB_CLASSES | ICC_STANDARD_CLASSES,
        });
        let instance = GetModuleHandleW(null());
        let class = wide("DesktopPlus.AnimationPreview");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..Default::default()
        });
        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            class.as_ptr(),
            wide("Animation preview").as_ptr(),
            WS_POPUP | WS_SYSMENU | WS_THICKFRAME | WS_CLIPCHILDREN,
            120,
            120,
            744,
            460,
            null_mut(),
            null_mut(),
            instance,
            2usize as _,
        );
        assert!(!hwnd.is_null());
        theme_frame(hwnd);
        let tabs = child(
            hwnd,
            "SysTabControl32",
            "",
            TABS,
            WS_TABSTOP | TCS_SINGLELINE | TCS_FIXEDWIDTH | TCS_FOCUSNEVER,
        );
        chrome::setup_tabs(tabs);
        for (i, name) in ["Music", "FL Projects"].into_iter().enumerate() {
            let mut caption = wide(name);
            let item = TCITEMW {
                mask: TCIF_TEXT,
                pszText: caption.as_mut_ptr(),
                ..std::mem::zeroed()
            };
            SendMessageW(tabs, TCM_INSERTITEMW, i, &item as *const _ as isize);
        }
        for (id, caption) in [
            (COLLAPSE, "Collapse / expand"),
            (HIDE_PANEL, "Hide panel"),
            (SEARCH_TOGGLE, "Search"),
        ] {
            button(hwnd, caption, id);
        }
        let list = create_list(hwnd, &[("Name", 380), ("Type", 150)]);
        let switch = styled::check(list, "Smooth motion", 950, false);
        SetWindowPos(
            switch,
            null_mut(),
            14,
            80,
            510,
            44,
            SWP_NOZORDER | SWP_NOACTIVATE,
        );
        child(hwnd, "STATIC", "Animation preview", STATUS, 0);
        VIEWS.with(|views| {
            views.borrow_mut().insert(
                hwnd as usize,
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
                    tab_count: 2,
                    kind: "Folder".into(),
                    loaded: true,
                    searching: false,
                    search_open: false,
                    options: panel_options::Options {
                        alignment: "center".into(),
                        search: "button".into(),
                        ..Default::default()
                    },
                    hover_tracking: false,
                    zoom: 1.0,
                },
            );
        });
        layout(hwnd);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(hwnd);
        println!("Animation preview HWND: {}", hwnd as usize);
        let start = Instant::now();
        let mut phase = 0;
        let actions = [
            (500, 1),
            (590, 0),
            (1000, 1),
            (1500, 2),
            (2000, 3),
            (2500, 2),
            (2590, 3),
            (3200, 0),
        ];
        let mut changes = Vec::new();
        let mut previous_height = 460;
        while start.elapsed() < Duration::from_secs(5) {
            let mut message = MSG::default();
            while PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) != 0 {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            EVENTS.with(|events| events.borrow_mut().clear());
            let elapsed = start.elapsed();
            if let Some(&(at, action)) = actions.get(phase) {
                if elapsed.as_millis() >= at {
                    phase += 1;
                    if action < 2 {
                        SendMessageW(tabs, TCM_SETCURSEL, action, 0);
                        InvalidateRect(tabs, null(), 0);
                    } else {
                        let collapsed = action == 2;
                        let rect = RECT {
                            left: 120,
                            top: 120,
                            right: 864,
                            bottom: 120 + if collapsed { 48 } else { 460 },
                        };
                        resize(hwnd, rect, collapsed);
                        SendMessageW(switch, BM_SETCHECK, usize::from(!collapsed), 0);
                    }
                }
            }
            let mut bounds = RECT::default();
            GetWindowRect(hwnd, &mut bounds);
            let height = bounds.bottom - bounds.top;
            if height != previous_height {
                changes.push((elapsed.as_micros(), height));
                previous_height = height;
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        println!("Panel frame samples (microseconds, height): {changes:?}");
        DestroyWindow(hwnd);
        shutdown();
        assert_eq!(active_count(), 0);
    }
}
