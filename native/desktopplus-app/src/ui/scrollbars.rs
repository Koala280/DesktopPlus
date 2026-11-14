//! Paint and track native list scroll ranges with the panel palette.
//! The virtual ListView keeps scrolling, selection and keyboard semantics.
use super::*;
#[derive(Clone, Copy, Default)]
struct State {
    hot: Option<bool>,
    drag: Option<(bool, i32)>,
    form: bool,
}
thread_local! {static STATES: RefCell<HashMap<usize,State>>=RefCell::new(HashMap::new());}
struct Bar {
    vertical: bool,
    area: RECT,
    thumb: RECT,
    position: i32,
    max: i32,
    page: i32,
    origin: POINT,
}
impl Bar {
    fn read(hwnd: HWND, vertical: bool) -> Option<Self> {
        unsafe {
            let mut bar = SCROLLBARINFO {
                cbSize: std::mem::size_of::<SCROLLBARINFO>() as u32,
                ..std::mem::zeroed()
            };
            if GetScrollBarInfo(hwnd, if vertical { -5 } else { -6 }, &mut bar) == 0
                || bar.rgstate[0] & 0x18000 != 0
            {
                return None;
            }
            let mut info = SCROLLINFO {
                cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
                fMask: SIF_ALL,
                ..std::mem::zeroed()
            };
            if GetScrollInfo(hwnd, if vertical { SB_VERT } else { SB_HORZ }, &mut info) == 0 {
                return None;
            }
            let max = (info.nMax - info.nMin + 1 - info.nPage as i32).max(0);
            if max == 0 {
                return None;
            }
            let mut window = RECT::default();
            GetWindowRect(hwnd, &mut window);
            let mut area = bar.rcScrollBar;
            OffsetRect(&mut area, -window.left, -window.top);
            let start = if vertical { area.top } else { area.left } + 2;
            let length = (if vertical {
                area.bottom - area.top
            } else {
                area.right - area.left
            } - 4)
                .max(1);
            let extent = (info.nMax - info.nMin + 1).max(1);
            let thumb_len = (length as i64 * info.nPage as i64 / extent as i64) as i32;
            let thumb_len = thumb_len.max(chrome::scale(hwnd, 24)).min(length);
            let offset =
                ((length - thumb_len) as i64 * (info.nPos - info.nMin) as i64 / max as i64) as i32;
            let thickness = chrome::scale(hwnd, 6);
            let thumb = if vertical {
                let x = (area.left + area.right - thickness) / 2;
                RECT {
                    left: x,
                    top: start + offset,
                    right: x + thickness,
                    bottom: start + offset + thumb_len,
                }
            } else {
                let y = (area.top + area.bottom - thickness) / 2;
                RECT {
                    left: start + offset,
                    top: y,
                    right: start + offset + thumb_len,
                    bottom: y + thickness,
                }
            };
            Some(Self {
                vertical,
                area,
                thumb,
                position: info.nPos - info.nMin,
                max,
                page: info.nPage as i32,
                origin: POINT {
                    x: window.left,
                    y: window.top,
                },
            })
        }
    }
    fn pointer(&self) -> POINT {
        let mut point = POINT::default();
        unsafe {
            GetCursorPos(&mut point);
        }
        point.x -= self.origin.x;
        point.y -= self.origin.y;
        point
    }
    fn coordinate(&self, point: POINT) -> i32 {
        if self.vertical { point.y } else { point.x }
    }
    fn thumb_start(&self) -> i32 {
        if self.vertical {
            self.thumb.top
        } else {
            self.thumb.left
        }
    }
    fn contains(&self, point: POINT) -> bool {
        unsafe { PtInRect(&self.area, point) != 0 }
    }
    fn scroll(&self, hwnd: HWND, target: i32) {
        unsafe {
            if STATES.with(|s| s.borrow().get(&(hwnd as usize)).is_some_and(|s| s.form)) {
                SendMessageW(hwnd, form::SCROLL_TO, target.clamp(0, self.max) as usize, 0);
                return;
            }
            let delta = target.clamp(0, self.max) - self.position;
            if delta == 0 {
                return;
            }
            let details = SendMessageW(hwnd, LVM_GETVIEW, 0, 0) == LV_VIEW_DETAILS as isize;
            let rows = if details && self.vertical {
                let mut r = RECT {
                    left: LVIR_BOUNDS as i32,
                    ..Default::default()
                };
                if SendMessageW(hwnd, LVM_GETITEMRECT, 0, &mut r as *mut RECT as isize) != 0 {
                    (r.bottom - r.top).max(1)
                } else {
                    chrome::content_scale(GetParent(hwnd), 30)
                }
            } else {
                1
            };
            SendMessageW(
                hwnd,
                LVM_SCROLL,
                if self.vertical { 0 } else { delta as usize },
                if self.vertical {
                    (delta * rows) as isize
                } else {
                    0
                },
            );
        }
    }
}
pub fn attach(hwnd: HWND) {
    unsafe {
        SetWindowSubclass(hwnd, Some(proc), 12, 0);
    }
}
pub fn attach_form(hwnd: HWND) {
    STATES.with(|s| {
        s.borrow_mut().insert(
            hwnd as usize,
            State {
                form: true,
                ..State::default()
            },
        )
    });
    attach(hwnd);
}
pub fn paint(hwnd: HWND) {
    paint_to(hwnd, None);
}
fn paint_to(hwnd: HWND, print: Option<HDC>) {
    unsafe {
        // Nested WM_SIZE/WM_NCPAINT messages must not expose a scrollbar
        // from an intermediate list layout, including custom print requests.
        if content::updating(hwnd) || (print.is_none() && IsWindowVisible(hwnd) == 0) {
            return;
        }
        let state = STATES.with(|s| {
            s.borrow()
                .get(&(hwnd as usize))
                .copied()
                .unwrap_or_default()
        });
        let dc = print.unwrap_or_else(|| GetWindowDC(hwnd));
        if dc.is_null() {
            return;
        }
        let vertical = Bar::read(hwnd, true);
        let horizontal = Bar::read(hwnd, false);
        with_palette(GetParent(hwnd), |p| {
            for bar in [vertical.as_ref(), horizontal.as_ref()]
                .into_iter()
                .flatten()
            {
                let hot = state.hot == Some(bar.vertical)
                    || state.drag.is_some_and(|d| d.0 == bar.vertical);
                let color = motion::scrollbar_color(
                    hwnd,
                    u32::MAX - u32::from(bar.vertical),
                    if hot {
                        p.accent
                    } else {
                        blend(p.background, p.muted, 52)
                    },
                );
                chrome::buffered(dc, bar.area, |buffer| {
                    chrome::fill(buffer, &bar.area, p.background);
                    chrome::rounded(buffer, &bar.thumb, chrome::scale(hwnd, 6), color, color);
                });
            }
            if let (Some(v), Some(h)) = (&vertical, &horizontal) {
                chrome::fill(
                    dc,
                    &RECT {
                        left: v.area.left,
                        top: h.area.top,
                        right: v.area.right,
                        bottom: h.area.bottom,
                    },
                    p.background,
                );
            }
        });
        if print.is_none() {
            ReleaseDC(hwnd, dc);
        }
    }
}
unsafe extern "system" fn proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    let mut state = STATES.with(|s| {
        s.borrow()
            .get(&(hwnd as usize))
            .copied()
            .unwrap_or_default()
    });
    if message == WM_NCDESTROY {
        STATES.with(|s| s.borrow_mut().remove(&(hwnd as usize)));
        return unsafe { DefSubclassProc(hwnd, message, w, l) };
    }
    if message == WM_NCMOUSEMOVE {
        state.hot = [true, false]
            .into_iter()
            .find(|v| Bar::read(hwnd, *v).is_some_and(|b| b.contains(b.pointer())));
        STATES.with(|s| s.borrow_mut().insert(hwnd as usize, state));
        unsafe {
            let mut track = TRACKMOUSEEVENT {
                cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                dwFlags: TME_LEAVE | TME_NONCLIENT,
                hwndTrack: hwnd,
                dwHoverTime: 0,
            };
            TrackMouseEvent(&mut track);
        }
        paint(hwnd);
        return 0;
    }
    if message == WM_NCMOUSELEAVE {
        state.hot = None;
        STATES.with(|s| s.borrow_mut().insert(hwnd as usize, state));
        paint(hwnd);
    }
    if message == WM_NCLBUTTONDOWN || message == WM_NCLBUTTONDBLCLK {
        for vertical in [true, false] {
            if let Some(bar) = Bar::read(hwnd, vertical) {
                let point = bar.pointer();
                if bar.contains(point) {
                    if unsafe { PtInRect(&bar.thumb, point) } != 0 {
                        state.drag = Some((vertical, bar.coordinate(point) - bar.thumb_start()));
                        STATES.with(|s| s.borrow_mut().insert(hwnd as usize, state));
                        unsafe {
                            SetCapture(hwnd);
                        }
                    } else {
                        bar.scroll(
                            hwnd,
                            bar.position
                                + if bar.coordinate(point) < bar.thumb_start() {
                                    -bar.page
                                } else {
                                    bar.page
                                },
                        );
                    }
                    paint(hwnd);
                    return 0;
                }
            }
        }
    }
    if message == WM_MOUSEMOVE {
        if let Some((vertical, offset)) = state.drag {
            if let Some(bar) = Bar::read(hwnd, vertical) {
                let start = if vertical {
                    bar.area.top
                } else {
                    bar.area.left
                } + 2;
                let travel = (if vertical {
                    bar.area.bottom - bar.area.top - (bar.thumb.bottom - bar.thumb.top)
                } else {
                    bar.area.right - bar.area.left - (bar.thumb.right - bar.thumb.left)
                } - 4)
                    .max(1);
                let target = ((bar.coordinate(bar.pointer()) - start - offset) as i64
                    * bar.max as i64
                    / travel as i64) as i32;
                bar.scroll(hwnd, target);
                paint(hwnd);
                return 0;
            }
        }
    }
    if (message == WM_LBUTTONUP || message == WM_CAPTURECHANGED) && state.drag.take().is_some() {
        STATES.with(|s| s.borrow_mut().insert(hwnd as usize, state));
        if message == WM_LBUTTONUP {
            unsafe {
                ReleaseCapture();
            }
        }
        paint(hwnd);
        return 0;
    }
    let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
    if message == WM_PRINT {
        paint_to(hwnd, Some(w as HDC));
    }
    if matches!(
        message,
        WM_NCPAINT
            | WM_PAINT
            | WM_SIZE
            | WM_MOUSEWHEEL
            | WM_HSCROLL
            | WM_VSCROLL
            | LVM_SCROLL
            | LVM_SETITEMCOUNT
            | LVM_SETCOLUMNWIDTH
            | LVM_SETVIEW
            | LVM_SETIMAGELIST
    ) {
        paint(hwnd);
    }
    result
}
