//! Keep virtual rows bound to their source while native view settings change.
use super::*;
use crate::model::Tab;

thread_local! {
    static UPDATING: RefCell<std::collections::HashSet<usize>> = RefCell::new(Default::default());
}

pub(super) fn updating(list: HWND) -> bool {
    UPDATING.with(|updating| updating.borrow().contains(&(list as usize)))
}

pub(super) fn dismiss_selection(parent: HWND, message: u32, w: WPARAM, l: LPARAM) {
    if !matches!(
        message,
        WM_ACTIVATE
            | WM_LBUTTONDOWN
            | WM_RBUTTONDOWN
            | WM_MBUTTONDOWN
            | WM_NCLBUTTONDOWN
            | WM_NCRBUTTONDOWN
            | WM_NCMBUTTONDOWN
            | WM_PARENTNOTIFY
    ) || !chrome::panel(parent)
    {
        return;
    }
    let list = control(parent, LIST);
    if list.is_null() {
        return;
    }
    let dismiss = match message {
        WM_ACTIVATE => w & 0xffff == WA_INACTIVE as usize,
        WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN | WM_NCLBUTTONDOWN | WM_NCRBUTTONDOWN
        | WM_NCMBUTTONDOWN => true,
        WM_PARENTNOTIFY
            if matches!(
                w as u16 as u32,
                WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN
            ) =>
        {
            // Child clicks do not reach the panel's button handlers. Keep
            // native item selection and scrolling intact inside the list.
            let child = unsafe {
                ChildWindowFromPointEx(
                    parent,
                    POINT {
                        x: l as i16 as i32,
                        y: (l >> 16) as i16 as i32,
                    },
                    CWP_SKIPINVISIBLE,
                )
            };
            child != list && unsafe { IsChild(list, child) } == 0
        }
        _ => false,
    };
    if !dismiss {
        return;
    }
    // Clear the actual selection as well as its custom-painted highlight, so
    // a later keyboard command cannot operate on an apparently unselected row.
    let state = LVITEMW {
        stateMask: LVIS_SELECTED | LVIS_FOCUSED,
        ..unsafe { std::mem::zeroed() }
    };
    unsafe {
        SendMessageW(
            list,
            LVM_SETITEMSTATE,
            usize::MAX,
            &state as *const _ as isize,
        );
        SendMessageW(list, LVM_SETSELECTIONMARK, 0, -1);
        RemovePropW(list, wide("DPRowHot").as_ptr());
    }
    motion::reset_colors(list);
    unsafe { InvalidateRect(list, null(), 0) };
}

#[derive(PartialEq, Eq)]
pub(super) struct Source {
    tab: String,
    folder: String,
    kind: String,
    view: String,
}

pub(super) struct Update {
    list: HWND,
    redraw: bool,
    outer: bool,
}

impl Update {
    pub(super) fn new(parent: HWND) -> Self {
        let list = control(parent, LIST);
        // Common controls need not remove WS_VISIBLE for WM_SETREDRAW. Track
        // nesting explicitly so layout cannot re-enable an outer update.
        let outer = UPDATING.with(|updating| updating.borrow_mut().insert(list as usize));
        let redraw =
            outer && unsafe { GetWindowLongPtrW(list, GWL_STYLE) as u32 & WS_VISIBLE != 0 };
        if redraw {
            unsafe { SendMessageW(list, WM_SETREDRAW, 0, 0) };
        }
        Self {
            list,
            redraw,
            outer,
        }
    }
}

impl Drop for Update {
    fn drop(&mut self) {
        // Re-enabling redraw flushes native deferred arrangement and can call
        // custom draw synchronously. Keep the transaction guarded until that
        // work is complete, then invalidate the finished content once.
        if self.redraw {
            unsafe { SendMessageW(self.list, WM_SETREDRAW, 1, 0) };
        }
        if self.outer {
            UPDATING.with(|updating| updating.borrow_mut().remove(&(self.list as usize)));
        }
        if self.redraw {
            unsafe {
                RedrawWindow(
                    self.list,
                    null(),
                    null_mut(),
                    RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN | RDW_NOERASE,
                );
            }
        }
    }
}

pub(super) fn prepare(parent: HWND, tab: &Tab) -> bool {
    let source = Source {
        tab: tab.tab_id.clone(),
        folder: tab.folder_path.clone(),
        kind: tab.panel_type.clone(),
        view: tab.view_mode.clone(),
    };
    let changed = VIEWS.with(|views| {
        let mut views = views.borrow_mut();
        let Some(view) = views.get_mut(&(parent as usize)) else {
            return false;
        };
        if view.source.as_ref() == Some(&source) {
            return false;
        }
        view.source = Some(source);
        // Clear the backing rows before native messages can request an image
        // or synchronously repaint using the destination tab's image size.
        view.rows = Arc::default();
        view.loaded = false;
        true
    });
    if changed {
        let _update = Update::new(parent);
        let list = control(parent, LIST);
        motion::reset_colors(list);
        unsafe {
            RemovePropW(list, wide("DPRowHot").as_ptr());
            SendMessageW(list, LVM_SETITEMCOUNT, 0, 0);
        }
    }
    changed
}

#[cfg(test)]
mod tests;
