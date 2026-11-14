use super::*;
use std::ops::Range;
const STATE: &[u16] = &[68, 80, 70, 111, 114, 109, 0];
const BODY: i32 = 801;
pub(super) const SCROLL_TO: u32 = WM_APP + 71;
pub(super) const REVEAL_FOCUS: u32 = WM_APP + 72;
pub enum Input {
    Text(String, String),
    Folder(String, String),
    Check(String, bool),
    Choice(String, Vec<String>, usize),
    Action(String, u16),
}
pub struct Page {
    pub name: &'static str,
    pub fields: Vec<Input>,
}
pub struct Answer {
    pub values: Vec<String>,
    pub action: Option<u16>,
}
type Validator<'a> = dyn Fn(&[String]) -> Result<(), String> + 'a;
struct State<'a> {
    fields: Vec<Input>,
    pages: Vec<(&'static str, Range<usize>)>,
    page: usize,
    offset: i32,
    extent: i32,
    result: Option<Answer>,
    done: bool,
    validator: Box<Validator<'a>>,
    embedded: Option<u16>,
    title: String,
    subtitle: String,
}
fn state(hwnd: HWND) -> *mut State<'static> {
    unsafe { GetPropW(hwnd, STATE.as_ptr()) as _ }
}
/// A persistent workspace tab. Its controls retain drafts while another tab is visible.
pub fn embed(
    owner: HWND,
    command: u16,
    title: &'static str,
    fields: Vec<Input>,
    validate: impl Fn(&[String]) -> Result<(), String> + 'static,
) -> Result<HWND, String> {
    let count = fields.len();
    let data = Box::new(State {
        fields,
        pages: vec![(title, 0..count)],
        page: 0,
        offset: 0,
        extent: 0,
        result: None,
        done: false,
        validator: Box::new(validate),
        embedded: Some(command),
        title: crate::locale::text(title),
        subtitle: crate::locale::text("Make it work your way."),
    });
    unsafe {
        let instance = GetModuleHandleW(null());
        let class = wide("DesktopPlus.Rust.WorkspaceForm");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let hwnd = CreateWindowExW(
            WS_EX_CONTROLPARENT,
            class.as_ptr(),
            wide(&data.title).as_ptr(),
            WS_CHILD | WS_CLIPCHILDREN,
            0,
            0,
            1,
            1,
            owner,
            dashboard::form_id(command) as usize as _,
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(shell::error_text());
        }
        let pointer = Box::into_raw(data);
        SetPropW(hwnd, STATE.as_ptr(), pointer.cast());
        create_controls(hwnd, &*pointer);
        Ok(hwnd)
    }
}
pub fn status(hwnd: HWND, caption: &str) {
    set_text(control(hwnd, 802), &crate::locale::text(caption));
}
pub fn choices(hwnd: HWND, index: usize, choices: Vec<String>, selected: usize) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let data = unsafe { &mut *pointer };
    if let Some(Input::Choice(_, values, value)) = data.fields.get_mut(index) {
        *values = choices;
        *value = selected;
        relabel(hwnd);
        unsafe {
            SendMessageW(
                control(control(hwnd, BODY), 1000 + index as i32),
                CB_SETCURSEL,
                selected,
                0,
            );
        }
    }
}
pub fn restyle(hwnd: HWND) {
    if hwnd.is_null() {
        return;
    }
    chrome::restyle(hwnd);
    chrome::restyle(control(hwnd, BODY));
    layout_form(hwnd);
}
pub fn relabel(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let data = unsafe { &mut *pointer };
    data.title = crate::locale::text(data.pages[0].0);
    data.subtitle = crate::locale::text("Make it work your way.");
    let body = control(hwnd, BODY);
    for (i, field) in data.fields.iter().enumerate() {
        let input = control(body, 1000 + i as i32);
        match field {
            Input::Text(label, _) | Input::Folder(label, _) => {
                set_text(control(body, 2000 + i as i32), &crate::locale::text(label));
            }
            Input::Check(label, _) | Input::Action(label, _) => {
                set_text(input, &crate::locale::text(label));
            }
            Input::Choice(label, choices, _) => unsafe {
                set_text(control(body, 2000 + i as i32), &crate::locale::text(label));
                let selected = SendMessageW(input, CB_GETCURSEL, 0, 0);
                SendMessageW(input, CB_RESETCONTENT, 0, 0);
                for choice in choices {
                    SendMessageW(
                        input,
                        CB_ADDSTRING,
                        0,
                        wide(crate::locale::text(choice)).as_ptr() as isize,
                    );
                }
                SendMessageW(input, CB_SETCURSEL, selected as usize, 0);
            },
        }
    }
    set_text(control(hwnd, IDOK), &crate::locale::text("Save"));
    set_text(control(hwnd, IDCANCEL), &crate::locale::text("Cancel"));
    status(hwnd, "");
    unsafe {
        InvalidateRect(hwnd, null(), 1);
    }
}
pub fn show(
    owner: HWND,
    title: &str,
    subtitle: &str,
    pages: Vec<Page>,
    validate: &Validator<'_>,
) -> Option<Answer> {
    unsafe {
        let mut fields = Vec::new();
        let mut groups = Vec::new();
        for page in pages {
            let start = fields.len();
            fields.extend(page.fields);
            groups.push((page.name, start..fields.len()));
        }
        let mut data = State {
            fields,
            pages: groups,
            page: 0,
            offset: 0,
            extent: 0,
            result: None,
            done: false,
            validator: Box::new(validate),
            embedded: None,
            title: crate::locale::text(title),
            subtitle: subtitle.into(),
        };
        let instance = GetModuleHandleW(null());
        let class = wide("DesktopPlus.Rust.PanelSettings");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let mut owner_rect = RECT::default();
        GetWindowRect(owner, &mut owner_rect);
        let mut monitor = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..std::mem::zeroed()
        };
        GetMonitorInfoW(
            MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST),
            &mut monitor,
        );
        let work = monitor.rcWork;
        let single = data.pages.len() == 1;
        let content_height: i32 = data
            .fields
            .iter()
            .map(|field| {
                if matches!(field, Input::Check(..) | Input::Action(..)) {
                    56
                } else {
                    88
                }
            })
            .sum();
        let width =
            chrome::scale(owner, if single { 620 } else { 880 }).min(work.right - work.left);
        let height = chrome::scale(
            owner,
            if single {
                (content_height + 256).clamp(340, 720)
            } else {
                720
            },
        )
        .min(work.bottom - work.top);
        let x = (owner_rect.left + (owner_rect.right - owner_rect.left - width) / 2)
            .clamp(work.left, (work.right - width).max(work.left));
        let y = (owner_rect.top + (owner_rect.bottom - owner_rect.top - height) / 2)
            .clamp(work.top, (work.bottom - height).max(work.top));
        let hwnd = CreateWindowExW(
            WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT,
            class.as_ptr(),
            wide(&data.title).as_ptr(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN,
            x,
            y,
            width,
            height,
            owner,
            null_mut(),
            instance,
            null(),
        );
        if hwnd.is_null() {
            return None;
        }
        SetPropW(hwnd, STATE.as_ptr(), (&mut data as *mut State<'_>).cast());
        theme_frame(hwnd);
        create_controls(hwnd, &data);
        EnableWindow(owner, 0);
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        SetFocus(control(control(hwnd, BODY), 1000));
        let mut msg = MSG::default();
        loop {
            if data.done {
                break;
            }
            let status = GetMessageW(&mut msg, null_mut(), 0, 0);
            if status <= 0 {
                if status == 0 {
                    PostQuitMessage(msg.wParam as i32);
                }
                break;
            }
            if IsDialogMessageW(hwnd, &msg) == 0 {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        RemovePropW(hwnd, STATE.as_ptr());
        DestroyWindow(hwnd);
        EnableWindow(owner, 1);
        SetForegroundWindow(owner);
        data.result
    }
}
fn create_controls(hwnd: HWND, data: &State<'_>) {
    unsafe {
        let instance = GetModuleHandleW(null());
        let body_class = wide("DesktopPlus.Rust.FormBody");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(body_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: body_class.as_ptr(),
            ..std::mem::zeroed()
        });
        let body = CreateWindowExW(
            WS_EX_CONTROLPARENT,
            body_class.as_ptr(),
            null(),
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_CLIPCHILDREN,
            0,
            0,
            1,
            1,
            hwnd,
            BODY as usize as _,
            instance,
            null(),
        );
        SetWindowTheme(body, wide("DarkMode_Explorer").as_ptr(), null());
        scrollbars::attach_form(body);
        if data.embedded.is_none() {
            for (index, (name, _)) in data.pages.iter().enumerate() {
                button(hwnd, name, 700 + index as u16);
            }
        }
        for (index, field) in data.fields.iter().enumerate() {
            let id = 1000 + index as i32;
            match field {
                Input::Text(label, value) | Input::Folder(label, value) => {
                    child(body, "STATIC", label, 2000 + index as i32, 0);
                    styled::edit(body, value, id);
                    if matches!(field, Input::Folder(..)) {
                        button(body, "Browse…", 3000 + index as u16);
                    }
                }
                Input::Check(label, value) => {
                    styled::check(body, label, id, *value);
                }
                Input::Choice(label, choices, selected) => {
                    child(body, "STATIC", label, 2000 + index as i32, 0);
                    styled::choice(body, label, id, choices, *selected);
                }
                Input::Action(label, _) => {
                    button(body, label, id as u16);
                }
            }
        }
        button(hwnd, "Save", IDOK as u16);
        button(hwnd, "Cancel", IDCANCEL as u16);
        child(hwnd, "STATIC", "", 802, 0);
        layout_form(hwnd);
    }
}
fn values(hwnd: HWND, data: &State<'_>) -> Vec<String> {
    let body = control(hwnd, BODY);
    data.fields
        .iter()
        .enumerate()
        .map(|(i, field)| {
            let child = control(body, 1000 + i as i32);
            match field {
                Input::Text(..) | Input::Folder(..) => text(child),
                Input::Check(..) => usize::from(
                    unsafe { SendMessageW(child, BM_GETCHECK, 0, 0) } == BST_CHECKED as isize,
                )
                .to_string(),
                Input::Choice(..) => unsafe { SendMessageW(child, CB_GETCURSEL, 0, 0) }.to_string(),
                Input::Action(..) => String::new(),
            }
        })
        .collect()
}
fn layout_form(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let data = unsafe { &mut *pointer };
    let mut bounds = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut bounds);
    }
    let width = bounds.right * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let height = bounds.bottom * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let single = data.pages.len() == 1;
    for i in 0..data.pages.len() {
        unsafe {
            ShowWindow(
                control(hwnd, 700 + i as i32),
                if single { SW_HIDE } else { SW_SHOWNA },
            );
        }
        move_control(hwnd, 700 + i as i32, 20, 120 + i as i32 * 48, 172, 40);
    }
    let body_left = if single { 28 } else { 232 };
    move_control(
        hwnd,
        BODY,
        body_left,
        116,
        width - body_left - 28,
        height - 200,
    );
    move_control(hwnd, IDOK, width - 260, height - 56, 112, 38);
    move_control(hwnd, IDCANCEL, width - 138, height - 56, 114, 38);
    move_control(hwnd, 802, 24, height - 56, width - 296, 40);
    layout_fields(hwnd);
    unsafe {
        InvalidateRect(hwnd, null(), 0);
    }
}
// Move every field before painting any of them. MoveWindow(..., TRUE) repaints
// synchronously and copies pixels while sibling controls still occupy their old
// positions, leaving fragments of labels and backgrounds in the moved inputs.
fn place_field(body: HWND, id: i32, bounds: Option<(i32, i32, i32, i32)>) {
    let field = control(body, id);
    if field.is_null() {
        return;
    }
    let (x, y, width, height) = bounds.unwrap_or_default();
    unsafe {
        SetWindowPos(
            field,
            null_mut(),
            chrome::scale(body, x),
            chrome::scale(body, y),
            chrome::scale(body, width.max(0)),
            chrome::scale(body, height.max(0)),
            SWP_NOZORDER
                | SWP_NOACTIVATE
                | SWP_NOREDRAW
                | SWP_NOCOPYBITS
                | if bounds.is_some() {
                    SWP_SHOWWINDOW
                } else {
                    SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE
                },
        );
    }
}
fn layout_fields(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let data = unsafe { &mut *pointer };
    let body = control(hwnd, BODY);
    let mut client = RECT::default();
    unsafe {
        GetClientRect(body, &mut client);
    }
    let range = data.pages[data.page].1.clone();
    data.extent = range
        .clone()
        .map(|i| {
            if matches!(data.fields[i], Input::Check(..) | Input::Action(..)) {
                56
            } else {
                88
            }
        })
        .sum();
    let page = client.bottom * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    data.offset = data.offset.clamp(0, (data.extent - page).max(0));
    let info = SCROLLINFO {
        cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
        fMask: SIF_RANGE | SIF_PAGE | SIF_POS,
        nMin: 0,
        nMax: data.extent - 1,
        nPage: page.max(0) as u32,
        nPos: data.offset,
        ..unsafe { std::mem::zeroed() }
    };
    unsafe {
        SetScrollInfo(body, SB_VERT, &info, 0);
        // Showing or hiding the scrollbar changes the available field width.
        GetClientRect(body, &mut client);
    }
    let body_width = client.right * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32 - 12;
    let mut y = -data.offset;
    for (i, field) in data.fields.iter().enumerate() {
        if !range.contains(&i) {
            for id in [1000 + i as i32, 2000 + i as i32, 3000 + i as i32] {
                place_field(body, id, None);
            }
            continue;
        }
        let id = 1000 + i as i32;
        match field {
            Input::Check(..) | Input::Action(..) => {
                place_field(body, id, Some((0, y + 2, body_width, 44)));
                y += 56;
            }
            Input::Text(..) | Input::Folder(..) => {
                place_field(body, 2000 + i as i32, Some((0, y, body_width, 24)));
                let edit_width = body_width
                    - if matches!(field, Input::Folder(..)) {
                        112
                    } else {
                        0
                    };
                place_field(body, id, Some((12, y + 36, edit_width - 24, 24)));
                if matches!(field, Input::Folder(..)) {
                    place_field(
                        body,
                        3000 + i as i32,
                        Some((body_width - 102, y + 28, 102, 40)),
                    );
                }
                y += 88;
            }
            Input::Choice(..) => {
                place_field(body, 2000 + i as i32, Some((0, y, body_width, 24)));
                place_field(body, id, Some((0, y + 28, body_width, 270)));
                y += 88;
            }
        }
    }
    unsafe {
        // WS_CLIPCHILDREN excludes the inputs from the body's paint region;
        // invalidate them explicitly, including fields returning from clipping.
        RedrawWindow(
            body,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN,
        );
    }
}
fn scroll(hwnd: HWND, delta: i32, absolute: bool) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let data = unsafe { &mut *pointer };
    let mut rect = RECT::default();
    unsafe {
        GetClientRect(control(hwnd, BODY), &mut rect);
    }
    let page = rect.bottom * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let offset =
        (if absolute { delta } else { data.offset + delta }).clamp(0, (data.extent - page).max(0));
    if offset != data.offset {
        data.offset = offset;
        layout_fields(hwnd);
    }
}
unsafe extern "system" fn body_proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let parent = unsafe { GetParent(hwnd) };
    if message == SCROLL_TO {
        scroll(parent, w as i32, true);
        return 0;
    }
    if message == REVEAL_FOCUS {
        let mut area = RECT::default();
        let mut client = RECT::default();
        unsafe {
            GetWindowRect(w as HWND, &mut area);
            MapWindowPoints(null_mut(), hwnd, (&mut area as *mut RECT).cast(), 2);
            GetClientRect(hwnd, &mut client);
        }
        let margin = chrome::scale(hwnd, 12);
        let delta = if area.top < margin {
            area.top - margin
        } else if area.bottom > client.bottom - margin {
            area.bottom - client.bottom + margin
        } else {
            0
        };
        if delta != 0 {
            scroll(
                parent,
                delta * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32,
                false,
            );
        }
        return 0;
    }
    if message == WM_PAINT || message == WM_PRINTCLIENT {
        chrome::paint(
            hwnd,
            (message == WM_PRINTCLIENT).then_some(w as HDC),
            |dc, bounds| {
                with_palette(parent, |p| {
                    chrome::fill(dc, &bounds, p.background);
                    let pointer = state(parent);
                    if pointer.is_null() {
                        return;
                    }
                    let data = unsafe { &*pointer };
                    for i in data.pages[data.page].1.clone() {
                        if matches!(data.fields[i], Input::Text(..) | Input::Folder(..)) {
                            let edit = control(hwnd, 1000 + i as i32);
                            let mut area = RECT::default();
                            unsafe {
                                GetWindowRect(edit, &mut area);
                                MapWindowPoints(
                                    null_mut(),
                                    hwnd,
                                    (&mut area as *mut RECT).cast(),
                                    2,
                                );
                                InflateRect(
                                    &mut area,
                                    chrome::scale(parent, 12),
                                    chrome::scale(parent, 8),
                                );
                            }
                            chrome::rounded(
                                dc,
                                &area,
                                chrome::scale(parent, 12),
                                p.surface,
                                motion::color(
                                    hwnd,
                                    i as u32,
                                    if unsafe { GetFocus() == edit } {
                                        p.accent
                                    } else {
                                        p.border
                                    },
                                ),
                            );
                        }
                    }
                });
            },
        );
        return 0;
    }
    if message == WM_VSCROLL {
        let mut info = SCROLLINFO {
            cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
            fMask: SIF_ALL,
            ..unsafe { std::mem::zeroed() }
        };
        unsafe {
            GetScrollInfo(hwnd, SB_VERT, &mut info);
        }
        match w as u16 as i32 {
            SB_LINEUP => scroll(parent, -40, false),
            SB_LINEDOWN => scroll(parent, 40, false),
            SB_PAGEUP => scroll(parent, -(info.nPage as i32), false),
            SB_PAGEDOWN => scroll(parent, info.nPage as i32, false),
            SB_THUMBTRACK | SB_THUMBPOSITION => scroll(parent, info.nTrackPos, true),
            _ => {}
        }
        return 0;
    }
    if message == WM_MOUSEWHEEL {
        scroll(parent, -((w >> 16) as i16 as i32) / 120 * 96, false);
        return 0;
    }
    if message == WM_NCDESTROY {
        chrome::release(hwnd);
    }
    if message == WM_ERASEBKGND {
        // The buffered WM_PAINT supplies the complete background and borders.
        return 1;
    }
    if matches!(
        message,
        WM_COMMAND
            | WM_MEASUREITEM
            | WM_DRAWITEM
            | WM_CTLCOLORSTATIC
            | WM_CTLCOLOREDIT
            | WM_CTLCOLORLISTBOX
    ) {
        return unsafe { SendMessageW(parent, message, w, l) };
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let pointer = state(hwnd);
    if message == WM_NCDESTROY {
        chrome::release(hwnd);
        if !pointer.is_null() && unsafe { (*pointer).embedded.is_some() } {
            unsafe {
                RemovePropW(hwnd, STATE.as_ptr());
                drop(Box::from_raw(pointer));
            }
            return unsafe { DefWindowProcW(hwnd, message, w, l) };
        }
    }
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    if message == WM_CLOSE {
        unsafe {
            (*pointer).done = true;
        }
        return 0;
    }
    if message == WM_ERASEBKGND {
        return 1;
    }
    if message == WM_SIZE || message == WM_DPICHANGED || message == WM_DPICHANGED_AFTERPARENT {
        if message == WM_DPICHANGED {
            let r = unsafe { &*(l as *const RECT) };
            unsafe {
                SetWindowPos(
                    hwnd,
                    null_mut(),
                    r.left,
                    r.top,
                    r.right - r.left,
                    r.bottom - r.top,
                    SWP_NOZORDER | SWP_NOACTIVATE,
                );
            }
            chrome::restyle(hwnd);
            chrome::restyle(control(hwnd, BODY));
        }
        if message == WM_DPICHANGED_AFTERPARENT {
            restyle(hwnd);
        }
        layout_form(hwnd);
        return 0;
    }
    if message == WM_PAINT || message == WM_PRINTCLIENT {
        chrome::paint(
            hwnd,
            (message == WM_PRINTCLIENT).then_some(w as HDC),
            |dc, bounds| {
                let data = unsafe { &*pointer };
                with_palette(hwnd, |p| {
                    chrome::fill(dc, &bounds, p.background);
                    let s = |v| chrome::scale(hwnd, v);
                    if data.pages.len() > 1 {
                        chrome::fill(
                            dc,
                            &RECT {
                                left: s(12),
                                top: s(108),
                                right: s(210),
                                bottom: bounds.bottom - s(86),
                            },
                            blend(p.background, p.surface, 55),
                        );
                        let top =
                            motion::scalar(hwnd, 0, (130 + data.page * 48) as f64).round() as i32;
                        chrome::rounded(
                            dc,
                            &RECT {
                                left: s(12),
                                top: s(top),
                                right: s(15),
                                bottom: s(top + 20),
                            },
                            s(3),
                            p.accent,
                            p.accent,
                        );
                    }

                    chrome::label(
                        dc,
                        &data.title,
                        RECT {
                            left: s(24),
                            top: s(18),
                            right: bounds.right - s(24),
                            bottom: s(60),
                        },
                        chrome::font(hwnd, 2),
                        p.text,
                        DT_VCENTER | DT_SINGLELINE,
                    );
                    chrome::label(
                        dc,
                        &data.subtitle,
                        RECT {
                            left: s(26),
                            top: s(64),
                            right: bounds.right - s(24),
                            bottom: s(90),
                        },
                        chrome::font(hwnd, 0),
                        p.muted,
                        DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                    );
                    chrome::fill(
                        dc,
                        &RECT {
                            left: 0,
                            top: bounds.bottom - s(72),
                            right: bounds.right,
                            bottom: bounds.bottom - s(72) + 1,
                        },
                        p.border,
                    );
                });
            },
        );
        return 0;
    }
    if message == WM_COMMAND {
        let id = w as u16 as i32;
        let data = unsafe { &mut *pointer };
        if id >= 1000 && matches!((w >> 16) as u32, EN_CHANGE | BN_CLICKED | CBN_SELCHANGE) {
            set_text(control(hwnd, 802), "");
        }
        if id == IDCANCEL {
            if let Some(command) = data.embedded {
                emit(Event::PreferenceCancelled(command));
            } else {
                data.done = true;
            }
            return 0;
        }
        if (700..700 + data.pages.len() as i32).contains(&id) {
            data.page = (id - 700) as usize;
            data.offset = 0;
            let page_count = data.pages.len();
            layout_form(hwnd);
            // The form clips child windows. Repainting its background cannot
            // clear the previous page's owner-drawn selection or focus state.
            for index in 0..page_count {
                unsafe {
                    InvalidateRect(control(hwnd, 700 + index as i32), null(), 0);
                }
            }
            return 0;
        }
        if id >= 3000 && (id - 3000) < data.fields.len() as i32 {
            if let Ok(Some(path)) = shell::choose_folder(hwnd) {
                set_text(
                    control(control(hwnd, BODY), 1000 + id - 3000),
                    &path.to_string_lossy(),
                );
            }
            return 0;
        }
        let action = (1000..1000 + data.fields.len() as i32)
            .contains(&id)
            .then(|| &data.fields[(id - 1000) as usize])
            .and_then(|field| {
                if let Input::Action(_, action) = field {
                    Some(*action)
                } else {
                    None
                }
            });
        if id == IDOK || action.is_some() {
            if let (Some(command), Some(action)) = (data.embedded, action) {
                emit(Event::PreferenceSaved(
                    command,
                    Answer {
                        values: Vec::new(),
                        action: Some(action),
                    },
                ));
                return 0;
            }
            let values = values(hwnd, data);
            match (data.validator)(&values) {
                Ok(()) => {
                    let answer = Answer { values, action };
                    if let Some(command) = data.embedded {
                        emit(Event::PreferenceSaved(command, answer));
                    } else {
                        data.result = Some(answer);
                        data.done = true;
                    }
                }
                Err(error) => {
                    set_text(control(hwnd, 802), &crate::locale::text(&error));
                }
            }
            return 0;
        }
    }
    if message == WM_MEASUREITEM {
        let item = unsafe { &mut *(l as *mut MEASUREITEMSTRUCT) };
        item.itemHeight = chrome::scale(hwnd, 30) as u32;
        return 1;
    }
    if message == WM_DRAWITEM {
        let draw = unsafe { &*(l as *const DRAWITEMSTRUCT) };
        if draw.CtlType == ODT_COMBOBOX {
            chrome::draw_item(draw, styled::draw_choice);
            return 1;
        }
        if (700..800).contains(&draw.CtlID) {
            let data = unsafe { &*pointer };
            chrome::draw_item(draw, |draw| {
                with_palette(hwnd, |p| {
                    chrome::fill(draw.hDC, &draw.rcItem, blend(p.background, p.surface, 55));
                    let active = draw.CtlID as usize - 700 == data.page;
                    chrome::rounded(
                        draw.hDC,
                        &draw.rcItem,
                        chrome::scale(hwnd, 12),
                        motion::color(
                            draw.hwndItem,
                            0,
                            if active {
                                p.selected
                            } else if chrome::is_hot(draw.hwndItem) {
                                p.hover
                            } else {
                                blend(p.background, p.surface, 55)
                            },
                        ),
                        motion::color(
                            draw.hwndItem,
                            1,
                            if chrome::keyboard_focus(draw.hwndItem) {
                                p.accent
                            } else {
                                blend(p.background, p.surface, 55)
                            },
                        ),
                    );
                    let mut caption_rect = draw.rcItem;
                    caption_rect.left += chrome::scale(hwnd, 18);
                    chrome::label(
                        draw.hDC,
                        &text(draw.hwndItem),
                        caption_rect,
                        chrome::font(hwnd, usize::from(active)),
                        motion::color(draw.hwndItem, 2, if active { p.accent } else { p.text }),
                        DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                    );
                })
            });
            return 1;
        }
    }
    if matches!(
        message,
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT | WM_CTLCOLORLISTBOX | WM_DRAWITEM
    ) {
        return unsafe { window_proc(hwnd, message, w, l) };
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
