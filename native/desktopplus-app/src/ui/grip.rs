use super::*;
#[derive(Clone, Copy)]
struct Drag {
    pointer: POINT,
    bounds: RECT,
    changed: bool,
}
thread_local! { static DRAGS: RefCell<HashMap<usize,Drag>>=RefCell::new(HashMap::new()); }
pub fn create(parent: HWND) {
    unsafe {
        let class = wide("DesktopPlus.Rust.ResizeGrip");
        let instance = GetModuleHandleW(null());
        RegisterClassW(&WNDCLASSW {
            style: CS_DBLCLKS,
            lpfnWndProc: Some(proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_SIZENWSE),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let grip = CreateWindowExW(
            0,
            class.as_ptr(),
            wide(crate::locale::text(
                "Drag to resize; double-click to fit width, then height.",
            ))
            .as_ptr(),
            WS_CHILD | WS_VISIBLE,
            0,
            0,
            20,
            20,
            parent,
            RESIZE_GRIP as usize as _,
            instance,
            null(),
        );
        chrome::setup_button(grip);
        chrome::tooltips(parent, &[RESIZE_GRIP]);
    }
}
unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let parent = unsafe { GetParent(hwnd) };
    match message {
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, area| {
                    with_palette(parent, |p| {
                        chrome::fill(dc, &area, p.background);
                        unsafe {
                            let pen = CreatePen(
                                PS_SOLID,
                                chrome::scale(parent, 1),
                                motion::color(
                                    hwnd,
                                    0,
                                    if chrome::is_hot(hwnd)
                                        || DRAGS.with(|d| d.borrow().contains_key(&(hwnd as usize)))
                                    {
                                        p.accent
                                    } else {
                                        p.muted
                                    },
                                ),
                            );
                            let previous = SelectObject(dc, pen);
                            let s = |v| chrome::scale(parent, v);
                            for length in [4, 8, 12] {
                                MoveToEx(
                                    dc,
                                    area.right - s(length + 3),
                                    area.bottom - s(3),
                                    null_mut(),
                                );
                                LineTo(dc, area.right - s(3), area.bottom - s(length + 3));
                            }
                            SelectObject(dc, previous);
                            DeleteObject(pen);
                        }
                    });
                },
            );
            return 0;
        }
        WM_ERASEBKGND => return 1,
        WM_LBUTTONDBLCLK => {
            DRAGS.with(|d| d.borrow_mut().remove(&(hwnd as usize)));
            unsafe {
                ReleaseCapture();
            }
            emit(Event::Command(parent as usize, FIT_CONTENT));
            return 0;
        }
        WM_LBUTTONDOWN => {
            motion::cancel_resize(parent);
            icon_grid::begin_resize(parent);
            unsafe {
                let mut pointer = POINT::default();
                GetCursorPos(&mut pointer);
                let mut bounds = RECT::default();
                GetWindowRect(parent, &mut bounds);
                DRAGS.with(|d| {
                    d.borrow_mut().insert(
                        hwnd as usize,
                        Drag {
                            pointer,
                            bounds,
                            changed: false,
                        },
                    )
                });
                SetCapture(hwnd);
                InvalidateRect(hwnd, null(), 0);
            }
            return 0;
        }
        WM_MOUSEMOVE => {
            if let Some(mut drag) = DRAGS.with(|d| d.borrow().get(&(hwnd as usize)).copied()) {
                unsafe {
                    let mut point = POINT::default();
                    GetCursorPos(&mut point);
                    let mut monitor = MONITORINFO {
                        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                        ..std::mem::zeroed()
                    };
                    GetMonitorInfoW(
                        MonitorFromWindow(parent, MONITOR_DEFAULTTONEAREST),
                        &mut monitor,
                    );
                    let width = (drag.bounds.right - drag.bounds.left + point.x - drag.pointer.x)
                        .clamp(
                            chrome::scale(parent, 220),
                            (monitor.rcWork.right - drag.bounds.left)
                                .max(chrome::scale(parent, 220)),
                        );
                    let height = (drag.bounds.bottom - drag.bounds.top + point.y - drag.pointer.y)
                        .clamp(
                            chrome::scale(parent, 160),
                            (monitor.rcWork.bottom - drag.bounds.top)
                                .max(chrome::scale(parent, 160)),
                        );
                    if point.x != drag.pointer.x || point.y != drag.pointer.y {
                        drag.changed = true;
                    }
                    DRAGS.with(|d| d.borrow_mut().insert(hwnd as usize, drag));
                    SetWindowPos(
                        parent,
                        null_mut(),
                        0,
                        0,
                        width,
                        height,
                        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
                    );
                }
            }
            return 0;
        }
        WM_LBUTTONUP | WM_CAPTURECHANGED => {
            let drag = DRAGS.with(|d| d.borrow_mut().remove(&(hwnd as usize)));
            if message == WM_LBUTTONUP {
                unsafe {
                    ReleaseCapture();
                }
            }
            icon_grid::end_resize(parent);
            if drag.is_some_and(|d| d.changed) {
                emit(Event::Geometry(parent as usize));
            }
            unsafe {
                InvalidateRect(hwnd, null(), 0);
            }
            return 0;
        }
        WM_NCDESTROY => {
            DRAGS.with(|d| d.borrow_mut().remove(&(hwnd as usize)));
            icon_grid::forget(parent);
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
