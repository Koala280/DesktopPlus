use super::*;

pub(super) fn header_view(style: HeaderStyle) -> ViewData {
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
            header_style: style,
            ..Default::default()
        },
        hover_tracking: false,
        zoom: 1.0,
    }
}

#[test]
fn every_header_style_keeps_actions_and_content_clear_of_tabs() {
    let hwnd = 17usize as HWND;
    for style in HeaderStyle::ALL {
        let mut view = header_view(style);
        view.options.movement = "button".into();
        view.options.empty_button = true;
        view.options.search = "button".into();
        view.options.settings = "always".into();
        VIEWS.with(|views| views.borrow_mut().insert(hwnd as usize, view));
        for collapsed in [false, true] {
            VIEWS.with(|views| {
                views
                    .borrow_mut()
                    .get_mut(&(hwnd as usize))
                    .unwrap()
                    .collapsed = collapsed
            });
            for width in [220, 249, 300, 440, 560, 744, 1000] {
                let layout = header_layout(hwnd, width);
                assert_eq!(layout.height, style.height(collapsed));
                assert!(layout.identity_top + layout.identity_height <= layout.height);
                assert!(layout.button_top + 28 <= layout.height);
                assert!(
                    layout.identity_left >= 0
                        && layout.identity_left + layout.identity_width <= width
                );
                if width >= 744 {
                    assert!((2 * layout.identity_left + layout.identity_width - width).abs() <= 1);
                }
                let mut buttons: Vec<_> = [
                    layout.settings,
                    layout.movement,
                    layout.up,
                    layout.empty,
                    layout.close,
                    layout.collapse,
                    layout.search_button,
                ]
                .into_iter()
                .flatten()
                .collect();
                buttons.sort_unstable();
                assert!(
                    buttons.iter().all(|x| *x >= 0 && x + 26 <= width),
                    "{style:?} / {width}"
                );
                assert!(
                    buttons.windows(2).all(|pair| pair[0] + 26 <= pair[1]),
                    "{style:?} / {width}: {buttons:?}"
                );
                if !(style == HeaderStyle::TwoRows && !collapsed) && layout.identity_width > 0 {
                    assert!(
                        buttons.iter().all(|x| x + 26 <= layout.identity_left
                            || *x >= layout.identity_left + layout.identity_width),
                        "{style:?} / {width}"
                    );
                }
            }
        }
        VIEWS.with(|views| views.borrow_mut().remove(&(hwnd as usize)));
    }
}

#[test]
fn centered_tabs_use_the_panel_center_with_asymmetric_toolbars() {
    for (width, left, right, count) in [(744, 72, 630, 2), (584, 42, 440, 2), (900, 108, 740, 3)] {
        let (x, extent) = identity_bounds(width, left, right, count, "center");
        assert!((2 * x + extent - width).abs() <= 1);
        assert!(x >= left && x + extent <= right);
        assert!(extent <= count as i32 * 156 + 6);
    }
}

#[test]
fn title_and_tabs_stay_inside_toolbar_gap_at_narrow_widths() {
    for width in [220, 249, 300, 440, 744] {
        for alignment in ["left", "center", "right"] {
            for count in [1, 2, 8] {
                let (x, extent) = identity_bounds(width, 72, width - 106, count, alignment);
                assert!(extent >= 0 && x >= 72 && x + extent <= width - 106);
                if alignment == "left" {
                    assert_eq!(x, 72);
                }
                if alignment == "right" {
                    assert_eq!(x + extent, width - 106);
                }
            }
        }
    }
}

#[test]
fn centered_tab_group_does_not_shift_when_header_buttons_hide() {
    assert_eq!(
        identity_bounds(744, 72, 630, 2, "center"),
        identity_bounds(744, 44, 694, 2, "center")
    );
}

#[test]
fn centered_title_shrinks_continuously_as_search_takes_its_space() {
    for width in [220, 440, 584, 744] {
        for count in [1, 2, 8] {
            let mut previous = identity_bounds(width, 44, width - 32, count, "center");
            for right in (44..width - 32).rev() {
                let current = identity_bounds(width, 44, right, count, "center");
                assert!(
                    (current.0 - previous.0).abs() <= 2,
                    "title jumped: {previous:?} -> {current:?}"
                );
                assert!(
                    (current.1 - previous.1).abs() <= 2,
                    "title width jumped: {previous:?} -> {current:?}"
                );
                assert!(current.0 >= 44 && current.0 + current.1 <= right);
                previous = current;
            }
        }
    }
}

#[test]
fn search_shifts_tabs_into_available_space_before_truncating_them() {
    let (left, width) = identity_bounds(584, 44, 304, 2, "center");
    assert_eq!((left, width), (44, 260));
}

struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    previous: HGDIOBJ,
}
impl Surface {
    fn new() -> Self {
        unsafe {
            let dc = CreateCompatibleDC(null_mut());
            assert!(!dc.is_null());
            let info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: 48,
                    biHeight: -48,
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
            let previous = SelectObject(dc, bitmap);
            fill(
                dc,
                &RECT {
                    left: 0,
                    top: 0,
                    right: 48,
                    bottom: 48,
                },
                0x123456,
            );
            Self {
                dc,
                bitmap,
                previous,
            }
        }
    }
    fn pixel(&self, x: i32, y: i32) -> u32 {
        unsafe { GetPixel(self.dc, x, y) }
    }
}
impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            SelectObject(self.dc, self.previous);
            DeleteObject(self.bitmap);
            DeleteDC(self.dc);
        }
    }
}

#[test]
fn owner_draw_presents_background_and_icon_together_at_item_coordinates() {
    let surface = Surface::new();
    let item = DRAWITEMSTRUCT {
        hDC: surface.dc,
        rcItem: RECT {
            left: 8,
            top: 12,
            right: 40,
            bottom: 44,
        },
        CtlType: ODT_BUTTON,
        itemState: ODS_SELECTED,
        ..Default::default()
    };
    draw_item(&item, |frame| {
        assert_eq!(frame.itemState, ODS_SELECTED);
        fill(frame.hDC, &frame.rcItem, 0x202020);
        unsafe {
            GdiFlush();
        }
        // The previous icon must survive until the new foreground is ready.
        assert_eq!(surface.pixel(20, 24), 0x123456);
        fill(
            frame.hDC,
            &RECT {
                left: 16,
                top: 20,
                right: 24,
                bottom: 28,
            },
            0x00ff00,
        );
        unsafe {
            GdiFlush();
        }
        assert_eq!(surface.pixel(20, 24), 0x123456);
    });
    assert_eq!(surface.pixel(20, 24), 0x00ff00);
    assert_eq!(surface.pixel(8, 12), 0x202020);
    assert_eq!(surface.pixel(39, 43), 0x202020);
    assert_eq!(surface.pixel(7, 12), 0x123456);
    assert_eq!(surface.pixel(40, 44), 0x123456);
}

#[test]
fn buffered_presentation_respects_destination_clip_and_preserves_dc_origin() {
    let surface = Surface::new();
    unsafe {
        IntersectClipRect(surface.dc, 16, 20, 24, 28);
    }
    buffered(
        surface.dc,
        RECT {
            left: 8,
            top: 12,
            right: 40,
            bottom: 44,
        },
        |dc| {
            fill(
                dc,
                &RECT {
                    left: 8,
                    top: 12,
                    right: 40,
                    bottom: 44,
                },
                0x00ff00,
            );
        },
    );
    unsafe {
        SelectClipRgn(surface.dc, null_mut());
    }
    assert_eq!(surface.pixel(20, 24), 0x00ff00);
    assert_eq!(surface.pixel(8, 12), 0x123456);
    let mut origin = POINT::default();
    unsafe {
        GetWindowOrgEx(surface.dc, &mut origin);
    }
    assert_eq!((origin.x, origin.y), (0, 0));
}

#[test]
fn owner_draw_erase_keeps_the_previous_frame_visible() {
    let surface = Surface::new();
    // This message is handled before consulting native button state.
    let handled = unsafe { button_proc(null_mut(), WM_ERASEBKGND, surface.dc as usize, 0, 2, 0) };
    assert_eq!(handled, 1);
    assert_eq!(surface.pixel(20, 24), 0x123456);
}

#[test]
fn reused_buffers_reset_clipping_and_do_not_leave_stale_pixels() {
    let surface = Surface::new();
    for (rect, color) in [
        (
            RECT {
                left: 8,
                top: 12,
                right: 24,
                bottom: 28,
            },
            0xff0000,
        ),
        (
            RECT {
                left: 16,
                top: 20,
                right: 40,
                bottom: 44,
            },
            0x00ff00,
        ),
    ] {
        buffered(surface.dc, rect, |dc| fill(dc, &rect, color));
        assert_eq!(surface.pixel(rect.left, rect.top), color);
        assert_eq!(surface.pixel(rect.right - 1, rect.bottom - 1), color);
    }
    assert_eq!(surface.pixel(8, 12), 0xff0000);
    assert_eq!(surface.pixel(40, 44), 0x123456);
}

#[test]
fn fractional_thumb_positions_produce_distinct_antialiased_frames() {
    let first = Surface::new();
    let next = Surface::new();
    let knob = RECT {
        left: 8,
        top: 8,
        right: 28,
        bottom: 28,
    };
    rounded_offset(first.dc, &knob, 20, 0xffffff, 0xffffff, (0.0, 0.0));
    rounded_offset(next.dc, &knob, 20, 0xffffff, 0xffffff, (0.4, 0.0));
    assert!(
        (6..31).any(|x| (6..31).any(|y| first.pixel(x, y) != next.pixel(x, y))),
        "subpixel motion was rounded back to the same image"
    );
    assert_eq!(next.pixel(18, 18), 0xffffff);
    assert_eq!(next.pixel(5, 5), 0x123456);
}

#[test]
fn rounded_control_has_smooth_corners_and_crisp_inset_border() {
    let surface = Surface::new();
    let rect = RECT {
        left: 4,
        top: 8,
        right: 44,
        bottom: 40,
    };
    rounded(surface.dc, &rect, 12, 0x202020, 0xffffff);
    for (x, y) in [(24, 8), (24, 39), (4, 24), (43, 24)] {
        assert_eq!(surface.pixel(x, y), 0xffffff, "border at {x}, {y}");
    }
    for (x, y) in [(24, 9), (24, 38), (5, 24), (42, 24)] {
        assert_eq!(surface.pixel(x, y), 0x202020, "fill at {x}, {y}");
    }
    assert_eq!(surface.pixel(4, 8), 0x123456);
    assert_eq!(surface.pixel(3, 24), 0x123456);
    assert_eq!(surface.pixel(44, 24), 0x123456);
    assert!(
        (4..10)
            .any(|x| (8..14)
                .any(|y| { ![0x123456, 0xffffff, 0x202020].contains(&surface.pixel(x, y)) })),
        "curved edges need partial pixel coverage"
    );
}

#[test]
fn rounded_controls_keep_owner_draw_coordinates_and_clip() {
    let surface = Surface::new();
    unsafe {
        IntersectClipRect(surface.dc, 16, 20, 32, 36);
    }
    buffered(
        surface.dc,
        RECT {
            left: 8,
            top: 12,
            right: 40,
            bottom: 44,
        },
        |dc| {
            fill(
                dc,
                &RECT {
                    left: 8,
                    top: 12,
                    right: 40,
                    bottom: 44,
                },
                0x202020,
            );
            rounded(
                dc,
                &RECT {
                    left: 12,
                    top: 16,
                    right: 36,
                    bottom: 40,
                },
                12,
                0x0000ff,
                0x0000ff,
            );
        },
    );
    unsafe {
        SelectClipRgn(surface.dc, null_mut());
    }
    assert_eq!(surface.pixel(16, 28), 0x0000ff);
    assert_eq!(surface.pixel(31, 28), 0x0000ff);
    assert_eq!(surface.pixel(24, 20), 0x0000ff);
    assert_eq!(surface.pixel(24, 35), 0x0000ff);
    assert_eq!(surface.pixel(15, 28), 0x123456);
    assert_eq!(surface.pixel(32, 28), 0x123456);
}
