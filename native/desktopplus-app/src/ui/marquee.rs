//! Selection for the buffered icon grid, including pointer capture outside it.
use super::*;
use std::{collections::HashSet, rc::Rc};
use windows_sys::Win32::System::SystemServices::{MK_LBUTTON, MK_SHIFT};

const SCROLL_TIMER: usize = 0x44505345;

#[derive(Clone)]
struct Drag {
    // Anchor in content coordinates; the pointer remains in client coordinates.
    anchor: POINT,
    start: POINT,
    pointer: POINT,
    initial: Rc<HashSet<usize>>,
    hits: Rc<HashSet<usize>>,
    ctrl: bool,
    shift: bool,
    active: bool,
}

thread_local! {
    static DRAGS: RefCell<HashMap<usize, Drag>> = RefCell::new(HashMap::new());
}

fn point(lparam: LPARAM) -> POINT {
    POINT {
        x: lparam as i16 as i32,
        y: (lparam >> 16) as i16 as i32,
    }
}

fn origin(list: HWND) -> POINT {
    let mut origin = POINT::default();
    unsafe { SendMessageW(list, LVM_GETORIGIN, 0, &mut origin as *mut _ as isize) };
    origin
}

fn bounds(list: HWND, drag: &Drag) -> RECT {
    let origin = origin(list);
    let mut client = RECT::default();
    unsafe { GetClientRect(list, &mut client) };
    // Keep selecting at the edge without selecting unseen rows beyond it.
    let end = POINT {
        x: drag.pointer.x.clamp(client.left, client.right),
        y: drag.pointer.y.clamp(client.top, client.bottom),
    };
    let start = POINT {
        x: drag.anchor.x - origin.x,
        y: drag.anchor.y - origin.y,
    };
    RECT {
        left: start.x.min(end.x),
        top: start.y.min(end.y),
        right: start.x.max(end.x),
        bottom: start.y.max(end.y),
    }
}

pub(super) fn rectangle(list: HWND) -> Option<RECT> {
    let drag = DRAGS.with(|drags| drags.borrow().get(&(list as usize)).cloned())?;
    drag.active.then(|| bounds(list, &drag))
}

fn set_selected(list: HWND, index: usize, selected: bool) {
    let item = LVITEMW {
        stateMask: LVIS_SELECTED,
        state: if selected { LVIS_SELECTED } else { 0 },
        ..unsafe { std::mem::zeroed() }
    };
    unsafe { SendMessageW(list, LVM_SETITEMSTATE, index, &item as *const _ as isize) };
}

fn intersect(a: RECT, b: RECT) -> bool {
    a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top
}

fn hit_items(list: HWND, parent: HWND, area: RECT) -> HashSet<usize> {
    let mut hits = HashSet::new();
    if area.right <= area.left || area.bottom <= area.top {
        return hits;
    }
    let count = unsafe { SendMessageW(list, LVM_GETITEMCOUNT, 0, 0) } as usize;
    if count == 0 {
        return hits;
    }
    let item_rect = |index| {
        let mut rect = RECT {
            left: LVIR_BOUNDS as i32,
            ..Default::default()
        };
        unsafe { SendMessageW(list, LVM_GETITEMRECT, index, &mut rect as *mut _ as isize) };
        rect
    };
    let first = item_rect(0);
    let (width, height) = images::cell(parent);
    let mut columns = 1;
    // Match the native arrangement used by the buffered painter. Limit work to
    // rows touched by the rectangle, even in very large virtual folders.
    for index in 1..count.min(512) {
        if item_rect(index).top != first.top {
            break;
        }
        columns += 1;
    }
    let start = ((area.top - first.top).max(0) / height) as usize * columns;
    let end = (((area.bottom - first.top).max(0) / height + 1) as usize * columns).min(count);
    for index in start..end {
        let left = first.left + (index % columns) as i32 * width;
        let top = first.top + (index / columns) as i32 * height;
        let mut card = RECT {
            left,
            top,
            right: left + width,
            bottom: top + height,
        };
        unsafe {
            InflateRect(
                &mut card,
                -chrome::scale(parent, 3),
                -chrome::scale(parent, 2),
            )
        };
        if intersect(area, card) {
            hits.insert(index);
        }
    }
    hits
}

pub(super) fn refresh(list: HWND, parent: HWND) {
    let Some(drag) = DRAGS.with(|drags| drags.borrow().get(&(list as usize)).cloned()) else {
        return;
    };
    if !drag.active {
        return;
    }
    let hits = hit_items(list, parent, bounds(list, &drag));
    // Only touched rows change. Ctrl toggles against the selection at mouse-down;
    // Shift adds to it, and shrinking the rectangle restores the initial state.
    for &index in drag.hits.symmetric_difference(&hits) {
        let inside = hits.contains(&index);
        let initial = drag.initial.contains(&index);
        let selected = if drag.ctrl {
            initial ^ inside
        } else {
            inside || (drag.shift && initial)
        };
        set_selected(list, index, selected);
    }
    DRAGS.with(|drags| {
        if let Some(drag) = drags.borrow_mut().get_mut(&(list as usize)) {
            drag.hits = Rc::new(hits);
        }
    });
    unsafe { InvalidateRect(list, null(), 0) };
}

fn finish(list: HWND, restore: bool) {
    let drag = DRAGS.with(|drags| drags.borrow_mut().remove(&(list as usize)));
    if let Some(drag) = drag {
        if restore {
            set_selected(list, usize::MAX, false);
            for &index in drag.initial.iter() {
                set_selected(list, index, true);
            }
        }
        unsafe {
            KillTimer(list, SCROLL_TIMER);
            if GetCapture() == list {
                ReleaseCapture();
            }
            InvalidateRect(list, null(), 0);
        }
    }
}

pub(super) fn message(
    list: HWND,
    parent: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
) -> Option<LRESULT> {
    if matches!(
        message,
        WM_CANCELMODE
            | WM_CAPTURECHANGED
            | WM_NCDESTROY
            | LVM_SETITEMCOUNT
            | LVM_SETVIEW
            | LVM_SETICONSPACING
    ) {
        finish(list, false);
        return None;
    }
    let dragging = DRAGS.with(|drags| drags.borrow().contains_key(&(list as usize)));
    if dragging {
        match message {
            WM_MOUSEMOVE | WM_LBUTTONUP => {
                if message == WM_MOUSEMOVE && w & MK_LBUTTON as usize == 0 {
                    finish(list, false);
                    return Some(0);
                }
                let pointer = point(l);
                DRAGS.with(|drags| {
                    if let Some(drag) = drags.borrow_mut().get_mut(&(list as usize)) {
                        drag.pointer = pointer;
                        drag.active |= (pointer.x - drag.start.x).abs()
                            >= unsafe { GetSystemMetrics(SM_CXDRAG) }
                            || (pointer.y - drag.start.y).abs()
                                >= unsafe { GetSystemMetrics(SM_CYDRAG) };
                    }
                });
                refresh(list, parent);
                if message == WM_LBUTTONUP {
                    finish(list, false);
                }
                return Some(0);
            }
            WM_KEYDOWN if w == VK_ESCAPE as usize => {
                finish(list, true);
                return Some(0);
            }
            WM_KEYDOWN => return Some(0),
            WM_TIMER if w == SCROLL_TIMER => {
                let drag = DRAGS
                    .with(|drags| drags.borrow().get(&(list as usize)).cloned())
                    .unwrap();
                if drag.active {
                    let mut client = RECT::default();
                    unsafe { GetClientRect(list, &mut client) };
                    let step = (images::cell(parent).1 / 3).max(1);
                    let dy = if drag.pointer.y < client.top {
                        -step
                    } else if drag.pointer.y >= client.bottom {
                        step
                    } else {
                        0
                    };
                    if dy != 0 {
                        unsafe { SendMessageW(list, LVM_SCROLL, 0, dy as isize) };
                    }
                }
                return Some(0);
            }
            // Leaving the panel only clips the drawing; capture owns the gesture
            // until release (and prevents hover-collapse during selection).
            WM_MOUSELEAVE => return Some(0),
            _ => {}
        }
    }
    if message != WM_LBUTTONDOWN
        || !chrome::panel(parent)
        || unsafe { SendMessageW(list, LVM_GETVIEW, 0, 0) } != LV_VIEW_ICON as isize
    {
        return None;
    }
    let pointer = point(l);
    let mut hit = LVHITTESTINFO {
        pt: pointer,
        ..Default::default()
    };
    if unsafe { SendMessageW(list, LVM_HITTEST, 0, &mut hit as *mut _ as isize) } >= 0 {
        // Item clicks and file dragging remain owned by the native control.
        return None;
    }
    let origin = origin(list);
    let ctrl = w & MK_CONTROL as usize != 0;
    let shift = w & MK_SHIFT as usize != 0;
    let initial = Rc::new(selected(parent).into_iter().collect());
    unsafe {
        SetFocus(list);
        SetCapture(list);
    }
    if unsafe { GetCapture() } != list {
        return None;
    }
    DRAGS.with(|drags| {
        drags.borrow_mut().insert(
            list as usize,
            Drag {
                anchor: POINT {
                    x: pointer.x + origin.x,
                    y: pointer.y + origin.y,
                },
                start: pointer,
                pointer,
                initial,
                hits: Rc::default(),
                ctrl,
                shift,
                active: false,
            },
        );
    });
    if !ctrl && !shift {
        set_selected(list, usize::MAX, false);
    }
    unsafe {
        RemovePropW(list, wide("DPRowHot").as_ptr());
        SetTimer(list, SCROLL_TIMER, 50, None);
        InvalidateRect(list, null(), 0);
    }
    Some(0)
}
