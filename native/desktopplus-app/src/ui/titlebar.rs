//! Workspace caption: themed, accessible buttons with native window commands.
use super::*;

pub(super) const HEIGHT: i32 = 48;
const MINIMIZE: u16 = 280;
const MAXIMIZE: u16 = 281;
const CLOSE: u16 = 282;
const BUTTONS: [u16; 3] = [MINIMIZE, MAXIMIZE, CLOSE];
const BUTTON_WIDTH: i32 = 44;

fn command(hwnd: HWND, id: u16) -> u32 {
    match id {
        MINIMIZE => SC_MINIMIZE,
        MAXIMIZE if unsafe { IsZoomed(hwnd) } != 0 => SC_RESTORE,
        MAXIMIZE => SC_MAXIMIZE,
        _ => SC_CLOSE,
    }
}

fn caption(hwnd: HWND, id: u16) -> String {
    // Use the system's localized window action names for accessibility/tooltips.
    let mut buffer = [0u16; 128];
    let length = unsafe {
        GetMenuStringW(
            GetSystemMenu(hwnd, 0),
            command(hwnd, id),
            buffer.as_mut_ptr(),
            buffer.len() as i32,
            MF_BYCOMMAND,
        )
    };
    let label = String::from_utf16_lossy(&buffer[..length.max(0) as usize]);
    label
        .split('\t')
        .next()
        .unwrap_or_default()
        .replace('&', "")
}

pub(super) fn create(hwnd: HWND) {
    for id in BUTTONS {
        button(hwnd, &caption(hwnd, id), id);
    }
    chrome::tooltips(hwnd, &BUTTONS);
    // Recalculate once after creation, before the workspace is shown.
    unsafe {
        SetWindowPos(
            hwnd,
            null_mut(),
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
        );
    }
}

pub(super) fn layout(hwnd: HWND, width: i32) {
    for (index, id) in BUTTONS.into_iter().enumerate() {
        move_control(
            hwnd,
            id as i32,
            width - 8 - (3 - index as i32) * BUTTON_WIDTH,
            8,
            BUTTON_WIDTH,
            HEIGHT - 16,
        );
        let button = control(hwnd, id as i32);
        if !button.is_null() {
            let label = caption(hwnd, id);
            if text(button) != label {
                set_text(button, &label);
            }
            unsafe { InvalidateRect(button, null(), 0) };
        }
    }
}

pub(super) fn paint(hwnd: HWND, dc: HDC, bounds: RECT) {
    let s = |value| chrome::scale(hwnd, value);
    with_palette(hwnd, |p| {
        chrome::fill(
            dc,
            &RECT {
                bottom: s(HEIGHT),
                ..bounds
            },
            p.surface,
        );
        chrome::fill(
            dc,
            &RECT {
                top: s(HEIGHT) - 1,
                bottom: s(HEIGHT),
                ..bounds
            },
            p.border,
        );
        let icon = RECT {
            left: s(20),
            top: s(10),
            right: s(48),
            bottom: s(38),
        };
        chrome::rounded(dc, &icon, s(10), p.accent, p.accent);
        chrome::label(
            dc,
            "\u{e8a9}",
            icon,
            chrome::font(hwnd, 3),
            p.background,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE,
        );
        chrome::label(
            dc,
            "DesktopPlus",
            RECT {
                left: s(58),
                top: 0,
                right: bounds.right - s(3 * BUTTON_WIDTH + 24),
                bottom: s(HEIGHT),
            },
            chrome::font(hwnd, 1),
            p.text,
            DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
        );
    });
}

fn draw_button(draw: &DRAWITEMSTRUCT) {
    let hwnd = unsafe { GetParent(draw.hwndItem) };
    let id = draw.CtlID as u16;
    let hot = chrome::is_hot(draw.hwndItem);
    let pressed = draw.itemState & ODS_SELECTED != 0;
    let focused = chrome::keyboard_focus(draw.hwndItem);
    with_palette(hwnd, |p| {
        chrome::fill(draw.hDC, &draw.rcItem, p.surface);
        let background = if id == CLOSE && (hot || pressed) {
            color(if pressed { "#B92B3A" } else { "#D93646" }, 0)
        } else if pressed {
            p.selected
        } else if hot {
            p.hover
        } else {
            p.surface
        };
        let background = motion::color(draw.hwndItem, 0, background);
        chrome::rounded(
            draw.hDC,
            &draw.rcItem,
            chrome::scale(hwnd, 8),
            background,
            if focused { p.accent } else { background },
        );
        let glyph = match id {
            MINIMIZE => "\u{e921}",
            MAXIMIZE if unsafe { IsZoomed(hwnd) } != 0 => "\u{e923}",
            MAXIMIZE => "\u{e922}",
            _ => "\u{e8bb}",
        };
        chrome::label(
            draw.hDC,
            glyph,
            draw.rcItem,
            chrome::font(hwnd, 6),
            if id == CLOSE && (hot || pressed) {
                color("#FFFFFF", 0)
            } else {
                p.text
            },
            DT_CENTER | DT_VCENTER | DT_SINGLELINE,
        );
    });
}

fn hit_test(bounds: RECT, point: POINT, edge: i32, caption_height: i32) -> u32 {
    if point.x < 0 || point.y < 0 || point.x >= bounds.right || point.y >= bounds.bottom {
        return HTNOWHERE;
    }
    let left = point.x < edge;
    let right = point.x >= bounds.right - edge;
    let top = point.y < edge;
    let bottom = point.y >= bounds.bottom - edge;
    match (left, right, top, bottom) {
        (true, _, true, _) => HTTOPLEFT,
        (_, true, true, _) => HTTOPRIGHT,
        (true, _, _, true) => HTBOTTOMLEFT,
        (_, true, _, true) => HTBOTTOMRIGHT,
        (true, _, _, _) => HTLEFT,
        (_, true, _, _) => HTRIGHT,
        (_, _, true, _) => HTTOP,
        (_, _, _, true) => HTBOTTOM,
        _ if point.y < caption_height => HTCAPTION,
        _ => HTCLIENT,
    }
}

fn maximized_client(bounds: &mut RECT, work: RECT) {
    bounds.left = bounds.left.max(work.left);
    bounds.top = bounds.top.max(work.top);
    bounds.right = bounds.right.min(work.right);
    bounds.bottom = bounds.bottom.min(work.bottom);
}

pub(super) fn message(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> Option<LRESULT> {
    if unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } != 1 {
        return None;
    }
    match message {
        WM_NCCALCSIZE => {
            // RECT is also the first field of NCCALCSIZE_PARAMS (wParam != 0).
            let bounds = unsafe { &mut *(l as *mut RECT) };
            if unsafe { IsZoomed(hwnd) } != 0 {
                let mut monitor = MONITORINFO {
                    cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                    ..Default::default()
                };
                if unsafe {
                    GetMonitorInfoW(
                        MonitorFromRect(bounds, MONITOR_DEFAULTTONEAREST),
                        &mut monitor,
                    )
                } != 0
                {
                    maximized_client(bounds, monitor.rcWork);
                }
            }
            Some(0)
        }
        WM_NCACTIVATE if unsafe { IsIconic(hwnd) } == 0 => {
            Some(unsafe { DefWindowProcW(hwnd, message, w, -1) })
        }
        WM_NCHITTEST => {
            let mut point = POINT {
                x: l as i16 as i32,
                y: (l >> 16) as i16 as i32,
            };
            let mut bounds = RECT::default();
            unsafe {
                ScreenToClient(hwnd, &mut point);
                GetClientRect(hwnd, &mut bounds);
            }
            let edge = if unsafe { IsZoomed(hwnd) } != 0 {
                0
            } else {
                chrome::scale(hwnd, 8)
            };
            let hit = hit_test(bounds, point, edge, chrome::scale(hwnd, HEIGHT));
            // Child buttons must receive clicks rather than start a caption drag.
            let child = unsafe {
                ChildWindowFromPointEx(hwnd, point, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED)
            };
            if hit == HTCAPTION
                && !child.is_null()
                && child != hwnd
                && BUTTONS.contains(&(unsafe { GetDlgCtrlID(child) } as u16))
            {
                Some(HTCLIENT as isize)
            } else {
                Some(hit as isize)
            }
        }
        WM_NCRBUTTONUP if w == HTCAPTION as usize => {
            unsafe {
                let selected = TrackPopupMenu(
                    GetSystemMenu(hwnd, 0),
                    TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    l as i16 as i32,
                    (l >> 16) as i16 as i32,
                    0,
                    hwnd,
                    null(),
                );
                if selected != 0 {
                    PostMessageW(hwnd, WM_SYSCOMMAND, selected as usize, 0);
                }
            }
            Some(0)
        }
        WM_COMMAND if BUTTONS.contains(&(w as u16)) && w >> 16 == BN_CLICKED as usize => {
            unsafe { PostMessageW(hwnd, WM_SYSCOMMAND, command(hwnd, w as u16) as usize, 0) };
            Some(0)
        }
        WM_DRAWITEM if l != 0 => {
            let draw = unsafe { &*(l as *const DRAWITEMSTRUCT) };
            if draw.CtlType == ODT_BUTTON && BUTTONS.contains(&(draw.CtlID as u16)) {
                chrome::draw_item(draw, draw_button);
                Some(1)
            } else {
                None
            }
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_caption_buttons_receive_clicks_and_keep_system_commands() {
        unsafe {
            let class = wide(format!(
                "DesktopPlus.TitlebarTest.{}",
                crate::model::new_id()
            ));
            let instance = GetModuleHandleW(null());
            assert_ne!(
                RegisterClassW(&WNDCLASSW {
                    lpfnWndProc: Some(window_proc),
                    hInstance: instance,
                    lpszClassName: class.as_ptr(),
                    ..std::mem::zeroed()
                }),
                0
            );
            let hwnd = CreateWindowExW(
                0,
                class.as_ptr(),
                wide("DesktopPlus").as_ptr(),
                WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                40,
                40,
                1020,
                720,
                null_mut(),
                null_mut(),
                instance,
                1usize as _,
            );
            assert!(!hwnd.is_null());
            create(hwnd);
            let mut bounds = RECT::default();
            GetClientRect(hwnd, &mut bounds);
            layout(
                hwnd,
                bounds.right * 96 / GetDpiForWindow(hwnd).max(96) as i32,
            );
            let mut outer = RECT::default();
            GetWindowRect(hwnd, &mut outer);
            let mut origin = POINT::default();
            ClientToScreen(hwnd, &mut origin);
            assert_eq!((origin.x, origin.y), (outer.left, outer.top));
            for (id, expected) in [
                (MINIMIZE, SC_MINIMIZE),
                (MAXIMIZE, SC_MAXIMIZE),
                (CLOSE, SC_CLOSE),
            ] {
                let button = control(hwnd, id as i32);
                assert!(!text(button).is_empty());
                let mut area = RECT::default();
                GetWindowRect(button, &mut area);
                let x = (area.left + area.right) / 2;
                let y = (area.top + area.bottom) / 2;
                let point = ((y as u16 as u32) << 16 | x as u16 as u32) as isize;
                assert_eq!(
                    SendMessageW(hwnd, WM_NCHITTEST, 0, point),
                    HTCLIENT as isize
                );
                SendMessageW(button, BM_CLICK, 0, 0);
                let mut message = MSG::default();
                assert_ne!(
                    PeekMessageW(&mut message, hwnd, WM_SYSCOMMAND, WM_SYSCOMMAND, PM_REMOVE),
                    0
                );
                assert_eq!(message.wParam, expected as usize);
            }
            DestroyWindow(hwnd);
            UnregisterClassW(class.as_ptr(), instance);
        }
    }

    #[test]
    fn resize_edges_take_priority_over_dragging_at_each_dpi() {
        for scale in [1, 2, 3] {
            let bounds = RECT {
                right: 820 * scale,
                bottom: 500 * scale,
                ..Default::default()
            };
            for (x, y, expected) in [
                (0, 0, HTTOPLEFT),
                (819, 0, HTTOPRIGHT),
                (0, 499, HTBOTTOMLEFT),
                (819, 499, HTBOTTOMRIGHT),
                (400, 0, HTTOP),
                (400, 499, HTBOTTOM),
                (0, 250, HTLEFT),
                (819, 250, HTRIGHT),
                (400, 24, HTCAPTION),
                (400, 48, HTCLIENT),
                (-1, 24, HTNOWHERE),
                (820, 24, HTNOWHERE),
            ] {
                assert_eq!(
                    hit_test(
                        bounds,
                        POINT {
                            x: x * scale,
                            y: y * scale
                        },
                        8 * scale,
                        HEIGHT * scale
                    ),
                    expected
                );
            }
        }
    }

    #[test]
    fn maximized_window_has_no_resize_targets() {
        let bounds = RECT {
            right: 820,
            bottom: 500,
            ..Default::default()
        };
        assert_eq!(hit_test(bounds, POINT { x: 0, y: 0 }, 0, HEIGHT), HTCAPTION);
        assert_eq!(
            hit_test(bounds, POINT { x: 819, y: 499 }, 0, HEIGHT),
            HTCLIENT
        );
    }

    #[test]
    fn maximized_content_stays_inside_work_area_on_negative_coordinate_monitor() {
        let mut bounds = RECT {
            left: -1928,
            top: -8,
            right: 8,
            bottom: 1088,
        };
        let work = RECT {
            left: -1920,
            top: 40,
            right: 0,
            bottom: 1080,
        };
        maximized_client(&mut bounds, work);
        assert_eq!(
            (bounds.left, bounds.top, bounds.right, bounds.bottom),
            (-1920, 40, 0, 1080)
        );
    }
}
