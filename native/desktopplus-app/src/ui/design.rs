//! A draft-based appearance studio shared by global and per-panel settings.
use super::*;
use crate::model::Settings;
use form::Input;
use windows_sys::Win32::UI::Controls::Dialogs::*;

mod draft;
mod preview;
#[cfg(test)]
mod tests;
use draft::Draft;

const PROPERTY: &[u16] = &[68, 80, 68, 101, 115, 105, 103, 110, 0];
const BODY: i32 = 801;
const FEEDBACK: i32 = 802;
const SCOPE: i32 = 810;
const PRESET: i32 = 811;
const PREVIEW: i32 = 812;
const PREVIEW_MODE: i32 = 813;
const DESKTOP_PREVIEW: u16 = 814;
const REVERT: u16 = 815;
const REFRESH: u32 = WM_APP + 90;

struct State {
    draft: Draft,
    page: usize,
    offset: i32,
    extent: i32,
    wheel: scrolling::Wheel,
    embedded: bool,
    fixed_scope: bool,
    building: bool,
    done: bool,
    accepted: bool,
    dirty: bool,
    desktop: HWND,
    custom_colors: [u32; 16],
}
fn state(hwnd: HWND) -> *mut State {
    unsafe { GetPropW(hwnd, PROPERTY.as_ptr()).cast() }
}
fn rect(hwnd: HWND, x: i32, y: i32, width: i32, height: i32) -> RECT {
    RECT {
        left: chrome::scale(hwnd, x),
        top: chrome::scale(hwnd, y),
        right: chrome::scale(hwnd, x + width),
        bottom: chrome::scale(hwnd, y + height),
    }
}
fn dimensions(hwnd: HWND) -> (i32, i32) {
    let mut r = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut r);
    }
    let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    (r.right * 96 / dpi, r.bottom * 96 / dpi)
}

pub fn embed(owner: HWND, settings: &Settings) -> Result<HWND, String> {
    create(owner, settings, None, true)
}
pub fn show(owner: HWND, settings: &Settings, panel: Option<usize>) -> Option<Settings> {
    let hwnd = create(owner, settings, panel, false).ok()?;
    unsafe {
        EnableWindow(owner, 0);
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        SetFocus(control(hwnd, SCOPE));
        let mut msg = MSG::default();
        while !(*state(hwnd)).done {
            let result = GetMessageW(&mut msg, null_mut(), 0, 0);
            if result <= 0 {
                if result == 0 {
                    PostQuitMessage(msg.wParam as i32);
                }
                break;
            }
            if IsDialogMessageW(hwnd, &msg) == 0 {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        let result = (*state(hwnd))
            .accepted
            .then(|| (*state(hwnd)).draft.settings.clone());
        DestroyWindow(hwnd);
        EnableWindow(owner, 1);
        SetForegroundWindow(owner);
        result
    }
}
pub fn saved(hwnd: HWND) -> Result<Settings, String> {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return Err("The design editor is no longer open.".into());
    }
    unsafe {
        (*pointer).draft.commit()?;
        (*pointer).draft.original = (*pointer).draft.settings.clone();
        (*pointer).dirty = false;
        set_text(control(hwnd, FEEDBACK), &crate::locale::text("Saved."));
        Ok((*pointer).draft.settings.clone())
    }
}
pub fn relabel(hwnd: HWND) {
    if !state(hwnd).is_null() {
        rebuild(hwnd);
    }
}

fn create(
    owner: HWND,
    settings: &Settings,
    scope: Option<usize>,
    embedded: bool,
) -> Result<HWND, String> {
    unsafe {
        InitCommonControlsEx(&INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_BAR_CLASSES | ICC_TAB_CLASSES | ICC_STANDARD_CLASSES,
        });
        let class = wide("DesktopPlus.Rust.DesignStudio");
        let instance = GetModuleHandleW(null());
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let mut monitor = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..std::mem::zeroed()
        };
        GetMonitorInfoW(
            MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST),
            &mut monitor,
        );
        let area = monitor.rcWork;
        let width = chrome::scale(owner, 1120).min(area.right - area.left);
        let height = chrome::scale(owner, 800).min(area.bottom - area.top);
        let hwnd = CreateWindowExW(
            WS_EX_CONTROLPARENT,
            class.as_ptr(),
            wide(crate::locale::text("Design studio")).as_ptr(),
            WS_CLIPCHILDREN
                | if embedded {
                    WS_CHILD
                } else {
                    WS_OVERLAPPEDWINDOW
                },
            if embedded {
                0
            } else {
                area.left + (area.right - area.left - width) / 2
            },
            if embedded {
                0
            } else {
                area.top + (area.bottom - area.top - height) / 2
            },
            if embedded { 1 } else { width },
            if embedded { 1 } else { height },
            owner,
            if embedded {
                dashboard::form_id(THEMES) as usize as _
            } else {
                null_mut()
            },
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(shell::error_text());
        }
        let data = Box::new(State {
            draft: Draft::new(settings, scope),
            page: 2,
            offset: 0,
            extent: 0,
            wheel: Default::default(),
            embedded,
            fixed_scope: scope.is_some(),
            building: true,
            done: false,
            accepted: false,
            dirty: false,
            desktop: null_mut(),
            custom_colors: [0xffffff; 16],
        });
        SetPropW(hwnd, PROPERTY.as_ptr(), Box::into_raw(data).cast());
        // The editing tools stay readable, even while designing a low-contrast theme.
        PANEL_PALETTES.with(|p| {
            p.borrow_mut()
                .insert(hwnd as usize, Palette::new(&Theme::default()))
        });
        theme_frame(hwnd);
        let body_class = wide("DesktopPlus.Rust.DesignFields");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(body_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: body_class.as_ptr(),
            ..std::mem::zeroed()
        });
        let body = CreateWindowExW(
            WS_EX_CONTROLPARENT | WS_EX_COMPOSITED,
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
        child(hwnd, "STATIC", "", FEEDBACK, 0);
        for (id, label) in [
            (IDOK as u16, "Save design"),
            (IDCANCEL as u16, "Cancel"),
            (REVERT, "Undo changes"),
            (DESKTOP_PREVIEW, "Preview on desktop"),
        ] {
            button(hwnd, label, id);
        }
        styled::choice(
            hwnd,
            "Preview state",
            PREVIEW_MODE,
            &[
                "List view".into(),
                "With tabs".into(),
                "Collapsed".into(),
                "Icon view".into(),
            ],
            0,
        );
        preview::create(hwnd, PREVIEW, false);
        rebuild(hwnd);
        Ok(hwnd)
    }
}

fn rebuild(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let data = unsafe { &mut *pointer };
    data.building = true;
    let body = control(hwnd, BODY);
    unsafe {
        let mut child = GetWindow(body, GW_CHILD);
        while !child.is_null() {
            let next = GetWindow(child, GW_HWNDNEXT);
            DestroyWindow(child);
            child = next;
        }
        DestroyWindow(control(hwnd, SCOPE));
        for i in 0..5 {
            DestroyWindow(control(hwnd, 700 + i));
        }
    }
    let mut scopes = vec![crate::locale::text("Global design")];
    scopes.extend(
        data.draft
            .settings
            .windows
            .iter()
            .map(|p| p.panel_title.clone()),
    );
    styled::choice(
        hwnd,
        "Customize",
        SCOPE,
        &scopes,
        data.draft.scope.map_or(0, |i| i + 1),
    );
    unsafe {
        EnableWindow(control(hwnd, SCOPE), i32::from(!data.fixed_scope));
    }
    for (i, title) in ["Layout", "Glass", "Colors", "Typography", "Buttons"]
        .into_iter()
        .enumerate()
    {
        button(hwnd, title, 700 + i as u16);
    }
    styled::choice(
        body,
        "Color palette",
        PRESET,
        &std::iter::once("Choose a palette…".into())
            .chain(draft::PALETTES.iter().map(|(name, _)| (*name).into()))
            .collect::<Vec<_>>(),
        0,
    );
    for (index, field) in data
        .draft
        .editor
        .pages
        .iter()
        .flat_map(|p| &p.fields)
        .enumerate()
    {
        let id = 1000 + index as i32;
        let key = data.draft.editor.key(index);
        let value = &data.draft.values[index];
        match field {
            Input::Text(label, _) | Input::Folder(label, _) => {
                let label = label.split(" (").next().unwrap_or(label);
                child(body, "STATIC", label, 2000 + index as i32, 0);
                styled::edit(body, value, id);
                if is_color(key) || key == "font" {
                    button(
                        body,
                        if key == "font" {
                            "Choose…"
                        } else {
                            "Pick color"
                        },
                        3000 + index as u16,
                    );
                }
                if let Some((min, max, scale)) = number_range(key) {
                    let slider = child(
                        body,
                        "msctls_trackbar32",
                        label,
                        4000 + index as i32,
                        WS_TABSTOP | TBS_NOTICKS,
                    );
                    unsafe {
                        SetWindowSubclass(slider, Some(slider_proc), 92, 0);
                        SendMessageW(slider, TBM_SETRANGEMIN, 0, (min * scale) as isize);
                        SendMessageW(slider, TBM_SETRANGEMAX, 1, (max * scale) as isize);
                        SendMessageW(
                            slider,
                            TBM_SETPOS,
                            1,
                            (value.parse::<f64>().unwrap_or_else(|_| {
                                inherited_value(&data.draft, key)
                                    .parse()
                                    .unwrap_or(min as f64)
                            }) * scale as f64) as isize,
                        );
                    }
                }
                if data.draft.scope.is_some() {
                    button(body, "↶", 5000 + index as u16);
                    let inherited = inherited_value(&data.draft, key);
                    unsafe {
                        SendMessageW(
                            control(body, id),
                            EM_SETCUEBANNER,
                            1,
                            wide(format!("{}: {inherited}", crate::locale::text("Inherited")))
                                .as_ptr() as isize,
                        );
                    }
                }
            }
            Input::Choice(label, options, _) => {
                child(body, "STATIC", label, 2000 + index as i32, 0);
                styled::choice(body, label, id, options, value.parse().unwrap_or(0));
            }
            Input::Check(label, _) => {
                styled::check(body, label, id, value == "1");
            }
            Input::Action(..) => {}
        }
    }
    data.building = false;
    data.offset = 0;
    layout_editor(hwnd);
    update_preview(hwnd);
}
unsafe extern "system" fn slider_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if message == WM_MOUSEWHEEL {
        return unsafe { SendMessageW(GetParent(hwnd), message, w, l) };
    }
    if message == WM_SETFOCUS {
        unsafe {
            PostMessageW(GetParent(hwnd), form::REVEAL_FOCUS, hwnd as usize, 0);
        }
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}
fn is_color(key: &str) -> bool {
    matches!(key, "background" | "header" | "text" | "muted" | "accent")
}
fn number_range(key: &str) -> Option<(i32, i32, i32)> {
    match key {
        "opacity" => Some((10, 100, 1)),
        "item_size" => Some((9, 24, 2)),
        "title_size" => Some((10, 30, 2)),
        _ => None,
    }
}
fn inherited_value(draft: &Draft, key: &str) -> String {
    let mut panel = draft
        .scope
        .map(|i| draft.settings.windows[i].clone())
        .unwrap_or_default();
    if draft.scope.is_some() {
        draft.editor.apply_panel(&mut panel, &draft.values);
    }
    panel.appearance_overrides = Default::default();
    let theme = draft.settings.panel_theme(&panel);
    match key {
        "background" => theme.background_color,
        "header" => theme.header_color,
        "text" => theme.text_color,
        "muted" => theme.muted_text_color,
        "accent" => theme.accent_color,
        "font" => theme.font_family,
        "opacity" => theme.glass_opacity.to_string(),
        "item_size" => theme.item_font_size.to_string(),
        "title_size" => theme.title_font_size.to_string(),
        _ => String::new(),
    }
}

fn layout_editor(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let (width, height) = dimensions(hwnd);
    let split = (width * 46 / 100).max(280).min(width - 220);
    move_control(hwnd, SCOPE, 24, 94, width - 48, 280);
    let tab_width = (width - 48) / 5;
    for i in 0..5 {
        move_control(hwnd, 700 + i, 24 + i * tab_width, 146, tab_width - 5, 38);
    }
    move_control(hwnd, BODY, 24, 207, split - 40, (height - 300).max(80));
    let right = split + 24;
    let span = width - right - 24;
    move_control(hwnd, PREVIEW_MODE, right + 142, 202, span - 142, 240);
    let preview_height = (height - if height >= 650 { 458 } else { 408 }).clamp(44, 350);
    let collapsed = unsafe { SendMessageW(control(hwnd, PREVIEW_MODE), CB_GETCURSEL, 0, 0) } == 2;
    move_control(
        hwnd,
        PREVIEW,
        right,
        260,
        span,
        if collapsed { 64 } else { preview_height },
    );
    preview::layout(control(hwnd, PREVIEW));
    move_control(hwnd, DESKTOP_PREVIEW as i32, right, height - 140, span, 38);
    move_control(hwnd, FEEDBACK, 24, height - 88, width - 48, 26);
    move_control(hwnd, REVERT as i32, 24, height - 52, 156, 36);
    move_control(hwnd, IDCANCEL, width - 300, height - 52, 104, 36);
    move_control(hwnd, IDOK, width - 184, height - 52, 160, 36);
    layout_fields(hwnd);
    unsafe {
        InvalidateRect(hwnd, null(), 0);
    }
}
fn place(body: HWND, id: i32, bounds: Option<(i32, i32, i32, i32)>) {
    let child = control(body, id);
    if child.is_null() {
        return;
    }
    if let Some((x, y, width, height)) = bounds {
        move_control(body, id, x, y, width.max(1), height.max(1));
    }
    unsafe {
        ShowWindow(child, if bounds.is_some() { SW_SHOWNA } else { SW_HIDE });
    }
}
fn layout_fields(hwnd: HWND) {
    let data = unsafe { &mut *state(hwnd) };
    let body = control(hwnd, BODY);
    let (_, height) = dimensions(body);
    let start: usize = data
        .draft
        .editor
        .pages
        .iter()
        .take(data.page)
        .map(|p| p.fields.len())
        .sum();
    let end = start + data.draft.editor.pages[data.page].fields.len();
    let row_height = if data.page == 2 { 78 } else { 88 };
    data.extent = (end - start) as i32 * row_height + if data.page == 2 { 58 } else { 0 };
    data.offset = data.offset.clamp(0, (data.extent - height).max(0));
    unsafe {
        SetScrollInfo(
            body,
            SB_VERT,
            &SCROLLINFO {
                cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
                fMask: SIF_RANGE | SIF_PAGE | SIF_POS,
                nMax: data.extent - 1,
                nPage: height.max(0) as u32,
                nPos: data.offset,
                ..std::mem::zeroed()
            },
            1,
        );
    }
    let (width, _) = dimensions(body);
    let width = width - 12;
    place(
        body,
        PRESET,
        (data.page == 2).then_some((0, -data.offset, width, 280)),
    );
    let mut y = -data.offset + if data.page == 2 { 58 } else { 0 };
    for (index, field) in data
        .draft
        .editor
        .pages
        .iter()
        .flat_map(|p| &p.fields)
        .enumerate()
    {
        if !(start..end).contains(&index) {
            for base in [1000, 2000, 3000, 4000, 5000] {
                place(body, base + index as i32, None);
            }
            continue;
        }
        let id = 1000 + index as i32;
        let key = data.draft.editor.key(index);
        place(body, 2000 + index as i32, Some((0, y, width, 24)));
        match field {
            Input::Check(..) => place(body, id, Some((0, y + 4, width, 62))),
            Input::Choice(..) => place(body, id, Some((0, y + 28, width, 280))),
            Input::Text(..) | Input::Folder(..) => {
                let inherit = if data.draft.scope.is_some() { 34 } else { 0 };
                let available = width - inherit;
                if number_range(key).is_some() {
                    place(body, id, Some((10, y + 36, 52, 24)));
                    place(
                        body,
                        4000 + index as i32,
                        Some((82, y + 28, available - 82, 40)),
                    );
                } else {
                    let picker = if is_color(key) {
                        56
                    } else if key == "font" {
                        88
                    } else {
                        0
                    };
                    place(body, id, Some((10, y + 36, available - picker - 20, 24)));
                    if picker > 0 {
                        place(
                            body,
                            3000 + index as i32,
                            Some((available - picker + 4, y + 28, picker - 4, 40)),
                        );
                    }
                }
                if inherit > 0 {
                    place(
                        body,
                        5000 + index as i32,
                        Some((width - 30, y + 28, 30, 40)),
                    );
                }
            }
            _ => {}
        }
        y += row_height;
    }
    unsafe {
        RedrawWindow(
            body,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW,
        );
    }
}

fn update_preview(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() || unsafe { (*pointer).building } {
        return;
    }
    let result = unsafe { (*pointer).draft.preview() };
    match result {
        Ok((theme, panel)) => {
            let mode = unsafe { SendMessageW(control(hwnd, PREVIEW_MODE), CB_GETCURSEL, 0, 0) }
                .max(0) as usize;
            let desktop = unsafe { (*pointer).desktop };
            preview::update(control(hwnd, PREVIEW), &theme, &panel, mode, false);
            if !desktop.is_null() {
                preview::update(desktop, &theme, &panel, mode, true);
            }
            set_text(
                control(hwnd, FEEDBACK),
                &crate::locale::text(if unsafe { (*pointer).dirty } {
                    "Unsaved changes · preview only"
                } else {
                    "Changes appear here before you save."
                }),
            );
            unsafe {
                EnableWindow(control(hwnd, IDOK), 1);
            }
        }
        Err(error) => {
            set_text(control(hwnd, FEEDBACK), &crate::locale::text(&error));
            unsafe {
                EnableWindow(control(hwnd, IDOK), 0);
            }
        }
    }
}
fn changed(hwnd: HWND) {
    unsafe {
        (*state(hwnd)).dirty = true;
        // Coalesce edit notifications; incomplete HEX/number input retains the last valid preview.
        PostMessageW(hwnd, REFRESH, 0, 0);
    }
}
fn choose_color(hwnd: HWND, index: usize) {
    let pointer = state(hwnd);
    let current = unsafe { (&(*pointer).draft.values)[index].clone() };
    let fallback =
        unsafe { inherited_value(&(*pointer).draft, (*pointer).draft.editor.key(index)) };
    let mut colors = unsafe { (*pointer).custom_colors };
    let mut picker = CHOOSECOLORW {
        lStructSize: std::mem::size_of::<CHOOSECOLORW>() as u32,
        hwndOwner: hwnd,
        rgbResult: color(&current, color(&fallback, 0)),
        lpCustColors: colors.as_mut_ptr(),
        Flags: CC_FULLOPEN | CC_RGBINIT,
        ..unsafe { std::mem::zeroed() }
    };
    if unsafe { ChooseColorW(&mut picker) } != 0 {
        let rgb = picker.rgbResult;
        let value = format!(
            "#{:02X}{:02X}{:02X}",
            rgb & 255,
            (rgb >> 8) & 255,
            (rgb >> 16) & 255
        );
        set_text(control(control(hwnd, BODY), 1000 + index as i32), &value);
    }
    unsafe {
        (*pointer).custom_colors = colors;
    }
}
fn choose_font(hwnd: HWND, index: usize) {
    let mut font = LOGFONTW::default();
    let value = unsafe { (&(*state(hwnd)).draft.values)[index].clone() };
    for (out, input) in font
        .lfFaceName
        .iter_mut()
        .take(31)
        .zip(value.encode_utf16())
    {
        *out = input;
    }
    font.lfHeight = -16;
    let mut picker = CHOOSEFONTW {
        lStructSize: std::mem::size_of::<CHOOSEFONTW>() as u32,
        hwndOwner: hwnd,
        lpLogFont: &mut font,
        Flags: CF_SCREENFONTS | CF_INITTOLOGFONTSTRUCT | CF_NOSIZESEL | CF_NOSTYLESEL,
        ..unsafe { std::mem::zeroed() }
    };
    if unsafe { ChooseFontW(&mut picker) } != 0 {
        let end = font.lfFaceName.iter().position(|c| *c == 0).unwrap_or(32);
        set_text(
            control(control(hwnd, BODY), 1000 + index as i32),
            &String::from_utf16_lossy(&font.lfFaceName[..end]),
        );
    }
}

fn command(hwnd: HWND, w: WPARAM) {
    let id = (w & 0xffff) as i32;
    let code = (w >> 16) as u32;
    let pointer = state(hwnd);
    if unsafe { (*pointer).building } {
        return;
    }
    if id == IDCANCEL {
        unsafe {
            if (*pointer).embedded {
                emit(Event::PreferenceCancelled(THEMES));
            } else {
                (*pointer).done = true;
            }
        }
    } else if id == IDOK {
        match unsafe { (*pointer).draft.commit() } {
            Ok(()) => unsafe {
                if (*pointer).embedded {
                    emit(Event::PreferenceSaved(
                        THEMES,
                        form::Answer {
                            values: Vec::new(),
                            action: None,
                        },
                    ));
                } else {
                    (*pointer).accepted = true;
                    (*pointer).done = true;
                }
            },
            Err(error) => set_text(control(hwnd, FEEDBACK), &crate::locale::text(&error)),
        }
    } else if id == REVERT as i32 {
        unsafe {
            (*pointer).draft.revert();
            (*pointer).dirty = true;
        }
        rebuild(hwnd);
    } else if id == DESKTOP_PREVIEW as i32 {
        let old = unsafe { (*pointer).desktop };
        if old.is_null() || unsafe { IsWindow(old) } == 0 {
            let popup = preview::create(hwnd, 0, true);
            unsafe {
                (*pointer).desktop = popup;
            }
        }
        update_preview(hwnd);
        unsafe {
            ShowWindow((*pointer).desktop, SW_SHOWNOACTIVATE);
        }
    } else if id == PREVIEW_MODE && code == CBN_SELCHANGE {
        update_preview(hwnd);
        layout_editor(hwnd);
    } else if id == SCOPE && code == CBN_SELCHANGE {
        let selected = unsafe { SendMessageW(control(hwnd, SCOPE), CB_GETCURSEL, 0, 0) } as usize;
        match unsafe { (*pointer).draft.switch(selected.checked_sub(1)) } {
            Ok(()) => rebuild(hwnd),
            Err(error) => unsafe {
                SendMessageW(
                    control(hwnd, SCOPE),
                    CB_SETCURSEL,
                    (*pointer).draft.scope.map_or(0, |i| i + 1),
                    0,
                );
                set_text(control(hwnd, FEEDBACK), &crate::locale::text(&error));
            },
        }
    } else if id == PRESET && code == CBN_SELCHANGE {
        let selected =
            unsafe { SendMessageW(control(control(hwnd, BODY), PRESET), CB_GETCURSEL, 0, 0) };
        if selected > 0 {
            unsafe {
                (*pointer).draft.preset(selected as usize - 1);
                (*pointer).dirty = true;
            }
            rebuild(hwnd);
        }
    } else if (700..705).contains(&id) {
        unsafe {
            (*pointer).page = (id - 700) as usize;
            (*pointer).offset = 0;
        }
        layout_editor(hwnd);
        for id in 700..705 {
            unsafe {
                InvalidateRect(control(hwnd, id), null(), 0);
            }
        }
    } else if (3000..4000).contains(&id) {
        let index = (id - 3000) as usize;
        let key = unsafe { (*pointer).draft.editor.key(index).to_owned() };
        if is_color(&key) {
            choose_color(hwnd, index);
        } else if key == "font" {
            choose_font(hwnd, index);
        }
    } else if (5000..6000).contains(&id) {
        set_text(control(control(hwnd, BODY), id - 4000), "");
    } else if (1000..2000).contains(&id) && matches!(code, EN_CHANGE | CBN_SELCHANGE | BN_CLICKED) {
        let index = (id - 1000) as usize;
        let child = control(control(hwnd, BODY), id);
        let field = unsafe {
            (*pointer)
                .draft
                .editor
                .pages
                .iter()
                .flat_map(|p| &p.fields)
                .nth(index)
        };
        let value = match field {
            Some(Input::Choice(..)) => {
                unsafe { SendMessageW(child, CB_GETCURSEL, 0, 0) }.to_string()
            }
            Some(Input::Check(..)) => usize::from(
                unsafe { SendMessageW(child, BM_GETCHECK, 0, 0) } == BST_CHECKED as isize,
            )
            .to_string(),
            Some(_) => text(child),
            None => return,
        };
        unsafe {
            (&mut (*pointer).draft.values)[index] = value.clone();
        }
        let key = unsafe { (*pointer).draft.editor.key(index).to_owned() };
        if key == "preset" {
            rebuild(hwnd);
            unsafe {
                SetFocus(control(control(hwnd, BODY), id));
            }
        }
        if let Some((min, max, scale)) = number_range(&key) {
            let number = value.parse::<f64>().ok().or_else(|| {
                value
                    .is_empty()
                    .then(|| unsafe {
                        inherited_value(&(*pointer).draft, &key).parse::<f64>().ok()
                    })
                    .flatten()
            });
            if let Some(value) = number {
                unsafe {
                    SendMessageW(
                        control(control(hwnd, BODY), 4000 + index as i32),
                        TBM_SETPOS,
                        1,
                        (value.clamp(min as f64, max as f64) * scale as f64) as isize,
                    );
                }
            }
        }
        unsafe {
            InvalidateRect(control(control(hwnd, BODY), 3000 + index as i32), null(), 0);
        }
        changed(hwnd);
    }
}

fn paint(hwnd: HWND, dc: HDC, bounds: RECT) {
    let (width, height) = dimensions(hwnd);
    let split = (width * 46 / 100).max(280).min(width - 220);
    with_palette(hwnd, |p| {
        chrome::fill(dc, &bounds, p.background);
        chrome::label(
            dc,
            &crate::locale::text("Design studio"),
            rect(hwnd, 24, 16, width - 48, 40),
            chrome::font(hwnd, 2),
            p.text,
            DT_SINGLELINE | DT_VCENTER,
        );
        chrome::label(
            dc,
            &crate::locale::text("Your panels. Your style. Every detail."),
            rect(hwnd, 26, 58, width - 48, 24),
            chrome::font(hwnd, 0),
            p.muted,
            DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
        );
        chrome::fill(
            dc,
            &rect(hwnd, split, 207, 1, (height - 310).max(1)),
            p.border,
        );
        chrome::label(
            dc,
            &crate::locale::text("LIVE PREVIEW"),
            rect(hwnd, split + 24, 208, width - split - 48, 24),
            chrome::font(hwnd, 1),
            p.accent,
            DT_SINGLELINE | DT_VCENTER,
        );
        if height >= 650 {
            chrome::label(
                dc,
                &crate::locale::text("Sample files. View glass on your desktop."),
                rect(hwnd, split + 24, height - 186, width - split - 48, 40),
                chrome::font(hwnd, 0),
                p.muted,
                DT_WORDBREAK | DT_END_ELLIPSIS,
            );
        }
        chrome::fill(dc, &rect(hwnd, 24, height - 98, width - 48, 1), p.border);
    });
}
unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    match message {
        WM_SHOWWINDOW if w == 0 => unsafe {
            if !(*pointer).desktop.is_null() {
                ShowWindow((*pointer).desktop, SW_HIDE);
            }
        },
        WM_CLOSE => {
            command(hwnd, IDCANCEL as usize);
            return 0;
        }
        WM_NCDESTROY => unsafe {
            RemovePropW(hwnd, PROPERTY.as_ptr());
            let data = Box::from_raw(pointer);
            if IsWindow(data.desktop) != 0 {
                DestroyWindow(data.desktop);
            }
            chrome::release(hwnd);
            PANEL_PALETTES.with(|p| p.borrow_mut().remove(&(hwnd as usize)));
        },
        WM_GETMINMAXINFO => {
            unsafe {
                (*(l as *mut MINMAXINFO)).ptMinTrackSize = POINT {
                    x: chrome::scale(hwnd, 760),
                    y: chrome::scale(hwnd, 620),
                };
            }
            return 0;
        }
        WM_DPICHANGED_AFTERPARENT => {
            chrome::restyle(hwnd);
            chrome::restyle(control(hwnd, BODY));
            rebuild(hwnd);
            return 0;
        }
        WM_SIZE => {
            layout_editor(hwnd);
            return 0;
        }
        WM_DPICHANGED => unsafe {
            let r = &*(l as *const RECT);
            SetWindowPos(
                hwnd,
                null_mut(),
                r.left,
                r.top,
                r.right - r.left,
                r.bottom - r.top,
                SWP_NOZORDER | SWP_NOACTIVATE,
            );
            chrome::restyle(hwnd);
            chrome::restyle(control(hwnd, BODY));
            update_preview(hwnd);
            layout_editor(hwnd);
            return 0;
        },
        WM_ERASEBKGND => return 1,
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, bounds| paint(hwnd, dc, bounds),
            );
            return 0;
        }
        REFRESH => {
            update_preview(hwnd);
            return 0;
        }
        WM_COMMAND => {
            command(hwnd, w);
            return 0;
        }
        WM_MEASUREITEM => {
            unsafe {
                (*(l as *mut MEASUREITEMSTRUCT)).itemHeight = chrome::scale(hwnd, 32) as u32;
            }
            return 1;
        }
        WM_DRAWITEM => {
            let draw = unsafe { &*(l as *const DRAWITEMSTRUCT) };
            if draw.CtlType == ODT_COMBOBOX {
                chrome::draw_item(draw, styled::draw_choice);
                return 1;
            }
            if (700..705).contains(&draw.CtlID) {
                let active = unsafe { (*pointer).page == draw.CtlID as usize - 700 };
                chrome::draw_item(draw, |draw| {
                    with_palette(hwnd, |p| {
                        chrome::rounded(
                            draw.hDC,
                            &draw.rcItem,
                            chrome::scale(hwnd, 12),
                            if active { p.selected } else { p.surface },
                            if active || chrome::keyboard_focus(draw.hwndItem) {
                                p.accent
                            } else {
                                p.surface
                            },
                        );
                        chrome::label(
                            draw.hDC,
                            &text(draw.hwndItem),
                            draw.rcItem,
                            chrome::font(hwnd, 0),
                            if active { p.accent } else { p.text },
                            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                        );
                    })
                });
                return 1;
            }
            if (3000..4000).contains(&draw.CtlID) {
                let index = draw.CtlID as usize - 3000;
                if unsafe { is_color((*pointer).draft.editor.key(index)) } {
                    let value = unsafe { (&(*pointer).draft.values)[index].clone() };
                    let fallback = unsafe {
                        inherited_value(&(*pointer).draft, (*pointer).draft.editor.key(index))
                    };
                    chrome::draw_item(draw, |draw| {
                        with_palette(hwnd, |p| {
                            chrome::fill(draw.hDC, &draw.rcItem, p.background);
                            let mut r = draw.rcItem;
                            unsafe {
                                InflateRect(&mut r, -3, -3);
                            }
                            chrome::rounded(
                                draw.hDC,
                                &r,
                                chrome::scale(hwnd, 12),
                                color(&value, color(&fallback, p.surface)),
                                if chrome::keyboard_focus(draw.hwndItem) {
                                    p.accent
                                } else {
                                    p.muted
                                },
                            );
                        })
                    });
                    return 1;
                }
            }
        }
        _ => {}
    }
    if matches!(
        message,
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT | WM_CTLCOLORLISTBOX | WM_DRAWITEM
    ) {
        return unsafe { window_proc(hwnd, message, w, l) };
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}

unsafe extern "system" fn body_proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let parent = unsafe { GetParent(hwnd) };
    let pointer = state(parent);
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    match message {
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, bounds| {
                    with_palette(parent, |p| {
                        chrome::fill(dc, &bounds, p.background);
                        let data = unsafe { &*pointer };
                        for (index, field) in data
                            .draft
                            .editor
                            .pages
                            .iter()
                            .flat_map(|p| &p.fields)
                            .enumerate()
                        {
                            if !matches!(field, Input::Text(..) | Input::Folder(..)) {
                                continue;
                            }
                            let edit = control(hwnd, 1000 + index as i32);
                            if unsafe { GetWindowLongPtrW(edit, GWL_STYLE) as u32 & WS_VISIBLE }
                                == 0
                            {
                                continue;
                            }
                            let mut r = RECT::default();
                            unsafe {
                                GetWindowRect(edit, &mut r);
                                MapWindowPoints(null_mut(), hwnd, (&mut r as *mut RECT).cast(), 2);
                                InflateRect(
                                    &mut r,
                                    chrome::scale(parent, 9),
                                    chrome::scale(parent, 8),
                                );
                            }
                            chrome::rounded(
                                dc,
                                &r,
                                chrome::scale(parent, 12),
                                p.surface,
                                if unsafe { GetFocus() } == edit {
                                    p.accent
                                } else {
                                    p.border
                                },
                            );
                        }
                    })
                },
            );
            return 0;
        }
        WM_HSCROLL if l != 0 => {
            let index = unsafe { GetDlgCtrlID(l as HWND) } - 4000;
            if index >= 0 {
                let key = unsafe { (*pointer).draft.editor.key(index as usize) };
                if let Some((_, _, scale)) = number_range(key) {
                    let value =
                        unsafe { SendMessageW(l as HWND, WM_USER, 0, 0) } as f64 / scale as f64;
                    set_text(control(hwnd, 1000 + index), &value.to_string());
                }
            }
            return 0;
        }
        WM_VSCROLL | WM_MOUSEWHEEL | form::SCROLL_TO | form::REVEAL_FOCUS => {
            let (_, height) = dimensions(hwnd);
            let data = unsafe { &mut *pointer };
            if message == WM_MOUSEWHEEL {
                data.offset +=
                    data.wheel
                        .movement(-((w >> 16) as i16 as i32), scrolling::WHEEL_STEP, 1);
            } else if message == form::SCROLL_TO {
                data.offset = w as i32;
            } else if message == form::REVEAL_FOCUS {
                let mut r = RECT::default();
                unsafe {
                    GetWindowRect(w as HWND, &mut r);
                    MapWindowPoints(null_mut(), hwnd, (&mut r as *mut RECT).cast(), 2);
                }
                let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
                let top = r.top * 96 / dpi;
                let bottom = r.bottom * 96 / dpi;
                data.offset += if top < 8 {
                    top - 8
                } else if bottom > height - 8 {
                    bottom - height + 8
                } else {
                    0
                };
            } else {
                let mut info = SCROLLINFO {
                    cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
                    fMask: SIF_ALL,
                    ..unsafe { std::mem::zeroed() }
                };
                unsafe {
                    GetScrollInfo(hwnd, SB_VERT, &mut info);
                }
                data.offset = match w as u16 as i32 {
                    SB_LINEUP => data.offset - 40,
                    SB_LINEDOWN => data.offset + 40,
                    SB_PAGEUP => data.offset - height,
                    SB_PAGEDOWN => data.offset + height,
                    SB_THUMBTRACK | SB_THUMBPOSITION => info.nTrackPos,
                    _ => data.offset,
                };
            }
            layout_fields(parent);
            return 0;
        }
        WM_NOTIFY => {
            let header = unsafe { &*(l as *const NMHDR) };
            if header.code == NM_CUSTOMDRAW {
                let draw = unsafe { &*(l as *const NMCUSTOMDRAW) };
                if draw.dwDrawStage == CDDS_PREPAINT {
                    return CDRF_NOTIFYITEMDRAW as isize;
                }
                if draw.dwDrawStage == CDDS_ITEMPREPAINT {
                    with_palette(parent, |p| {
                        let r = draw.rc;
                        chrome::rounded(
                            draw.hdc,
                            &r,
                            chrome::scale(hwnd, 8),
                            if draw.dwItemSpec == TBCD_THUMB as usize {
                                p.accent
                            } else {
                                p.surface
                            },
                            p.border,
                        );
                    });
                    return CDRF_SKIPDEFAULT as isize;
                }
            }
        }
        WM_ERASEBKGND => return 1,
        WM_NCDESTROY => chrome::release(hwnd),
        WM_COMMAND | WM_DRAWITEM | WM_MEASUREITEM | WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT
        | WM_CTLCOLORLISTBOX => return unsafe { SendMessageW(parent, message, w, l) },
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
