//! Visual, transactional rule editor. Native buttons retain keyboard and accessibility semantics.
use super::*;
use crate::model::{Panel, SortRule};

const NAME: i32 = 1000;
const ENABLED: i32 = 1001;
const TARGET: i32 = 1002;
const NEW_TARGET: i32 = 1003;
const CUSTOM: i32 = 1004;
const ADD_CUSTOM: u16 = 410;
const ADD_GROUP: u16 = 411;
const SELECTION: u16 = 412;
const CLEAR: u16 = 413;
const TYPES: u16 = 420;
const GROUPS: u16 = 430;
const CHIPS: u16 = 2000;
const VIEWPORT: i32 = 800;
const ERROR: i32 = 802;
const TYPE_LABELS: [&str; 3] = ["File extensions", "Folders", "All other files"];
const TYPE_ICONS: [&str; 3] = ["\u{e8a5}", "\u{e8b7}", "\u{e8b3}"];
const GROUP_LABELS: [&str; 8] = [
    "Images",
    "Videos",
    "Audio",
    "Documents",
    "Archives",
    "Programs",
    "Links",
    "Code",
];
type Validator<'a> = dyn Fn(&[String]) -> Result<(), String> + 'a;

pub fn parse_extensions(source: &str) -> Result<Vec<String>, String> {
    let mut extensions = Vec::new();
    for token in source
        .split(|ch: char| ch.is_whitespace() || matches!(ch, ',' | ';'))
        .filter(|s| !s.is_empty())
    {
        let extension = token
            .strip_prefix("*.")
            .or_else(|| token.strip_prefix('.'))
            .unwrap_or(token)
            .to_lowercase();
        if extension.is_empty()
            || extension
                .chars()
                .any(|ch| ch.is_control() || ".<>:\"/\\|?*".contains(ch))
        {
            return Err(
                "Use extensions such as .pdf, jpg or *.png; no paths or multi-part extensions."
                    .into(),
            );
        }
        let extension = format!(".{extension}");
        if !extensions.contains(&extension) {
            extensions.push(extension);
        }
    }
    if extensions.is_empty() {
        return Err("Enter at least one extension, for example .pdf.".into());
    }
    Ok(extensions)
}

fn targets(panels: &[Panel]) -> Vec<String> {
    let mut names = Vec::<String>::new();
    let mut add = |name: &str| {
        if !name.trim().is_empty() && !names.iter().any(|other| other.eq_ignore_ascii_case(name)) {
            names.push(name.into());
        }
    };
    for panel in panels {
        let tabs = panel.tabs.as_deref().unwrap_or_default();
        for tab in tabs
            .iter()
            .filter(|tab| !tab.pinned() && !tab.folder_path.is_empty())
        {
            add(&tab.tab_name);
        }
        if tabs
            .get(panel.active_tab_index)
            .is_some_and(|tab| !tab.pinned() && !tab.folder_path.is_empty())
        {
            add(&panel.panel_title);
        }
    }
    names.sort_by_key(|name| name.to_lowercase());
    names
}

struct State<'a> {
    kind: usize,
    extensions: Vec<String>,
    groups: Vec<Vec<String>>,
    category: Option<usize>,
    chips: Vec<String>,
    targets: Vec<String>,
    offset: i32,
    extent: i32,
    wheel: scrolling::Wheel,
    result: Option<Vec<String>>,
    done: bool,
    editing: bool,
    validator: &'a Validator<'a>,
}

fn state(hwnd: HWND) -> *mut State<'static> {
    unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) as _ }
}

pub fn show(
    owner: HWND,
    rule: &SortRule,
    panels: &[Panel],
    editing: bool,
    validator: &Validator<'_>,
) -> Option<Vec<String>> {
    let mut data = State {
        kind: if rule.match_folders {
            1
        } else if rule.catch_all {
            2
        } else {
            0
        },
        extensions: parse_extensions(&rule.extensions.join(" "))
            .unwrap_or_else(|_| rule.extensions.clone()),
        groups: crate::model::default_rules()
            .into_iter()
            .filter(|rule| !rule.extensions.is_empty())
            .map(|rule| rule.extensions)
            .collect(),
        category: if rule.extensions.is_empty() {
            Some(3)
        } else {
            None
        },
        chips: Vec::new(),
        targets: targets(panels),
        offset: 0,
        extent: 0,
        wheel: scrolling::Wheel::default(),
        result: None,
        done: false,
        editing,
        validator,
    };
    unsafe {
        let instance = GetModuleHandleW(null());
        let class = wide("DesktopPlus.Rust.RuleEditor");
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
        let width = chrome::scale(owner, 960).min(work.right - work.left);
        let height = chrome::scale(owner, 800).min(work.bottom - work.top);
        let x = (owner_rect.left + (owner_rect.right - owner_rect.left - width) / 2)
            .clamp(work.left, (work.right - width).max(work.left));
        let y = (owner_rect.top + (owner_rect.bottom - owner_rect.top - height) / 2)
            .clamp(work.top, (work.bottom - height).max(work.top));
        let hwnd = CreateWindowExW(
            WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT,
            class.as_ptr(),
            wide(crate::locale::text(if editing {
                "Edit rule"
            } else {
                "New rule"
            }))
            .as_ptr(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_CLIPCHILDREN,
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
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, &mut data as *mut State<'_> as isize);
        child(hwnd, "STATIC", "Rule name", 900, 0);
        styled::edit(hwnd, &rule.rule_name, NAME);
        styled::check(hwnd, "Enabled", ENABLED, rule.enabled);
        for (i, title) in TYPE_LABELS.iter().enumerate() {
            button(hwnd, title, TYPES + i as u16);
        }
        for (i, title) in GROUP_LABELS.iter().enumerate() {
            button(hwnd, title, GROUPS + i as u16);
        }
        for (id, title) in [
            (ADD_CUSTOM, "Add extension"),
            (ADD_GROUP, "Select category"),
            (SELECTION, "Selected types"),
            (CLEAR, "Clear selection"),
            (IDOK as u16, "Save rule"),
            (IDCANCEL as u16, "Cancel"),
        ] {
            button(hwnd, title, id);
        }
        child(hwnd, "STATIC", "File types", 901, 0);
        child(
            hwnd,
            "STATIC",
            "Click to select or remove a file type.",
            902,
            0,
        );
        child(hwnd, "STATIC", "Target panel", 903, 0);
        child(hwnd, "STATIC", "New panel name", 904, 0);
        child(hwnd, "STATIC", "", ERROR, 0);
        styled::edit(hwnd, "", CUSTOM);
        SendMessageW(
            control(hwnd, CUSTOM),
            EM_SETCUEBANNER,
            0,
            wide(crate::locale::text("Own extension, e.g. .blend")).as_ptr() as isize,
        );
        let mut choices = vec![crate::locale::text("Create a new panel…")];
        // Decorated captions are not localization keys. Preserve user-defined target names exactly.
        choices.extend(data.targets.iter().map(|name| format!("▸ {name}")));
        let selection = data
            .targets
            .iter()
            .position(|name| name.eq_ignore_ascii_case(&rule.target_panel_name))
            .map_or(0, |index| index + 1);
        styled::choice(hwnd, "Target panel", TARGET, &choices, selection);
        styled::edit(hwnd, &rule.target_panel_name, NEW_TARGET);
        let body_class = wide("DesktopPlus.Rust.RuleChips");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(chips_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: body_class.as_ptr(),
            ..std::mem::zeroed()
        });
        let viewport = CreateWindowExW(
            WS_EX_CONTROLPARENT | WS_EX_COMPOSITED,
            body_class.as_ptr(),
            null(),
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_CLIPCHILDREN,
            0,
            0,
            1,
            1,
            hwnd,
            VIEWPORT as usize as _,
            instance,
            null(),
        );
        scrollbars::attach_form(viewport);
        rebuild_chips(hwnd);
        layout(hwnd);
        theme_frame(hwnd);
        EnableWindow(owner, 0);
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        SetFocus(control(hwnd, NAME));
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
            // Enter in the custom-extension input adds its contents without saving the entire rule.
            if msg.hwnd == control(hwnd, CUSTOM)
                && msg.message == WM_KEYDOWN
                && msg.wParam == VK_RETURN as usize
            {
                SendMessageW(hwnd, WM_COMMAND, ADD_CUSTOM as usize, 0);
            } else if IsDialogMessageW(hwnd, &msg) == 0 {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        DestroyWindow(hwnd);
        EnableWindow(owner, 1);
        SetForegroundWindow(owner);
    }
    data.result
}

fn target(hwnd: HWND) -> String {
    let selected = unsafe { SendMessageW(control(hwnd, TARGET), CB_GETCURSEL, 0, 0) };
    if selected > 0 {
        unsafe { &(*state(hwnd)).targets }
            .get(selected as usize - 1)
            .cloned()
            .unwrap_or_default()
    } else {
        text(control(hwnd, NEW_TARGET)).trim().into()
    }
}
fn values(hwnd: HWND) -> Vec<String> {
    let data = unsafe { &*state(hwnd) };
    vec![
        text(control(hwnd, NAME)),
        data.kind.to_string(),
        data.extensions.join(" "),
        target(hwnd),
        usize::from(
            unsafe { SendMessageW(control(hwnd, ENABLED), BM_GETCHECK, 0, 0) }
                == BST_CHECKED as isize,
        )
        .to_string(),
    ]
}

fn rebuild_chips(hwnd: HWND) {
    let pointer = state(hwnd);
    let viewport = control(hwnd, VIEWPORT);
    let old_count = unsafe { (*pointer).chips.len() };
    for i in 0..old_count {
        unsafe {
            DestroyWindow(control(viewport, CHIPS as i32 + i as i32));
        }
    }
    let chips = unsafe {
        (*pointer).offset = 0;
        (*pointer).wheel = scrolling::Wheel::default();
        (*pointer)
            .category
            .map(|i| (&(*pointer).groups)[i].clone())
            .unwrap_or_else(|| (*pointer).extensions.clone())
    };
    unsafe {
        (*pointer).chips = chips.clone();
    }
    for (index, extension) in chips.iter().enumerate() {
        button(viewport, extension, CHIPS + index as u16);
    }
    layout_chips(hwnd);
}

fn dimensions(hwnd: HWND) -> (i32, i32, i32, i32) {
    let mut r = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut r);
    }
    let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let width = r.right * 96 / dpi;
    let height = r.bottom * 96 / dpi;
    let right = (width * 31 / 100).clamp(238, 310);
    (width, height, width - right - 72, right)
}

fn layout(hwnd: HWND) {
    if state(hwnd).is_null() {
        return;
    }
    let (width, height, left, right) = dimensions(hwnd);
    let right_x = width - right - 24;
    move_control(hwnd, 900, 26, 92, width - 270, 24);
    move_control(hwnd, NAME, 38, 128, width - 296, 22);
    move_control(hwnd, ENABLED, width - 232, 120, 208, 38);
    let card = (width - 64) / 3;
    for i in 0..3 {
        move_control(hwnd, TYPES as i32 + i, 24 + i * (card + 8), 180, card, 60);
    }
    move_control(hwnd, 901, 26, 260, left - 190, 28);
    move_control(hwnd, SELECTION as i32, 24 + left - 180, 258, 180, 32);
    let category_width = (left - 24) / 4;
    for i in 0..8 {
        move_control(
            hwnd,
            GROUPS as i32 + i,
            24 + (i % 4) * (category_width + 8),
            306 + (i / 4) * 42,
            category_width,
            34,
        );
    }
    move_control(hwnd, ADD_GROUP as i32, 24, 398, (left - 8) / 2, 32);
    move_control(hwnd, CLEAR as i32, 28 + left / 2, 398, (left - 8) / 2, 32);
    move_control(hwnd, 902, 26, 438, left - 2, 24);
    move_control(hwnd, VIEWPORT, 24, 470, left, height - 598);
    move_control(hwnd, CUSTOM, 38, height - 104, left - 192, 22);
    move_control(
        hwnd,
        ADD_CUSTOM as i32,
        24 + left - 164,
        height - 112,
        164,
        38,
    );
    move_control(hwnd, 903, right_x + 2, 260, right - 2, 28);
    move_control(hwnd, TARGET, right_x, 300, right, 270);
    move_control(hwnd, 904, right_x + 2, 354, right - 2, 24);
    move_control(hwnd, NEW_TARGET, right_x + 14, 390, right - 28, 22);
    move_control(hwnd, IDOK, width - 284, height - 52, 140, 36);
    move_control(hwnd, IDCANCEL, width - 136, height - 52, 112, 36);
    move_control(hwnd, ERROR, 26, height - 52, width - 334, 36);
    let extensions = unsafe { (*state(hwnd)).kind == 0 };
    for id in [
        901,
        902,
        SELECTION as i32,
        ADD_GROUP as i32,
        CLEAR as i32,
        VIEWPORT,
        CUSTOM,
        ADD_CUSTOM as i32,
    ] {
        show_control(hwnd, id, extensions);
    }
    for i in 0..8 {
        show_control(hwnd, GROUPS as i32 + i, extensions);
    }
    let new_target = unsafe { SendMessageW(control(hwnd, TARGET), CB_GETCURSEL, 0, 0) <= 0 };
    show_control(hwnd, 904, new_target);
    show_control(hwnd, NEW_TARGET, new_target);
    unsafe {
        EnableWindow(
            control(hwnd, ADD_GROUP as i32),
            i32::from((*state(hwnd)).category.is_some()),
        );
    }
    layout_chips(hwnd);
    update_preview(hwnd);
}

fn layout_chips(hwnd: HWND) {
    let pointer = state(hwnd);
    let viewport = control(hwnd, VIEWPORT);
    let mut r = RECT::default();
    unsafe {
        GetClientRect(viewport, &mut r);
    }
    let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    // Reserve the scrollbar gutter even before Windows decides it is needed.
    let mut outer = RECT::default();
    unsafe {
        GetWindowRect(viewport, &mut outer);
    }
    let width = (outer.right - outer.left) * 96 / dpi - 18;
    let page = (r.bottom * 96 / dpi).max(1);
    let columns = ((width - 12) / 94).max(1);
    let count = unsafe { (*pointer).chips.len() } as i32;
    let extent = (((count + columns - 1) / columns) * 40).max(1);
    unsafe {
        (*pointer).extent = extent;
        (*pointer).offset = (*pointer).offset.clamp(0, (extent - page).max(0));
        SetScrollInfo(
            viewport,
            SB_VERT,
            &SCROLLINFO {
                cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
                fMask: SIF_RANGE | SIF_PAGE | SIF_POS,
                nMin: 0,
                nMax: extent - 1,
                nPage: page as u32,
                nPos: (*pointer).offset,
                nTrackPos: 0,
            },
            1,
        );
    }
    let chip_width = (width - 12 - (columns - 1) * 8) / columns;
    for i in 0..count {
        let y = 4 + i / columns * 40 - unsafe { (*pointer).offset };
        // Do not repaint controls individually while their neighbors still occupy old coordinates.
        unsafe {
            SetWindowPos(
                control(viewport, CHIPS as i32 + i),
                null_mut(),
                chrome::scale(hwnd, 6 + i % columns * (chip_width + 8)),
                chrome::scale(hwnd, y),
                chrome::scale(hwnd, chip_width),
                chrome::scale(hwnd, 32),
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW,
            );
        }
    }
    unsafe {
        RedrawWindow(
            viewport,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW,
        );
    }
}

fn update_preview(hwnd: HWND) {
    if state(hwnd).is_null() {
        return;
    }
    let count = unsafe { (*state(hwnd)).extensions.len() };
    set_text(
        control(hwnd, SELECTION as i32),
        &crate::locale::text("Selected ({count})").replace("{count}", &count.to_string()),
    );
    unsafe {
        EnableWindow(control(hwnd, CLEAR as i32), i32::from(count > 0));
        RedrawWindow(hwnd, null(), null_mut(), RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
}

fn suggest_names(hwnd: HWND) {
    let category = unsafe { (*state(hwnd)).category };
    let name = crate::locale::text(category.map_or("My files", |i| GROUP_LABELS[i]));
    if text(control(hwnd, NAME)).trim().is_empty() {
        set_text(control(hwnd, NAME), &name);
    }
    if text(control(hwnd, NEW_TARGET)).trim().is_empty() {
        set_text(control(hwnd, NEW_TARGET), &name);
    }
}

fn toggle(extensions: &mut Vec<String>, extension: &str) {
    if let Some(index) = extensions
        .iter()
        .position(|value| value.eq_ignore_ascii_case(extension))
    {
        extensions.remove(index);
    } else {
        extensions.push(extension.into());
    }
}

fn command(hwnd: HWND, w: WPARAM) {
    let pointer = state(hwnd);
    let id = w as u16;
    let code = (w >> 16) as u32;
    if id == IDCANCEL as u16 {
        unsafe {
            (*pointer).done = true;
        }
        return;
    }
    if id == IDOK as u16 {
        // Saving also accepts a custom extension still in the input, so it cannot be lost silently.
        let pending = text(control(hwnd, CUSTOM));
        if unsafe { (*pointer).kind == 0 } && !pending.trim().is_empty() {
            match parse_extensions(&pending) {
                Ok(extensions) => {
                    for extension in extensions {
                        if !unsafe { &(*pointer).extensions }.contains(&extension) {
                            unsafe {
                                (*pointer).extensions.push(extension);
                            }
                        }
                    }
                    set_text(control(hwnd, CUSTOM), "");
                    rebuild_chips(hwnd);
                }
                Err(error) => {
                    set_text(control(hwnd, ERROR), &crate::locale::text(&error));
                    return;
                }
            }
        }
        let values = values(hwnd);
        let result = unsafe { ((*pointer).validator)(&values) };
        match result {
            Ok(()) => unsafe {
                (*pointer).result = Some(values);
                (*pointer).done = true;
            },
            Err(error) => set_text(control(hwnd, ERROR), &crate::locale::text(&error)),
        }
        return;
    }
    if (TYPES..TYPES + 3).contains(&id) {
        unsafe {
            (*pointer).kind = (id - TYPES) as usize;
        }
        layout(hwnd);
    } else if (GROUPS..GROUPS + 8).contains(&id) || id == SELECTION {
        unsafe {
            (*pointer).category = if id == SELECTION {
                None
            } else {
                Some((id - GROUPS) as usize)
            };
        }
        rebuild_chips(hwnd);
        layout(hwnd);
    } else if id >= CHIPS {
        let extension = unsafe { &(*pointer).chips }
            .get((id - CHIPS) as usize)
            .cloned();
        if let Some(extension) = extension {
            unsafe {
                toggle(&mut (*pointer).extensions, &extension);
            }
            suggest_names(hwnd);
        }
    } else if id == ADD_GROUP {
        if let Some(index) = unsafe { (*pointer).category } {
            let group = unsafe { (&(*pointer).groups)[index].clone() };
            for extension in group {
                if !unsafe { &(*pointer).extensions }.contains(&extension) {
                    unsafe {
                        (*pointer).extensions.push(extension);
                    }
                }
            }
            suggest_names(hwnd);
        }
    } else if id == CLEAR {
        unsafe {
            (*pointer).extensions.clear();
        }
    } else if id == ADD_CUSTOM {
        match parse_extensions(&text(control(hwnd, CUSTOM))) {
            Ok(extensions) => {
                for extension in extensions {
                    if !unsafe { &(*pointer).extensions }.contains(&extension) {
                        unsafe {
                            (*pointer).extensions.push(extension);
                        }
                    }
                }
                unsafe {
                    (*pointer).category = None;
                }
                suggest_names(hwnd);
                set_text(control(hwnd, CUSTOM), "");
                rebuild_chips(hwnd);
                layout(hwnd);
                scroll(hwnd, i32::MAX, true);
                unsafe {
                    SetFocus(control(hwnd, CUSTOM));
                }
            }
            Err(error) => {
                set_text(control(hwnd, ERROR), &crate::locale::text(&error));
                return;
            }
        }
    } else if id == TARGET as u16 && code == CBN_SELCHANGE {
        layout(hwnd);
    } else if !matches!(id as i32, NAME | NEW_TARGET | CUSTOM | ENABLED) {
        return;
    }
    set_text(control(hwnd, ERROR), "");
    update_preview(hwnd);
}

fn area(hwnd: HWND, x: i32, y: i32, width: i32, height: i32) -> RECT {
    RECT {
        left: chrome::scale(hwnd, x),
        top: chrome::scale(hwnd, y),
        right: chrome::scale(hwnd, x + width),
        bottom: chrome::scale(hwnd, y + height),
    }
}

fn paint(hwnd: HWND, dc: HDC, bounds: RECT) {
    let (width, height, left, right) = dimensions(hwnd);
    let right_x = width - right - 24;
    let data = unsafe { &*state(hwnd) };
    with_palette(hwnd, |p| {
        chrome::fill(dc, &bounds, p.background);
        chrome::label(
            dc,
            &crate::locale::text(if data.editing {
                "Edit rule"
            } else {
                "New rule"
            }),
            area(hwnd, 24, 16, width - 48, 36),
            chrome::font(hwnd, 2),
            p.text,
            DT_SINGLELINE | DT_VCENTER,
        );
        chrome::label(
            dc,
            &crate::locale::text("Pick file types. Choose their destination."),
            area(hwnd, 26, 56, width - 52, 26),
            chrome::font(hwnd, 0),
            p.muted,
            DT_SINGLELINE | DT_VCENTER,
        );
        for id in [NAME, CUSTOM, NEW_TARGET] {
            let edit = control(hwnd, id);
            if unsafe { IsWindowVisible(edit) } == 0 {
                continue;
            }
            let mut r = RECT::default();
            unsafe {
                GetWindowRect(edit, &mut r);
                MapWindowPoints(null_mut(), hwnd, (&mut r as *mut RECT).cast(), 2);
                InflateRect(&mut r, chrome::scale(hwnd, 12), chrome::scale(hwnd, 8));
            }
            chrome::rounded(
                dc,
                &r,
                chrome::scale(hwnd, 12),
                p.surface,
                if unsafe { GetFocus() == edit } {
                    p.accent
                } else {
                    p.border
                },
            );
        }
        let new_target = unsafe { SendMessageW(control(hwnd, TARGET), CB_GETCURSEL, 0, 0) <= 0 };
        chrome::label(
            dc,
            &crate::locale::text(if new_target {
                "A folder and panel are created when sorting."
            } else {
                "Matching files move into this folder panel or tab."
            }),
            area(
                hwnd,
                right_x + 2,
                if new_target { 430 } else { 354 },
                right - 4,
                if height < 700 && new_target { 42 } else { 64 },
            ),
            chrome::font(hwnd, 0),
            p.muted,
            DT_WORDBREAK,
        );
        let summary_top = if height < 700 { 480 } else { 526 };
        let summary = area(
            hwnd,
            right_x,
            summary_top,
            right,
            (height - summary_top - 78).max(80),
        );
        chrome::rounded(dc, &summary, chrome::scale(hwnd, 16), p.surface, p.border);
        chrome::label(
            dc,
            &crate::locale::text("RULE PREVIEW"),
            area(hwnd, right_x + 16, summary_top + 14, right - 32, 22),
            chrome::font(hwnd, 1),
            p.accent,
            DT_SINGLELINE,
        );
        let matching = if data.kind == 0 {
            crate::locale::text("{count} file types")
                .replace("{count}", &data.extensions.len().to_string())
        } else {
            crate::locale::text(TYPE_LABELS[data.kind])
        };
        let target = target(hwnd);
        let destination = if target.is_empty() {
            crate::locale::text("Choose a destination")
        } else {
            target
        };
        chrome::label(
            dc,
            &format!("{matching}\n→ {destination}"),
            area(
                hwnd,
                right_x + 16,
                summary_top + 42,
                right - 32,
                (height - summary_top - 132).max(30),
            ),
            chrome::font(hwnd, 0),
            p.text,
            DT_WORDBREAK,
        );
        if data.kind != 0 {
            chrome::rounded(
                dc,
                &area(hwnd, 24, 260, left, height - 334),
                chrome::scale(hwnd, 16),
                p.surface,
                p.border,
            );
            chrome::label(
                dc,
                TYPE_ICONS[data.kind],
                area(hwnd, 48, 296, left - 48, 68),
                chrome::font(hwnd, 3),
                p.accent,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE,
            );
            chrome::label(
                dc,
                &crate::locale::text(TYPE_LABELS[data.kind]),
                area(hwnd, 48, 384, left - 48, 30),
                chrome::font(hwnd, 1),
                p.text,
                DT_CENTER | DT_SINGLELINE,
            );
            chrome::label(
                dc,
                &crate::locale::text(if data.kind == 1 {
                    "Moves whole desktop folders. No file extensions needed."
                } else {
                    "Catches files without a matching extension rule. Folders are excluded."
                }),
                area(hwnd, 62, 432, left - 76, 92),
                chrome::font(hwnd, 0),
                p.muted,
                DT_CENTER | DT_WORDBREAK,
            );
        }
        chrome::fill(dc, &area(hwnd, 0, height - 64, width, 1), p.border);
    });
}

fn draw_button(hwnd: HWND, draw: &DRAWITEMSTRUCT) {
    if draw.itemState & ODS_DISABLED != 0 {
        with_palette(hwnd, |p| {
            chrome::fill(draw.hDC, &draw.rcItem, p.background);
            chrome::rounded(
                draw.hDC,
                &draw.rcItem,
                chrome::scale(hwnd, 12),
                p.surface,
                p.surface,
            );
            chrome::label(
                draw.hDC,
                &text(draw.hwndItem),
                draw.rcItem,
                chrome::font(hwnd, 0),
                p.muted,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
        });
        return;
    }
    let data = unsafe { &*state(hwnd) };
    let id = draw.CtlID as u16;
    let is_type = (TYPES..TYPES + 3).contains(&id);
    let is_group = (GROUPS..GROUPS + 8).contains(&id);
    let is_chip = id >= CHIPS;
    let selected = if is_type {
        data.kind == (id - TYPES) as usize
    } else if is_group {
        data.category == Some((id - GROUPS) as usize)
    } else if is_chip {
        data.chips.get((id - CHIPS) as usize).is_some_and(|chip| {
            data.extensions
                .iter()
                .any(|ext| ext.eq_ignore_ascii_case(chip))
        })
    } else {
        id == SELECTION && data.category.is_none()
    };
    if !is_type && !is_group && !is_chip && id != SELECTION {
        chrome::draw_button(draw);
        return;
    }
    with_palette(hwnd, |p| {
        let hot = chrome::is_hot(draw.hwndItem);
        let focus = chrome::keyboard_focus(draw.hwndItem);
        chrome::fill(draw.hDC, &draw.rcItem, p.background);
        let mut r = draw.rcItem;
        unsafe {
            InflateRect(&mut r, -1, -1);
        }
        chrome::rounded(
            draw.hDC,
            &r,
            chrome::scale(hwnd, if is_chip { 16 } else { 12 }),
            if selected {
                p.selected
            } else if hot {
                p.hover
            } else {
                p.surface
            },
            if selected || focus {
                p.accent
            } else {
                p.border
            },
        );
        let mut caption = text(draw.hwndItem);
        let mut label = r;
        if is_type {
            let icon = RECT {
                left: r.left + chrome::scale(hwnd, 14),
                right: r.left + chrome::scale(hwnd, 44),
                ..r
            };
            chrome::label(
                draw.hDC,
                TYPE_ICONS[(id - TYPES) as usize],
                icon,
                chrome::font(hwnd, 3),
                if selected { p.accent } else { p.muted },
                DT_CENTER | DT_SINGLELINE | DT_VCENTER,
            );
            label.left += chrome::scale(hwnd, 52);
            label.right -= chrome::scale(hwnd, 8);
        } else if is_chip {
            caption = format!("{} {}", if selected { "✓" } else { "+" }, caption);
        }
        chrome::label(
            draw.hDC,
            &caption,
            label,
            chrome::font(hwnd, usize::from(selected && !is_chip)),
            if selected { p.accent } else { p.text },
            DT_VCENTER
                | DT_SINGLELINE
                | DT_END_ELLIPSIS
                | if is_type { DT_LEFT } else { DT_CENTER },
        );
    });
}

fn scroll(hwnd: HWND, offset: i32, absolute: bool) {
    let pointer = state(hwnd);
    unsafe {
        (*pointer).offset = if absolute {
            offset
        } else {
            (*pointer).offset + offset
        };
    }
    layout_chips(hwnd);
}

unsafe extern "system" fn chips_proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let parent = unsafe { GetParent(hwnd) };
    let pointer = state(parent);
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    match message {
        form::SCROLL_TO => {
            scroll(parent, w as i32, true);
            return 0;
        }
        form::REVEAL_FOCUS => {
            let mut r = RECT::default();
            let mut client = RECT::default();
            unsafe {
                GetWindowRect(w as HWND, &mut r);
                MapWindowPoints(null_mut(), hwnd, (&mut r as *mut RECT).cast(), 2);
                GetClientRect(hwnd, &mut client);
            }
            let delta = if r.top < 0 {
                r.top
            } else {
                (r.bottom - client.bottom).max(0)
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
        WM_MOUSEWHEEL => {
            let delta = unsafe {
                (*pointer)
                    .wheel
                    .movement(-((w >> 16) as i16 as i32), scrolling::WHEEL_STEP, 1)
            };
            scroll(parent, delta, false);
            return 0;
        }
        WM_VSCROLL => {
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
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, bounds| {
                    with_palette(parent, |p| {
                        chrome::fill(dc, &bounds, p.background);
                        if unsafe { (*pointer).chips.is_empty() } {
                            chrome::label(
                                dc,
                                &crate::locale::text(
                                    "Pick a category above or add your own extension.",
                                ),
                                bounds,
                                chrome::font(hwnd, 0),
                                p.muted,
                                DT_WORDBREAK,
                            );
                        }
                    })
                },
            );
            return 0;
        }
        WM_COMMAND | WM_DRAWITEM => return unsafe { SendMessageW(parent, message, w, l) },
        WM_ERASEBKGND => return 1,
        WM_NCDESTROY => chrome::release(hwnd),
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}

unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    if message == WM_GETMINMAXINFO {
        let info = unsafe { &mut *(l as *mut MINMAXINFO) };
        info.ptMinTrackSize = POINT {
            x: chrome::scale(hwnd, 820),
            y: chrome::scale(hwnd, 700),
        };
        return 0;
    }
    let pointer = state(hwnd);
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    match message {
        WM_CLOSE => {
            unsafe {
                (*pointer).done = true;
            }
            return 0;
        }
        WM_SIZE => {
            layout(hwnd);
            return 0;
        }
        WM_DPICHANGED => {
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
            chrome::restyle(control(hwnd, VIEWPORT));
            layout(hwnd);
            return 0;
        }
        WM_COMMAND => {
            command(hwnd, w);
            return 0;
        }
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, bounds| paint(hwnd, dc, bounds),
            );
            return 0;
        }
        WM_ERASEBKGND => return 1,
        WM_DRAWITEM => {
            let draw = unsafe { &*(l as *const DRAWITEMSTRUCT) };
            chrome::draw_item(draw, |draw| {
                if draw.CtlType == ODT_COMBOBOX {
                    styled::draw_choice(draw);
                } else {
                    draw_button(hwnd, draw);
                }
            });
            return 1;
        }
        WM_MEASUREITEM => {
            unsafe {
                (*(l as *mut MEASUREITEMSTRUCT)).itemHeight = chrome::scale(hwnd, 30) as u32;
            }
            return 1;
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT | WM_CTLCOLORLISTBOX => {
            return unsafe { window_proc(hwnd, message, w, l) };
        }
        WM_NCDESTROY => {
            chrome::release(hwnd);
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            }
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn removing_one_chip_preserves_other_categories_and_custom_types() {
        let mut extensions = vec![".pdf".into(), ".jpg".into(), ".blend".into()];
        toggle(&mut extensions, ".PDF");
        assert_eq!(extensions, [".jpg", ".blend"]);
        toggle(&mut extensions, ".pdf");
        assert_eq!(extensions, [".jpg", ".blend", ".pdf"]);
    }
    #[test]
    fn targets_only_offer_real_folder_destinations_and_preserve_names() {
        let mut panel = Panel::folder(std::path::Path::new("C:\\Files"));
        panel.panel_title = "Images".into();
        panel.tabs.as_mut().unwrap()[0].tab_name = "images".into();
        let mut pinned = panel.clone();
        pinned.panel_title = "Pinned".into();
        pinned.tabs.as_mut().unwrap()[0].panel_type = "List".into();
        assert_eq!(targets(&[panel, pinned]), ["images"]);
    }

    #[test]
    #[ignore = "Focused visual review of only the visual rule editor"]
    fn preview_visual_rule_editor() {
        unsafe {
            InitCommonControlsEx(&INITCOMMONCONTROLSEX {
                dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
                dwICC: ICC_STANDARD_CLASSES,
            });
        }
        crate::locale::configure(
            "de",
            &std::env::temp_dir().join("desktopplus-rule-editor-languages"),
        );
        let mut panel = Panel::folder(std::path::Path::new("C:\\Projects"));
        panel.panel_title = "Projekte".into();
        panel.tabs.as_mut().unwrap()[0].tab_name = "Projekte".into();
        let rule = crate::model::default_rules().remove(8);
        unsafe extern "system" fn finish(_: HWND, _: u32, _: usize, _: u32) {
            unsafe {
                PostQuitMessage(0);
            }
        }
        let timer = unsafe { SetTimer(null_mut(), 0, 60000, Some(finish)) };
        println!("Rule editor preview PID: {}", std::process::id());
        let result = show(null_mut(), &rule, &[panel], true, &|values| {
            if values[0].is_empty() || values[3].is_empty() {
                return Err("Enter a rule name.".into());
            }
            if values[1] == "0" {
                parse_extensions(&values[2])?;
            }
            Ok(())
        });
        unsafe {
            KillTimer(null_mut(), timer);
        }
        println!("Rule editor result: {result:?}");
    }
}
