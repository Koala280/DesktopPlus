//! Native control semantics with consistent, event-driven dark painting.
use super::*;
const HOT: &[u16] = &[68, 80, 67, 116, 108, 72, 111, 116, 0];
pub fn label(hwnd: HWND) {
    unsafe {
        SetWindowSubclass(hwnd, Some(label_proc), 13, 0);
    }
}
unsafe extern "system" fn label_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if matches!(message, WM_PAINT | WM_PRINTCLIENT) {
        let parent = unsafe { GetParent(hwnd) };
        chrome::paint(
            hwnd,
            (message == WM_PRINTCLIENT).then_some(w as HDC),
            |dc, bounds| {
                with_palette(parent, |p| {
                    chrome::fill(dc, &bounds, p.background);
                    let font = unsafe { SendMessageW(hwnd, WM_GETFONT, 0, 0) } as HFONT;
                    chrome::label(
                        dc,
                        &text(hwnd),
                        bounds,
                        font,
                        if matches!(unsafe { GetDlgCtrlID(hwnd) }, SUBTITLE | SOURCE | STATUS) {
                            p.muted
                        } else {
                            p.text
                        },
                        DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                    );
                })
            },
        );
        return 0;
    }
    if message == WM_ERASEBKGND {
        return 1;
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}
pub fn check(parent: HWND, caption: &str, id: i32, value: bool) -> HWND {
    let hwnd = child(
        parent,
        "BUTTON",
        caption,
        id,
        WS_TABSTOP | BS_AUTOCHECKBOX as u32,
    );
    unsafe {
        SendMessageW(hwnd, BM_SETCHECK, usize::from(value), 0);
        SetWindowSubclass(hwnd, Some(control_proc), 11, 1);
    }
    hwnd
}
pub fn choice(parent: HWND, caption: &str, id: i32, values: &[String], selected: usize) -> HWND {
    let hwnd = child(
        parent,
        "COMBOBOX",
        "",
        id,
        WS_TABSTOP
            | WS_VSCROLL
            | CBS_DROPDOWNLIST as u32
            | CBS_OWNERDRAWFIXED as u32
            | CBS_HASSTRINGS as u32,
    );
    unsafe {
        SetWindowSubclass(hwnd, Some(control_proc), 11, 2);
        for value in values {
            SendMessageW(
                hwnd,
                CB_ADDSTRING,
                0,
                wide(crate::locale::text(value)).as_ptr() as isize,
            );
        }
        SendMessageW(hwnd, CB_SETCURSEL, selected, 0);
        SendMessageW(
            hwnd,
            CB_SETITEMHEIGHT,
            usize::MAX,
            chrome::scale(parent, 36) as isize,
        );
        SendMessageW(
            hwnd,
            CB_SETITEMHEIGHT,
            0,
            chrome::scale(parent, 36) as isize,
        );
        SetWindowTextW(hwnd, wide(crate::locale::text(caption)).as_ptr());
    }
    hwnd
}
pub fn edit(parent: HWND, value: &str, id: i32) -> HWND {
    let hwnd = child(
        parent,
        "EDIT",
        value,
        id,
        WS_TABSTOP | ES_AUTOHSCROLL as u32,
    );
    unsafe {
        SetWindowSubclass(hwnd, Some(control_proc), 11, 3);
    }
    hwnd
}
fn draw(hwnd: HWND, kind: usize, print: Option<HDC>) {
    let parent = unsafe { GetParent(hwnd) };
    chrome::paint(hwnd, print, |dc, bounds| {
        with_palette(parent, |p| {
            let hot = !unsafe { GetPropW(hwnd, HOT.as_ptr()) }.is_null();
            let focus = chrome::keyboard_focus(hwnd);
            let enabled = unsafe { IsWindowEnabled(hwnd) != 0 };
            let ink = if enabled { p.text } else { p.muted };
            chrome::fill(dc, &bounds, p.background);
            let mut area = bounds;
            unsafe {
                InflateRect(&mut area, -1, -1);
            }
            chrome::rounded(
                dc,
                &area,
                chrome::scale(parent, 12),
                motion::color(hwnd, 0, if hot { p.hover } else { p.surface }),
                motion::color(
                    hwnd,
                    1,
                    if focus {
                        p.accent
                    } else if hot {
                        p.muted
                    } else {
                        p.border
                    },
                ),
            );
            if kind == 1 {
                let checked =
                    unsafe { SendMessageW(hwnd, BM_GETCHECK, 0, 0) == BST_CHECKED as isize };
                let s = |v| chrome::scale(parent, v);
                let width = s(36);
                let size = s(20);
                let left = bounds.right - s(50);
                let top = (bounds.bottom - size) / 2;
                let track = RECT {
                    left,
                    top,
                    right: left + width,
                    bottom: top + size,
                };
                chrome::rounded(
                    dc,
                    &track,
                    size,
                    motion::color(hwnd, 2, if checked { p.accent } else { p.border }),
                    motion::color(hwnd, 3, if checked { p.accent } else { p.muted }),
                );
                let offset =
                    motion::scalar(hwnd, 0, if checked { (width - size) as f64 } else { 0.0 });
                let knob = RECT {
                    left: left + s(3),
                    top: top + s(3),
                    right: left + size - s(3),
                    bottom: top + size - s(3),
                };
                let thumb_ink = motion::color(hwnd, 4, if checked { p.background } else { p.text });
                chrome::rounded_offset(dc, &knob, size, thumb_ink, thumb_ink, (offset as f32, 0.0));
                area.left += s(14);
                area.right = left - s(12);
                chrome::label(
                    dc,
                    &text(hwnd),
                    area,
                    chrome::font(parent, 0),
                    ink,
                    DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
            } else {
                area.left += chrome::scale(parent, 12);
                area.right -= chrome::scale(parent, 34);
                let selected = unsafe { SendMessageW(hwnd, CB_GETCURSEL, 0, 0) };
                chrome::label(
                    dc,
                    &choice_text(hwnd, selected),
                    area,
                    chrome::font(parent, 0),
                    ink,
                    DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
                area.left = bounds.right - chrome::scale(parent, 32);
                area.right = bounds.right - chrome::scale(parent, 8);
                chrome::label(
                    dc,
                    "\u{e70d}",
                    area,
                    chrome::font(parent, 3),
                    p.muted,
                    DT_CENTER | DT_VCENTER | DT_SINGLELINE,
                );
            }
        });
    });
}
fn choice_text(hwnd: HWND, index: isize) -> String {
    if index < 0 {
        return String::new();
    }
    unsafe {
        let len = SendMessageW(hwnd, CB_GETLBTEXTLEN, index as usize, 0);
        if !(0..=16384).contains(&len) {
            return String::new();
        }
        let mut value = vec![0u16; len as usize + 1];
        SendMessageW(
            hwnd,
            CB_GETLBTEXT,
            index as usize,
            value.as_mut_ptr() as isize,
        );
        String::from_utf16_lossy(&value[..len as usize])
    }
}
pub fn draw_choice(draw: &DRAWITEMSTRUCT) {
    let parent = unsafe { GetParent(draw.hwndItem) };
    with_palette(parent, |p| {
        chrome::fill(
            draw.hDC,
            &draw.rcItem,
            if draw.itemState & ODS_SELECTED != 0 {
                p.selected
            } else {
                p.surface
            },
        );
        let mut area = draw.rcItem;
        area.left += chrome::scale(parent, 12);
        area.right -= chrome::scale(parent, 10);
        chrome::label(
            draw.hDC,
            &choice_text(draw.hwndItem, draw.itemID as i32 as isize),
            area,
            chrome::font(parent, 0),
            p.text,
            DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
        );
    });
}
unsafe extern "system" fn control_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    kind: usize,
) -> LRESULT {
    if kind != 3 && matches!(message, WM_PAINT | WM_PRINTCLIENT) {
        draw(hwnd, kind, (message == WM_PRINTCLIENT).then_some(w as HDC));
        return 0;
    }
    if kind != 3 && message == WM_ERASEBKGND {
        return 1;
    }
    if message == WM_MOUSEMOVE {
        unsafe {
            if GetPropW(hwnd, HOT.as_ptr()).is_null() {
                SetPropW(hwnd, HOT.as_ptr(), 1usize as _);
                let mut tracking = TRACKMOUSEEVENT {
                    cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                    dwFlags: TME_LEAVE,
                    hwndTrack: hwnd,
                    dwHoverTime: 0,
                };
                TrackMouseEvent(&mut tracking);
                InvalidateRect(hwnd, null(), 0);
            }
        }
    }
    if matches!(message, WM_MOUSELEAVE | WM_NCDESTROY) {
        unsafe {
            RemovePropW(hwnd, HOT.as_ptr());
            InvalidateRect(hwnd, null(), 0);
        }
    }
    let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
    if message == WM_SETFOCUS {
        unsafe {
            PostMessageW(GetParent(hwnd), form::REVEAL_FOCUS, hwnd as usize, 0);
        }
    }
    if matches!(
        message,
        WM_SETFOCUS
            | WM_KILLFOCUS
            | WM_KEYDOWN
            | WM_KEYUP
            | WM_LBUTTONDOWN
            | WM_LBUTTONUP
            | BM_SETCHECK
            | CB_SETCURSEL
            | WM_ENABLE
            | WM_UPDATEUISTATE
    ) {
        unsafe {
            InvalidateRect(hwnd, null(), 0);
            if kind == 3 {
                InvalidateRect(GetParent(hwnd), null(), 0);
            }
        }
    }
    result
}
