//! Persistent rules workspace with accessible native controls and an in-place guide.
use super::*;
use crate::model::{AutoSort, SortRule};

const ADD: u16 = 400;
const EDIT: u16 = 401;
const ENABLE: u16 = 402;
const REMOVE: u16 = 403;
const GUIDE: u16 = 404;
const AUTOMATIC: u16 = 405;
const TEST: u16 = 406;
const GUIDE_TEXT: i32 = 407;
const DETAIL: i32 = 408;
const EMPTY: i32 = 409;

pub enum Action {
    Add,
    Edit(String),
    Toggle(String),
    Delete(String),
    Automatic(bool),
    Sort,
    Test,
}

#[derive(Default)]
struct State {
    rules: Vec<SortRule>,
    guide: bool,
    refreshing: bool,
    row_images: isize,
}

fn state(hwnd: HWND) -> *mut State {
    unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut State }
}

const CAPTIONS: &[(u16, &str)] = &[
    (ADD, "+ New rule"),
    (EDIT, "Edit rule"),
    (ENABLE, "Enable / disable"),
    (REMOVE, "Delete rule"),
    (GUIDE, "How it works"),
    (TEST, "Test a filename…"),
    (SORT_NOW, "Sort desktop now"),
    (AUTOMATIC, "Automatically sort"),
];
const COLUMNS: &[&str] = &["Rule name", "Matches", "Target panel", "Status"];
const GUIDE_SECTIONS: &[(&str, &str)] = &[
    (
        "1. Create a rule",
        "Click New rule and give it a name, for example My PDFs. Choose a rule type: file extensions, folders, or all other files.",
    ),
    (
        "2. Choose what matches",
        "Choose File extensions, open a category such as Documents, and click .pdf. Highlighted chips are selected; click again to remove them. Select category adds every type in that category. Selected shows all your choices. You can also add your own extension. Folders and All other files need no extensions.",
    ),
    (
        "3. Choose a target panel",
        "Choose an existing folder panel or tab from Target panel, or choose Create a new panel and give it a name. A new folder and panel are created when sorting. The rule preview shows the current selection and destination. Pinned lists are not folder targets.",
    ),
    (
        "4. Save, test, then sort",
        "Save the rule, then use Test a filename with invoice.pdf to check its destination without moving anything. Sort desktop now moves matching desktop items. Automatically sort also handles new desktop items.",
    ),
    (
        "Which rule wins?",
        "Only enabled rules are used. Custom extension rules take priority over built-in extension rules. Within each group, the first matching rule in the list wins. For folders and all other files, the first enabled rule of that type wins.",
    ),
    (
        "What happens to my files?",
        "Sorting moves files; it does not just display them in a panel. A backup is created before moves; use Backups to restore. Editing, disabling or deleting a rule does not move previously sorted files back. Items without a matching rule stay on the desktop.",
    ),
];

pub fn embed(owner: HWND, settings: &AutoSort) -> Result<HWND, String> {
    unsafe {
        let class = wide("DesktopPlus.Rust.SortRules");
        let instance = GetModuleHandleW(null());
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
            wide(crate::locale::text("Sort rules")).as_ptr(),
            WS_CHILD | WS_CLIPCHILDREN,
            0,
            0,
            1,
            1,
            owner,
            dashboard::form_id(AUTO_SORT) as usize as _,
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(shell::error_text());
        }
        SetWindowLongPtrW(
            hwnd,
            GWLP_USERDATA,
            Box::into_raw(Box::<State>::default()) as isize,
        );
        child(hwnd, "STATIC", "Sort rules", TITLE, 0);
        child(
            hwnd,
            "STATIC",
            "Your rules. A place for every file.",
            SUBTITLE,
            0,
        );
        styled::check(
            hwnd,
            "Automatically sort",
            AUTOMATIC as i32,
            settings.auto_sort_enabled,
        );
        for (id, caption) in CAPTIONS.iter().filter(|(id, _)| *id != AUTOMATIC) {
            button(hwnd, caption, *id);
        }
        let list = child(
            hwnd,
            "SysListView32",
            "Sort rules",
            LIST,
            WS_TABSTOP | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS | LVS_SHAREIMAGELISTS,
        );
        SendMessageW(
            list,
            LVM_SETEXTENDEDLISTVIEWSTYLE,
            0,
            (LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_LABELTIP) as isize,
        );
        for (index, caption) in COLUMNS.iter().enumerate() {
            let mut label = wide(crate::locale::text(caption));
            let column = LVCOLUMNW {
                mask: LVCF_TEXT | LVCF_WIDTH,
                pszText: label.as_mut_ptr(),
                cx: 140,
                ..std::mem::zeroed()
            };
            SendMessageW(list, LVM_INSERTCOLUMNW, index, &column as *const _ as isize);
        }
        chrome::setup_list(list, hwnd, false);
        child(
            hwnd,
            "STATIC",
            "No rules yet. Create your first rule above.",
            EMPTY,
            0,
        );
        for id in [GUIDE_TEXT, DETAIL] {
            let edit = child(
                hwnd,
                "EDIT",
                "",
                id,
                WS_TABSTOP
                    | ES_MULTILINE as u32
                    | ES_READONLY as u32
                    | ES_AUTOVSCROLL as u32
                    | if id == GUIDE_TEXT { WS_VSCROLL } else { 0 },
            );
            if id == GUIDE_TEXT {
                scrollbars::attach_form(edit);
                SetWindowSubclass(edit, Some(guide_proc), 41, 0);
            }
        }
        child(hwnd, "STATIC", "", STATUS, 0);
        restyle(hwnd);
        refresh(hwnd, settings);
        Ok(hwnd)
    }
}

pub fn refresh(hwnd: HWND, settings: &AutoSort) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let selected_id = selected_rule(hwnd).map(|rule| rule.rule_id.clone());
    // Notifications are synchronous; do not hold a mutable State reference while sending messages.
    unsafe {
        (*pointer).refreshing = true;
        (*pointer).rules = settings.rules.clone();
    }
    for (id, caption) in CAPTIONS {
        set_text(control(hwnd, *id as i32), &crate::locale::text(caption));
    }
    set_text(control(hwnd, TITLE), &crate::locale::text("Sort rules"));
    set_text(
        control(hwnd, SUBTITLE),
        &crate::locale::text("Your rules. A place for every file."),
    );
    set_text(
        control(hwnd, EMPTY),
        &crate::locale::text("No rules yet. Create your first rule above."),
    );
    let guide = GUIDE_SECTIONS
        .iter()
        .map(|(title, body)| {
            format!(
                "{}\r\n{}",
                crate::locale::text(title),
                crate::locale::text(body)
            )
        })
        .collect::<Vec<_>>()
        .join("\r\n\r\n");
    set_text(control(hwnd, GUIDE_TEXT), &guide);
    unsafe {
        SendMessageW(
            control(hwnd, AUTOMATIC as i32),
            BM_SETCHECK,
            usize::from(settings.auto_sort_enabled),
            0,
        );
        let list = control(hwnd, LIST);
        SendMessageW(list, WM_SETREDRAW, 0, 0);
        SendMessageW(list, LVM_DELETEALLITEMS, 0, 0);
        for (index, caption) in COLUMNS.iter().enumerate() {
            let mut label = wide(crate::locale::text(caption));
            let column = LVCOLUMNW {
                mask: LVCF_TEXT,
                pszText: label.as_mut_ptr(),
                ..std::mem::zeroed()
            };
            SendMessageW(list, LVM_SETCOLUMNW, index, &column as *const _ as isize);
        }
        for (index, rule) in settings.rules.iter().enumerate() {
            let cells = [
                rule.rule_name.clone(),
                matching_text(rule),
                rule.target_panel_name.clone(),
                crate::locale::text(if rule.enabled { "Enabled" } else { "Disabled" }),
            ];
            for (column, value) in cells.iter().enumerate() {
                let mut value = wide(value);
                let item = LVITEMW {
                    mask: LVIF_TEXT,
                    iItem: index as i32,
                    iSubItem: column as i32,
                    pszText: value.as_mut_ptr(),
                    ..std::mem::zeroed()
                };
                SendMessageW(
                    list,
                    if column == 0 {
                        LVM_INSERTITEMW
                    } else {
                        LVM_SETITEMW
                    },
                    0,
                    &item as *const _ as isize,
                );
            }
        }
        if let Some(index) =
            selected_id.and_then(|id| settings.rules.iter().position(|rule| rule.rule_id == id))
        {
            let item = LVITEMW {
                stateMask: LVIS_SELECTED | LVIS_FOCUSED,
                state: LVIS_SELECTED | LVIS_FOCUSED,
                ..std::mem::zeroed()
            };
            SendMessageW(list, LVM_SETITEMSTATE, index, &item as *const _ as isize);
            SendMessageW(list, LVM_ENSUREVISIBLE, index, 0);
        }
        SendMessageW(list, WM_SETREDRAW, 1, 0);
        InvalidateRect(list, null(), 1);
        (*pointer).refreshing = false;
    }
    selection_changed(hwnd);
    layout(hwnd);
}

fn matching_text(rule: &SortRule) -> String {
    if rule.match_folders {
        crate::locale::text("Folders")
    } else if rule.catch_all {
        crate::locale::text("All other files")
    } else {
        rule.extensions.join(" ")
    }
}

fn selected_rule(hwnd: HWND) -> Option<SortRule> {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return None;
    }
    selected(hwnd)
        .first()
        .and_then(|index| unsafe { &(*pointer).rules }.get(*index))
        .cloned()
}

fn selection_changed(hwnd: HWND) {
    let rule = selected_rule(hwnd);
    for id in [EDIT, ENABLE, REMOVE] {
        unsafe {
            EnableWindow(control(hwnd, id as i32), i32::from(rule.is_some()));
        }
    }
    set_text(
        control(hwnd, ENABLE as i32),
        &crate::locale::text(match &rule {
            Some(rule) if rule.enabled => "Disable rule",
            Some(_) => "Enable rule",
            None => "Enable / disable",
        }),
    );
    let detail = rule
        .map(|rule| {
            format!(
                "{} · {}\r\n{} → {}",
                crate::locale::text(if rule.is_built_in {
                    "Built-in rule"
                } else {
                    "Custom rule"
                }),
                rule.rule_name,
                matching_text(&rule),
                rule.target_panel_name
            )
        })
        .unwrap_or_else(|| {
            crate::locale::text("Select a rule to edit it. New here? Open How it works below.")
        });
    set_text(control(hwnd, DETAIL), &detail);
}

pub fn status(hwnd: HWND, caption: &str) {
    if !hwnd.is_null() {
        set_text(control(hwnd, STATUS), &crate::locale::text(caption));
    }
}

pub fn select_rule(hwnd: HWND, id: &str) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let index = unsafe { &(*pointer).rules }
        .iter()
        .position(|rule| rule.rule_id == id);
    if let Some(index) = index {
        unsafe {
            let list = control(hwnd, LIST);
            let item = LVITEMW {
                stateMask: LVIS_SELECTED | LVIS_FOCUSED,
                state: LVIS_SELECTED | LVIS_FOCUSED,
                ..std::mem::zeroed()
            };
            SendMessageW(list, LVM_SETITEMSTATE, index, &item as *const _ as isize);
            SendMessageW(list, LVM_ENSUREVISIBLE, index, 0);
        }
    }
}

pub fn busy(hwnd: HWND, sorting: bool) {
    if !hwnd.is_null() {
        unsafe {
            EnableWindow(control(hwnd, SORT_NOW as i32), i32::from(!sorting));
        }
        if sorting {
            status(hwnd, "Sorting desktop…");
        }
    }
}

unsafe extern "system" fn guide_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if message == form::SCROLL_TO {
        let line = unsafe { SendMessageW(hwnd, EM_GETFIRSTVISIBLELINE, 0, 0) };
        unsafe {
            SendMessageW(hwnd, EM_LINESCROLL, 0, w as isize - line);
        }
        return 0;
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}

pub fn restyle(hwnd: HWND) {
    if state(hwnd).is_null() {
        return;
    }
    chrome::restyle(hwnd);
    theme_list(control(hwnd, LIST));
    unsafe {
        let images = ImageList_Create(1, chrome::scale(hwnd, 34), ILC_COLOR32, 1, 0);
        SendMessageW(
            control(hwnd, LIST),
            LVM_SETIMAGELIST,
            LVSIL_SMALL as usize,
            images,
        );
        let old = std::mem::replace(&mut (*state(hwnd)).row_images, images);
        if old != 0 {
            ImageList_Destroy(old);
        }
    }
    layout(hwnd);
}

fn layout(hwnd: HWND) {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return;
    }
    let guide = unsafe { (*pointer).guide };
    let mut rect = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut rect);
    }
    let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let width = rect.right * 96 / dpi;
    let height = rect.bottom * 96 / dpi;
    let body = (width - 48).max(1);
    move_control(hwnd, TITLE, 24, 18, body, 42);
    move_control(hwnd, SUBTITLE, 26, 64, body - 2, 26);
    move_control(hwnd, AUTOMATIC as i32, 24, 102, body - 190, 32);
    move_control(hwnd, SORT_NOW as i32, width - 204, 98, 180, 38);
    let cell = (body - 24) / 4;
    for (index, id) in [ADD, EDIT, ENABLE, REMOVE].iter().enumerate() {
        move_control(
            hwnd,
            *id as i32,
            24 + index as i32 * (cell + 8),
            148,
            cell,
            36,
        );
    }
    move_control(hwnd, LIST, 24, 198, body, height - 356);
    move_control(hwnd, EMPTY, 36, 242, body - 24, 28);
    move_control(hwnd, DETAIL, 28, height - 146, body - 8, 58);
    move_control(
        hwnd,
        GUIDE as i32,
        24,
        if guide { 102 } else { height - 76 },
        180,
        36,
    );
    move_control(hwnd, TEST as i32, 214, height - 76, 190, 36);
    move_control(hwnd, STATUS, 26, height - 33, body - 2, 24);
    move_control(hwnd, GUIDE_TEXT, 26, 158, body - 2, height - 190);
    for id in [AUTOMATIC, SORT_NOW, ADD, EDIT, ENABLE, REMOVE, TEST] {
        show_control(hwnd, id as i32, !guide);
    }
    for id in [LIST, DETAIL, STATUS] {
        show_control(hwnd, id, !guide);
    }
    show_control(
        hwnd,
        EMPTY,
        !guide && unsafe { (*pointer).rules.is_empty() },
    );
    show_control(hwnd, GUIDE_TEXT, guide);
    set_text(
        control(hwnd, GUIDE as i32),
        &crate::locale::text(if guide {
            "Back to rules"
        } else {
            "How it works"
        }),
    );
    for (column, width) in [
        (0, body * 25 / 100),
        (1, body * 28 / 100),
        (2, body * 27 / 100),
        (3, body * 20 / 100 - 20),
    ] {
        unsafe {
            SendMessageW(
                control(hwnd, LIST),
                LVM_SETCOLUMNWIDTH,
                column,
                chrome::scale(hwnd, width.max(70)) as isize,
            );
        }
    }
    unsafe {
        InvalidateRect(hwnd, null(), 0);
    }
}

unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let pointer = state(hwnd);
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, w, l) };
    }
    match message {
        WM_NCDESTROY => unsafe {
            let data = Box::from_raw(pointer);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            if data.row_images != 0 {
                ImageList_Destroy(data.row_images);
            }
            chrome::release(hwnd);
        },
        WM_SIZE => {
            layout(hwnd);
            return 0;
        }
        WM_DPICHANGED_AFTERPARENT => {
            restyle(hwnd);
            return 0;
        }
        WM_ERASEBKGND => return 1,
        WM_PAINT | WM_PRINTCLIENT => {
            chrome::paint(
                hwnd,
                (message == WM_PRINTCLIENT).then_some(w as HDC),
                |dc, bounds| {
                    with_palette(hwnd, |p| chrome::fill(dc, &bounds, p.background));
                },
            );
            return 0;
        }
        WM_COMMAND if (w >> 16) as u32 == BN_CLICKED => {
            let action = match w as u16 {
                ADD => Some(Action::Add),
                EDIT => selected_rule(hwnd).map(|rule| Action::Edit(rule.rule_id)),
                ENABLE => selected_rule(hwnd).map(|rule| Action::Toggle(rule.rule_id)),
                REMOVE => selected_rule(hwnd).map(|rule| Action::Delete(rule.rule_id)),
                AUTOMATIC => Some(Action::Automatic(unsafe {
                    SendMessageW(control(hwnd, AUTOMATIC as i32), BM_GETCHECK, 0, 0) != 0
                })),
                SORT_NOW => Some(Action::Sort),
                TEST => Some(Action::Test),
                GUIDE => {
                    unsafe {
                        (*pointer).guide = !(*pointer).guide;
                    }
                    layout(hwnd);
                    None
                }
                _ => None,
            };
            if let Some(action) = action {
                emit(Event::SortRules(action));
            }
            return 0;
        }
        WM_NOTIFY => {
            let header = unsafe { &*(l as *const NMHDR) };
            if header.idFrom == LIST as usize {
                match header.code {
                    LVN_ITEMCHANGED if !unsafe { (*pointer).refreshing } => selection_changed(hwnd),
                    NM_DBLCLK => {
                        if let Some(rule) = selected_rule(hwnd) {
                            emit(Event::SortRules(Action::Edit(rule.rule_id)));
                        }
                    }
                    LVN_KEYDOWN => {
                        let key = unsafe { &*(l as *const NMLVKEYDOWN) }.wVKey;
                        if key == VK_RETURN {
                            if let Some(rule) = selected_rule(hwnd) {
                                emit(Event::SortRules(Action::Edit(rule.rule_id)));
                            }
                        }
                    }
                    NM_CUSTOMDRAW => {
                        let draw = unsafe { &mut *(l as *mut NMLVCUSTOMDRAW) };
                        if draw.nmcd.dwDrawStage == CDDS_PREPAINT {
                            return CDRF_NOTIFYITEMDRAW as isize;
                        }
                        if draw.nmcd.dwDrawStage == CDDS_ITEMPREPAINT {
                            with_palette(hwnd, |p| {
                                draw.clrText = p.text;
                                draw.clrTextBk = p.background;
                            });
                        }
                    }
                    _ => {}
                }
            }
        }
        WM_DRAWITEM if l != 0 => {
            let draw = unsafe { &*(l as *const DRAWITEMSTRUCT) };
            if draw.itemState & ODS_DISABLED != 0 {
                chrome::draw_item(draw, |draw| {
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
                    })
                });
                return 1;
            }
            return unsafe { window_proc(hwnd, message, w, l) };
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT => {
            return unsafe { window_proc(hwnd, message, w, l) };
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
