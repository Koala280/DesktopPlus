//! Keep native hit testing, scrolling and custom painting on the same flexible grid.
use super::*;

thread_local! {
    static UPDATING: RefCell<std::collections::HashSet<usize>> = RefCell::new(Default::default());
    static DRAGGING: RefCell<std::collections::HashSet<usize>> = RefCell::new(Default::default());
    static BOUNDS: RefCell<HashMap<usize, RECT>> = RefCell::new(Default::default());
}

pub(super) fn begin_resize(parent: HWND) {
    if chrome::panel(parent) {
        DRAGGING.with(|dragging| dragging.borrow_mut().insert(parent as usize));
        motion::reset_colors(control(parent, LIST));
    }
}

pub(super) fn end_resize(parent: HWND) {
    if DRAGGING.with(|dragging| dragging.borrow_mut().remove(&(parent as usize))) {
        relayout(parent);
    }
}

pub(super) fn forget(parent: HWND) {
    DRAGGING.with(|dragging| dragging.borrow_mut().remove(&(parent as usize)));
    BOUNDS.with(|bounds| bounds.borrow_mut().remove(&(parent as usize)));
}

pub(super) fn layout(parent: HWND, x: i32, y: i32, width: i32, height: i32) {
    let left = chrome::scale(parent, x);
    let top = chrome::scale(parent, y);
    BOUNDS.with(|bounds| {
        bounds.borrow_mut().insert(
            parent as usize,
            RECT {
                left,
                top,
                right: left + chrome::scale(parent, width.max(0)),
                bottom: top + chrome::scale(parent, height.max(0)),
            },
        )
    });
    relayout(parent);
}

fn centered_inset(width: i32, height: i32, count: i32, minimum: (i32, i32), scrollbar: i32) -> i32 {
    let gutter = width - available_width(width, height, count, minimum, scrollbar);
    // Reserve the scrollbar's space on both sides, then split any pixels left
    // over by integer cell spacing. Move the native control with its contents
    // so painting, selection and drag/drop retain the same coordinates.
    let available = (width - 2 * gutter).max(0);
    let columns = (available / minimum.0).max(1);
    ((width - columns * cell_width(available, minimum.0)) / 2).max(0)
}

fn position(parent: HWND, list: HWND, icons: bool) {
    let Some(mut bounds) = BOUNDS.with(|bounds| bounds.borrow().get(&(parent as usize)).copied())
    else {
        return;
    };
    unsafe {
        if icons {
            bounds.left += centered_inset(
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                SendMessageW(list, LVM_GETITEMCOUNT, 0, 0) as i32,
                images::minimum_cell(parent),
                GetSystemMetricsForDpi(SM_CXVSCROLL, GetDpiForWindow(list).max(96)),
            );
        }
        let mut old = RECT::default();
        GetWindowRect(list, &mut old);
        MapWindowPoints(null_mut(), parent, (&mut old as *mut RECT).cast(), 2);
        if (old.left, old.top, old.right, old.bottom)
            != (bounds.left, bounds.top, bounds.right, bounds.bottom)
        {
            SetWindowPos(
                list,
                null_mut(),
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS | SWP_DEFERERASE,
            );
        }
    }
}

#[derive(Clone, Copy)]
pub(super) struct Geometry {
    pub width: i32,
    pub height: i32,
    pub columns: usize,
}

fn geometry(parent: HWND) -> Option<Geometry> {
    let list = control(parent, LIST);
    let (minimum, height) = images::minimum_cell(parent);
    unsafe {
        let mut bounds = RECT::default();
        GetWindowRect(list, &mut bounds);
        if bounds.right <= bounds.left || bounds.bottom <= bounds.top {
            return None;
        }
        let available = available_width(
            bounds.right - bounds.left,
            bounds.bottom - bounds.top,
            SendMessageW(list, LVM_GETITEMCOUNT, 0, 0) as i32,
            (minimum, height),
            GetSystemMetricsForDpi(SM_CXVSCROLL, GetDpiForWindow(list).max(96)),
        );
        Some(Geometry {
            width: cell_width(available, minimum),
            height,
            columns: (available / minimum).max(1) as usize,
        })
    }
}

pub(super) fn preview(parent: HWND) -> Option<Geometry> {
    if DRAGGING.with(|dragging| dragging.borrow().contains(&(parent as usize))) {
        geometry(parent)
    } else {
        None
    }
}

fn cell_width(available: i32, minimum: i32) -> i32 {
    let columns = (available / minimum).max(1);
    // Short rows keep the same spacing as full rows. In narrow panels a single
    // column can grow by up to 50%, without becoming an oversized card.
    (available / columns).clamp(minimum, minimum * 3 / 2)
}

fn available_width(
    width: i32,
    height: i32,
    count: i32,
    minimum: (i32, i32),
    scrollbar: i32,
) -> i32 {
    let columns = (width / minimum.0).max(1);
    let rows = (i64::from(count) + i64::from(columns) - 1) / i64::from(columns);
    // The virtual ListView reserves this inset while arranging full cells,
    // even when the last row's shorter icon/text bounds hide the scrollbar.
    if rows * i64::from(minimum.1) > i64::from(height) {
        (width - scrollbar).max(0)
    } else {
        width
    }
}

pub(super) fn relayout(parent: HWND) {
    let list = control(parent, LIST);
    if list.is_null() || !chrome::panel(parent) {
        return;
    }
    if !UPDATING.with(|updating| updating.borrow_mut().insert(list as usize)) {
        return;
    }
    let icons = unsafe { SendMessageW(list, LVM_GETVIEW, 0, 0) } == LV_VIEW_ICON as isize;
    position(parent, list, icons);
    if !icons {
        UPDATING.with(|updating| updating.borrow_mut().remove(&(list as usize)));
        return;
    }
    // Native icon-spacing changes remeasure every item label synchronously.
    // While the resize handle owns the pointer, paint the exact final grid
    // directly and synchronize native hit testing once the gesture ends.
    if DRAGGING.with(|dragging| dragging.borrow().contains(&(parent as usize))) {
        unsafe {
            InvalidateRect(list, null(), 0);
        }
        UPDATING.with(|updating| updating.borrow_mut().remove(&(list as usize)));
        return;
    }
    // Measure the borderless list before scrollbars, so an existing scrollbar
    // cannot keep a column from returning when the panel grows. Spacing changes
    // may synchronously send WM_SIZE, which is guarded above.
    unsafe {
        if let Some(Geometry { width, height, .. }) = geometry(parent) {
            if images::cell(parent) != (width, height) {
                motion::reset_colors(list);
                // The native spacing setter otherwise redraws intermediate
                // item positions synchronously. Keep this an atomic layout
                // update and let the next paint present the finished grid.
                let _update = content::Update::new(parent);
                SendMessageW(
                    list,
                    LVM_SETICONSPACING,
                    0,
                    ((height << 16) | width) as isize,
                );
                // LVS_AUTOARRANGE already reflows when spacing changes. A
                // separate LVM_ARRANGE repeats that work on every resize step.
            }
        }
    }
    UPDATING.with(|updating| updating.borrow_mut().remove(&(list as usize)));
}

#[cfg(test)]
mod tests;
