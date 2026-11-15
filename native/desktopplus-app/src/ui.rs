mod app;
mod chrome;
mod content;
mod dashboard;
mod dialog;
mod drag_drop;
mod empty_panel;
mod form;
mod glass;
mod grip;
mod icon_grid;
mod images;
mod layout_builder;
mod marquee;
mod menus;
mod motion;
mod panel_options;
mod scrollbars;
mod styled;
mod text_render;
mod titlebar;

use crate::{
    model::{HeaderStyle, Theme},
    shell::{self, wide},
    worker::Row,
};
pub use app::{restore_before_uninstall, run};
use std::{
    cell::RefCell,
    collections::{HashMap, VecDeque},
    path::PathBuf,
    ptr::{null, null_mut},
    sync::Arc,
};
use windows_sys::Win32::{
    Foundation::*,
    Graphics::Gdi::*,
    System::{
        LibraryLoader::*,
        SystemServices::{MK_CONTROL, SS_CENTERIMAGE, SS_PATHELLIPSIS},
    },
    UI::{Controls::*, HiDpi::*, Input::KeyboardAndMouse::*, Shell::*, WindowsAndMessaging::*},
};

const CLASS: &str = "DesktopPlus.Rust.Window";
const TRAY_MESSAGE: u32 = WM_APP + 1;
const SAVE_TIMER: usize = 1;
const FOREGROUND_TIMER: usize = 2;
const SMOKE_TIMER: usize = 3;
const SORT_TIMER: usize = 4;
const LIST: i32 = 100;
const TABS: i32 = 101;
const SEARCH: i32 = 102;
const STATUS: i32 = 103;
const TITLE: i32 = 104;
const SUBTITLE: i32 = 105;
const SOURCE: i32 = 106;
const NEW_FOLDER: u16 = 200;
const NEW_LIST: u16 = 201;
const TOGGLE: u16 = 202;
const GENERAL: u16 = 203;
const EXIT: u16 = 204;
const OPEN: u16 = 205;
const REVEAL: u16 = 206;
const COPY: u16 = 207;
const CUT: u16 = 208;
const PASTE: u16 = 209;
const DELETE: u16 = 210;
const RENAME: u16 = 211;
const PROPERTIES: u16 = 212;
const REFRESH: u16 = 213;
const UP: u16 = 214;
const HOME: u16 = 215;
const ADD_TAB: u16 = 216;
const VIEW: u16 = 217;
const PANEL_SETTINGS: u16 = 218;
const COLLAPSE: u16 = 219;
const CLOSE_TAB: u16 = 220;
const RENAME_TAB: u16 = 221;
const DETACH_TAB: u16 = 222;
const TAB_LEFT: u16 = 223;
const TAB_RIGHT: u16 = 224;
const NEW_DIRECTORY: u16 = 225;
const LAYOUTS: u16 = 226;
const BACKUPS: u16 = 227;
const AUTO_SORT: u16 = 228;
const THEMES: u16 = 229;
const REMOVE_PANEL: u16 = 230;
const HIDE_PANEL: u16 = 231;
const HELP: u16 = 232;
const SORT_NOW: u16 = 233;
const MERGE_TAB: u16 = 234;
const NEW_RECYCLE: u16 = 235;
const EMPTY_RECYCLE: u16 = 236;
const WINDOWS_MENU: u16 = 237;
const COLUMNS: u16 = 238;
const SEARCH_TOGGLE: u16 = 239;
const PANEL_HEADER_HEIGHT: i32 = 48;
const MOVE_PANEL: u16 = 241;
const RESIZE_GRIP: u16 = 242;
const FIT_CONTENT: u16 = 243;
const OVERVIEW: u16 = 244;
const SHORTCUTS: u16 = 245;
const EMPTY_PANEL: u16 = 246;
const LAYOUT_BUILDER: u16 = 247;
const HOVER_TIMER: usize = 5;
const ZOOM_TIMER: usize = 6;
const TRAY_RETRY_TIMER: usize = 7;
const UPDATE_TIMER: usize = 8;
const ZOOM_STATUS_TIMER: usize = 9;

enum Event {
    PreferenceSaved(u16, form::Answer),
    PreferenceCancelled(u16),
    Command(usize, u16),
    HeaderDoubleClick(usize),
    Close(usize),
    Geometry(usize),
    Search(usize),
    SearchVisibility(usize, bool),
    Tab(usize, usize),
    Activate(usize, usize),
    Column(usize, usize),
    Context(usize, POINT),
    Drop(usize, Vec<PathBuf>, bool, bool, Option<PathBuf>),
    Hotkey(i32),
    Timer(usize),
    Tray(u32),
    Drag(usize),
    Image(usize, images::ImageSpec, PathBuf, u64, bool),
    Metadata(usize, PathBuf, u64, usize),
    Columns(usize),
    TaskbarCreated,

    Hover(usize),
    Zoom(usize, i32),
    ApplyZoom(usize),
    HideZoom(usize),
    DisplayScale(usize),
}
struct ViewData {
    source: Option<content::Source>,
    rows: Arc<Vec<Row>>,
    main: Vec<Vec<Vec<u16>>>,
    is_main: bool,
    workspace_page: u16,
    photos: bool,
    details: bool,
    single_click: bool,
    columns: [bool; 10],
    collapsed: bool,
    tab_count: usize,
    kind: String,
    loaded: bool,
    searching: bool,
    search_open: bool,
    options: panel_options::Options,
    hover_tracking: bool,
    zoom: f64,
}
thread_local! {
    static TASKBAR_CREATED: std::cell::Cell<u32> = const { std::cell::Cell::new(0) };
    static EVENTS: RefCell<VecDeque<Event>> = const { RefCell::new(VecDeque::new()) };
    static VIEWS: RefCell<HashMap<usize, ViewData>> = RefCell::new(HashMap::new());
    static PALETTE: RefCell<Palette> = RefCell::new(Palette::new(&Theme::default()));
    static METADATA: RefCell<MetadataCache> = RefCell::new(MetadataCache::default());
    static PANEL_PALETTES: RefCell<HashMap<usize, Palette>> = RefCell::new(HashMap::new());
}
fn with_palette<T>(hwnd: HWND, apply: impl FnOnce(&Palette) -> T) -> T {
    PANEL_PALETTES.with(|palettes| {
        let palettes = palettes.borrow();
        // A form body, nested control or owned dialog belongs to its panel's
        // theme too. GetParent follows both child parents and popup owners.
        let mut owner = hwnd;
        while !owner.is_null() {
            if let Some(palette) = palettes.get(&(owner as usize)) {
                return apply(palette);
            }
            owner = unsafe { GetParent(owner) };
        }
        PALETTE.with(|palette| apply(&palette.borrow()))
    })
}
fn emit(event: Event) {
    let wake = EVENTS.with(|events| {
        let mut events = events.borrow_mut();
        // A native popup runs its own message loop. Keep only one hover check
        // queued while it is open, instead of accumulating timer callbacks.
        if let Event::Hover(hwnd) = &event {
            if events
                .iter()
                .any(|queued| matches!(queued, Event::Hover(other) if other == hwnd))
            {
                return false;
            }
        }
        let wake = events.is_empty();
        events.push_back(event);
        wake
    });
    // Commands sent by accessibility clients may arrive while GetMessage is
    // dispatching a sent message. Wake the queue so the command runs at once.
    if wake {
        unsafe {
            PostThreadMessageW(
                windows_sys::Win32::System::Threading::GetCurrentThreadId(),
                WM_APP + 50,
                0,
                0,
            );
        }
    }
}

type MetadataKey = (PathBuf, u64, usize);
#[derive(Default)]
struct MetadataCache {
    entries: HashMap<MetadataKey, (Vec<u16>, u64)>,
    pending: std::collections::HashSet<MetadataKey>,
    sequence: u64,
}
impl MetadataCache {
    fn text(&mut self, hwnd: usize, row: &Row, column: usize) -> Vec<u16> {
        let key = (row.path.clone(), row.modified, column);
        self.sequence += 1;
        if let Some((value, used)) = self.entries.get_mut(&key) {
            *used = self.sequence;
            return value.clone();
        }
        if self.pending.len() < 128 && self.pending.insert(key) {
            emit(Event::Metadata(
                hwnd,
                row.path.clone(),
                row.modified,
                column,
            ));
        }
        Vec::new()
    }
    fn insert(&mut self, key: MetadataKey, value: Option<Vec<u16>>) {
        self.pending.remove(&key);
        let Some(value) = value else {
            return;
        };
        if self.entries.len() >= 1024 {
            if let Some(old) = self
                .entries
                .iter()
                .min_by_key(|(_, (_, used))| *used)
                .map(|(key, _)| key.clone())
            {
                self.entries.remove(&old);
            }
        }
        self.sequence += 1;
        self.entries.insert(key, (value, self.sequence));
    }
}

fn theme_frame(hwnd: HWND) {
    use windows_sys::Win32::Graphics::Dwm::*;
    let background = with_palette(hwnd, |palette| palette.background);
    let dark = i32::from(
        ((background & 255) + ((background >> 8) & 255) + ((background >> 16) & 255)) < 384,
    );
    unsafe {
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_IMMERSIVE_DARK_MODE as _,
            (&dark as *const i32).cast(),
            4,
        );
        let corners = DWMWCP_ROUND as u32;
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE as _,
            (&corners as *const u32).cast(),
            4,
        );
        let border = with_palette(hwnd, |palette| palette.border);
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_BORDER_COLOR as _,
            (&border as *const u32).cast(),
            4,
        );
        // Otherwise Windows can tint the caption with the user's system accent,
        // making the same application theme look different on another machine.
        let (caption, text) = with_palette(hwnd, |palette| (palette.surface, palette.text));
        for (attribute, color) in [(DWMWA_CAPTION_COLOR, caption), (DWMWA_TEXT_COLOR, text)] {
            DwmSetWindowAttribute(hwnd, attribute as _, (&color as *const u32).cast(), 4);
        }
    }
}

fn refresh_theme(hwnd: HWND) {
    // Theme changes are atomic. Hover/selection transitions may animate inside
    // the new palette, but must not keep blending colors from the old theme.
    unsafe extern "system" fn reset(hwnd: HWND, _: LPARAM) -> i32 {
        motion::reset_colors(hwnd);
        1
    }
    motion::reset_colors(hwnd);
    unsafe { EnumChildWindows(hwnd, Some(reset), 0) };
    chrome::restyle(hwnd);
    theme_frame(hwnd);
    theme_list(control(hwnd, LIST));
    unsafe {
        RedrawWindow(
            hwnd,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN,
        );
    }
}

struct Palette {
    background: u32,
    text: u32,
    muted: u32,
    accent: u32,
    brush: HBRUSH,
    button: HBRUSH,
    font: HFONT,
    heading: HFONT,
    family: String,
    size: f64,
    title_size: f64,
    surface: u32,
    border: u32,
    hover: u32,
    selected: u32,
    surface_brush: HBRUSH,
}
impl Palette {
    fn new(theme: &Theme) -> Self {
        let background = color(&theme.background_color, 0x332824);
        let surface = color(&theme.header_color, blend(background, 0xffffff, 5));
        unsafe {
            Self {
                background,
                family: theme.font_family.clone(),
                size: theme.item_font_size.clamp(9.0, 24.0),
                title_size: theme.title_font_size.clamp(10.0, 30.0),
                surface,
                border: blend(background, color(&theme.text_color, 0xffffff), 12),
                hover: blend(surface, color(&theme.text_color, 0xffffff), 7),
                selected: blend(background, color(&theme.accent_color, 0xff8b6e), 20),
                surface_brush: CreateSolidBrush(surface),
                text: color(&theme.text_color, 0xfaf5f2),
                muted: color(&theme.muted_text_color, 0xc0b0a7),
                accent: color(&theme.accent_color, 0xff8b6e),
                brush: CreateSolidBrush(background),
                button: CreateSolidBrush(color(&theme.header_color, 0x3b302a)),
                font: chrome::create_font(
                    &theme.font_family,
                    theme.item_font_size.clamp(9.0, 24.0),
                    400,
                    96,
                ),
                heading: chrome::create_font(&theme.font_family, 30.0, 600, 96),
            }
        }
    }
}
impl Drop for Palette {
    fn drop(&mut self) {
        unsafe {
            DeleteObject(self.brush);
            DeleteObject(self.button);
            DeleteObject(self.font);
            DeleteObject(self.heading);
            DeleteObject(self.surface_brush);
        }
    }
}
fn blend(a: u32, b: u32, amount: u32) -> u32 {
    let mix = |shift: u32| {
        ((((a >> shift) & 255u32) * (100 - amount) + ((b >> shift) & 255u32) * amount) / 100)
            << shift
    };
    mix(0) | mix(8) | mix(16)
}
fn color(text: &str, fallback: u32) -> u32 {
    let hex = text.trim_start_matches('#');
    let hex = if hex.len() == 8 { &hex[2..] } else { hex };
    if hex.len() != 6 {
        return fallback;
    }
    u32::from_str_radix(hex, 16)
        .map(|rgb| ((rgb & 255) << 16) | (rgb & 0xff00) | (rgb >> 16))
        .unwrap_or(fallback)
}
fn message(owner: HWND, text: &str) {
    unsafe {
        MessageBoxW(
            owner,
            wide(text).as_ptr(),
            wide("DesktopPlus").as_ptr(),
            MB_OK | MB_ICONINFORMATION,
        );
    }
}
fn confirm(owner: HWND, text: &str) -> bool {
    unsafe {
        MessageBoxW(
            owner,
            wide(text).as_ptr(),
            wide("DesktopPlus").as_ptr(),
            MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2,
        ) == IDYES
    }
}
fn text(hwnd: HWND) -> String {
    let length = unsafe { GetWindowTextLengthW(hwnd) }.max(0) as usize;
    let mut text = vec![0; length + 1];
    unsafe {
        GetWindowTextW(hwnd, text.as_mut_ptr(), text.len() as i32);
    }
    String::from_utf16_lossy(&text[..length])
}
fn set_text(hwnd: HWND, value: &str) {
    unsafe {
        let parent = GetParent(hwnd);
        let panel_status = GetDlgCtrlID(hwnd) == STATUS && chrome::panel(parent);
        // Native STATIC controls can paint synchronously during WM_SETTEXT.
        // Hide an empty status first so clearing it cannot leave an opaque
        // rectangle over the panel's glass background.
        if panel_status && value.is_empty() {
            show_control(parent, STATUS, false);
        }
        SetWindowTextW(hwnd, wide(value).as_ptr());
        if panel_status {
            show_panel_status(parent);
            InvalidateRect(hwnd, null(), 0);
        }
    }
}
fn show_panel_status(hwnd: HWND) {
    let collapsed = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .is_some_and(|view| view.collapsed)
    });
    show_control(
        hwnd,
        STATUS,
        !collapsed && !text(control(hwnd, STATUS)).is_empty(),
    );
}
fn child(parent: HWND, class: &str, caption: &str, id: i32, style: u32) -> HWND {
    unsafe {
        let caption = if class == "EDIT" {
            caption.to_owned()
        } else {
            crate::locale::text(caption)
        };
        let hwnd = CreateWindowExW(
            0,
            wide(class).as_ptr(),
            wide(&caption).as_ptr(),
            WS_CHILD | WS_VISIBLE | style,
            0,
            0,
            100,
            28,
            parent,
            id as usize as HMENU,
            GetModuleHandleW(null()),
            null(),
        );
        SendMessageW(
            hwnd,
            WM_SETFONT,
            chrome::font(parent, if id == TITLE { 2 } else { 0 }) as usize,
            1,
        );
        if class == "STATIC" {
            styled::label(hwnd);
        }
        hwnd
    }
}
fn button(parent: HWND, caption: &str, id: u16) -> HWND {
    let hwnd = child(
        parent,
        "BUTTON",
        caption,
        id as i32,
        WS_TABSTOP | BS_OWNERDRAW as u32,
    );
    chrome::setup_button(hwnd);
    hwnd
}
fn control(hwnd: HWND, id: i32) -> HWND {
    unsafe { GetDlgItem(hwnd, id) }
}
fn show_control(hwnd: HWND, id: i32, visible: bool) {
    unsafe {
        let child = control(hwnd, id);
        if !child.is_null()
            && (GetWindowLongPtrW(child, GWL_STYLE) as u32 & WS_VISIBLE != 0) != visible
        {
            ShowWindow(child, if visible { SW_SHOWNA } else { SW_HIDE });
        }
    }
}
fn move_control(hwnd: HWND, id: i32, x: i32, y: i32, width: i32, height: i32) {
    let scale = unsafe { GetDpiForWindow(hwnd) }.max(96) as f64 / 96.0;
    unsafe {
        let child = control(hwnd, id);
        if child.is_null() {
            return;
        }
        let (x, y, width, height) = (
            (x as f64 * scale) as i32,
            (y as f64 * scale) as i32,
            (width.max(0) as f64 * scale) as i32,
            (height.max(0) as f64 * scale) as i32,
        );
        let mut old = RECT::default();
        GetWindowRect(child, &mut old);
        MapWindowPoints(null_mut(), hwnd, (&mut old as *mut RECT).cast(), 2);
        if (
            old.left,
            old.top,
            old.right - old.left,
            old.bottom - old.top,
        ) == (x, y, width, height)
        {
            return;
        }
        // Queue painting until all siblings are positioned. MoveWindow(TRUE)
        // paints each control synchronously during every animation frame.
        SetWindowPos(
            child,
            null_mut(),
            x,
            y,
            width,
            height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS | SWP_DEFERERASE,
        );
    }
}
fn layout(hwnd: HWND) {
    let mut rect = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut rect);
    }
    let scale = unsafe { GetDpiForWindow(hwnd) }.max(96) as f64 / 96.0;
    let width = (rect.right as f64 / scale) as i32;
    let height = motion::content_height(hwnd).map_or(rect.bottom, |height| {
        let mut outer = RECT::default();
        unsafe {
            GetWindowRect(hwnd, &mut outer);
        }
        height - ((outer.bottom - outer.top) - rect.bottom)
    });
    let height = (height as f64 / scale) as i32;
    let main = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } == 1;
    if main {
        dashboard::layout(hwnd, width, height);
    } else if chrome::panel(hwnd) {
        let collapsed = VIEWS.with(|views| {
            views
                .borrow()
                .get(&(hwnd as usize))
                .is_some_and(|v| v.collapsed)
        });
        layout_panel_header(hwnd);
        show_panel_status(hwnd);
        for id in [LIST, RESIZE_GRIP as i32] {
            let visible = !collapsed
                && (id != RESIZE_GRIP as i32
                    || VIEWS.with(|v| {
                        v.borrow()
                            .get(&(hwnd as usize))
                            .is_some_and(|v| v.options.movement != "locked")
                    }));
            unsafe {
                ShowWindow(control(hwnd, id), if visible { SW_SHOWNA } else { SW_HIDE });
            }
        }
        if !collapsed {
            move_control(hwnd, RESIZE_GRIP as i32, width - 23, height - 23, 20, 20);
            let body_y = chrome::header_height(hwnd) + 8;
            icon_grid::layout(hwnd, 10, body_y, width - 20, height - body_y - 32);
            move_control(hwnd, STATUS, 14, height - 26, width - 48, 20);
        }
    }
    unsafe {
        InvalidateRect(hwnd, null(), 0);
    }
}

// Search motion only reflows the header; the file list stays at rest.
fn layout_panel_header(hwnd: HWND) {
    let mut bounds = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut bounds);
    }
    let width = bounds.right * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let tab_count = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map(|v| v.tab_count)
            .unwrap_or_default()
    });
    let header = chrome::header_layout(hwnd, width);
    for (id, position) in [
        (PANEL_SETTINGS, header.settings),
        (MOVE_PANEL, header.movement),
        (UP, header.up),
        (SEARCH_TOGGLE, header.search_button),
        (EMPTY_PANEL, header.empty),
        (COLLAPSE, header.collapse),
        (HIDE_PANEL, header.close),
    ] {
        if let Some(x) = position {
            move_control(hwnd, id as i32, x, header.button_top, 26, 28);
        }
        show_control(hwnd, id as i32, position.is_some());
    }
    for id in [TABS, HOME as i32, VIEW as i32, SOURCE, SEARCH] {
        let visible = match id {
            TABS => tab_count > 1 && header.identity_width > 6 + tab_count as i32,
            SEARCH => header.search_width > 0,
            _ => false,
        };
        show_control(hwnd, id, visible);
    }
    if tab_count > 1 && header.identity_width > 6 + tab_count as i32 {
        move_control(
            hwnd,
            TABS,
            header.identity_left,
            header.identity_top,
            header.identity_width,
            header.identity_height,
        );
        let tab_width = ((header.identity_width - 6) / tab_count as i32).max(1);
        unsafe {
            SendMessageW(
                control(hwnd, TABS),
                TCM_SETITEMSIZE,
                0,
                ((chrome::scale(hwnd, header.identity_height - 2) << 16)
                    | chrome::scale(hwnd, tab_width)) as isize,
            );
        }
    }
    move_control(
        hwnd,
        SEARCH,
        header.search_left + 10,
        header.button_top + 4,
        (header.search_width - 20 - header.search_icon_space).max(1),
        20,
    );
    chrome::refresh_search_hover(hwnd);
}

fn create_list(parent: HWND, columns: &[(&str, i32)]) -> HWND {
    let list = child(
        parent,
        "SysListView32",
        "",
        LIST,
        WS_TABSTOP
            | LVS_REPORT
            | LVS_AUTOARRANGE
            | LVS_OWNERDATA
            | LVS_SHOWSELALWAYS
            | LVS_SHAREIMAGELISTS,
    );
    unsafe {
        SendMessageW(
            list,
            LVM_SETEXTENDEDLISTVIEWSTYLE,
            0,
            (LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_HEADERDRAGDROP) as isize,
        );
        for (index, (caption, width)) in columns.iter().enumerate() {
            let mut caption = wide(crate::locale::text(caption));
            let mut column: LVCOLUMNW = std::mem::zeroed();
            column.mask = LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM;
            column.pszText = caption.as_mut_ptr();
            column.cx = *width;
            column.iSubItem = index as i32;
            SendMessageW(list, LVM_INSERTCOLUMNW, index, &column as *const _ as isize);
        }
        SetWindowSubclass(list, Some(list_proc), 1, parent as usize);
        scrollbars::attach(list);
        let mut info: SHFILEINFOW = std::mem::zeroed();
        let small = SHGetFileInfoW(
            wide("folder").as_ptr(),
            0x10,
            &mut info,
            std::mem::size_of_val(&info) as u32,
            SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES,
        );
        SendMessageW(list, LVM_SETIMAGELIST, LVSIL_SMALL as usize, small as isize);
        let large = SHGetFileInfoW(
            wide("folder").as_ptr(),
            0x10,
            &mut info,
            std::mem::size_of_val(&info) as u32,
            SHGFI_SYSICONINDEX | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES,
        );
        SendMessageW(
            list,
            LVM_SETIMAGELIST,
            LVSIL_NORMAL as usize,
            large as isize,
        );
    }
    chrome::setup_list(
        list,
        parent,
        unsafe { GetWindowLongPtrW(parent, GWLP_USERDATA) } == 1,
    );
    theme_list(list);
    list
}
fn set_column_width(list: HWND, column: usize, width: i32, visible: bool) {
    unsafe {
        let header = SendMessageW(list, LVM_GETHEADER, 0, 0) as HWND;
        let mut item = HDITEMW {
            mask: HDI_FORMAT,
            ..std::mem::zeroed()
        };
        SendMessageW(header, HDM_GETITEMW, column, &mut item as *mut _ as isize);
        // Fixed-width headers also reject programmatic width updates. Unlock
        // first, apply the saved width/visibility, then lock hidden dividers.
        item.fmt &= !HDF_FIXEDWIDTH;
        SendMessageW(header, HDM_SETITEMW, column, &item as *const _ as isize);
        SendMessageW(
            list,
            LVM_SETCOLUMNWIDTH,
            column,
            if visible { width as isize } else { 0 },
        );
        if !visible {
            item.fmt |= HDF_FIXEDWIDTH;
            SendMessageW(header, HDM_SETITEMW, column, &item as *const _ as isize);
        }
    }
}
fn theme_list(list: HWND) {
    with_palette(unsafe { GetParent(list) }, |palette| unsafe {
        SendMessageW(list, LVM_SETBKCOLOR, 0, palette.background as isize);
        SendMessageW(list, LVM_SETTEXTBKCOLOR, 0, palette.background as isize);
        SendMessageW(list, LVM_SETTEXTCOLOR, 0, palette.text as isize);
    });
}
fn selected(hwnd: HWND) -> Vec<usize> {
    let list = control(hwnd, LIST);
    let mut selected = Vec::new();
    let mut current = -1isize;
    loop {
        current = unsafe {
            SendMessageW(
                list,
                LVM_GETNEXTITEM,
                current as usize,
                LVNI_SELECTED as isize,
            )
        };
        if current < 0 {
            break;
        }
        selected.push(current as usize);
    }
    selected
}
fn copy_text(target: *mut u16, capacity: i32, value: &[u16]) {
    if target.is_null() || capacity <= 0 {
        return;
    }
    let length = value.len().min(capacity as usize - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(value.as_ptr(), target, length);
        *target.add(length) = 0;
    }
}
fn size_text(bytes: u64) -> String {
    let units = ["B", "KB", "MB", "GB", "TB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < units.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{bytes} B")
    } else {
        format!("{value:.1} {}", units[unit])
    }
}
fn date_text(ticks: u64) -> String {
    use windows_sys::Win32::{
        Globalization::*, Storage::FileSystem::FileTimeToLocalFileTime, System::Time::*,
    };
    let time = FILETIME {
        dwLowDateTime: ticks as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    let mut local = FILETIME::default();
    let mut system = SYSTEMTIME::default();
    let mut result = [0u16; 80];
    unsafe {
        if FileTimeToLocalFileTime(&time, &mut local) == 0
            || FileTimeToSystemTime(&local, &mut system) == 0
        {
            return String::new();
        }
        let count = GetDateFormatEx(
            null(),
            DATE_SHORTDATE,
            &system,
            null(),
            result.as_mut_ptr(),
            result.len() as i32,
            null(),
        );
        if count > 0 {
            String::from_utf16_lossy(&result[..count as usize - 1])
        } else {
            String::new()
        }
    }
}
fn display_info(hwnd: HWND, notification: *mut NMLVDISPINFOW) {
    let notification = unsafe { &mut *notification };
    let item = &mut notification.item;
    if item.iItem < 0 {
        return;
    }
    VIEWS.with(|views| {
        let views = views.borrow();
        let Some(view) = views.get(&(hwnd as usize)) else {
            return;
        };
        if view.is_main {
            if let Some(row) = view.main.get(item.iItem as usize) {
                if item.mask & LVIF_TEXT != 0 {
                    if let Some(text) = row.get(item.iSubItem as usize) {
                        copy_text(item.pszText, item.cchTextMax, text);
                    }
                }
            }
            return;
        }
        let Some(row) = view.rows.get(item.iItem as usize) else {
            return;
        };
        if row.parent_navigation && item.iSubItem > 0 {
            if item.mask & LVIF_TEXT != 0 {
                copy_text(item.pszText, item.cchTextMax, &[]);
            }
            return;
        }
        if item.mask & LVIF_IMAGE != 0 {
            // Native layout requests images even for offscreen items. Only
            // painting and bounded prefetch should schedule shell extraction.
            item.iImage = images::cached(hwnd, row);
        }

        if item.mask & LVIF_TEXT != 0 {
            match item.iSubItem {
                0 => copy_text(item.pszText, item.cchTextMax, &row.name),
                1 => copy_text(
                    item.pszText,
                    item.cchTextMax,
                    &crate::locale::text(&String::from_utf16_lossy(&row.kind))
                        .encode_utf16()
                        .collect::<Vec<_>>(),
                ),
                2 => copy_text(
                    item.pszText,
                    item.cchTextMax,
                    &if row.directory {
                        Vec::new()
                    } else {
                        size_text(row.size).encode_utf16().collect()
                    },
                ),
                3 => copy_text(
                    item.pszText,
                    item.cchTextMax,
                    &date_text(row.modified).encode_utf16().collect::<Vec<_>>(),
                ),
                4 => copy_text(
                    item.pszText,
                    item.cchTextMax,
                    &date_text(row.created).encode_utf16().collect::<Vec<_>>(),
                ),
                column if (5..10).contains(&column) && view.columns[column as usize] => {
                    let value = METADATA
                        .with(|cache| cache.borrow_mut().text(hwnd as usize, row, column as usize));
                    copy_text(item.pszText, item.cchTextMax, &value);
                }
                _ => {}
            }
        }
    });
}

unsafe extern "system" fn list_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    _id: usize,
    parent: usize,
) -> LRESULT {
    if let Some(result) = marquee::message(hwnd, parent as _, message, wparam, lparam) {
        return result;
    }
    if matches!(message, WM_PAINT | WM_PRINTCLIENT | WM_ERASEBKGND) && content::updating(hwnd) {
        if message == WM_PAINT {
            unsafe {
                let mut paint = std::mem::zeroed();
                BeginPaint(hwnd, &mut paint);
                EndPaint(hwnd, &paint);
            }
        }
        return 1;
    }
    if matches!(message, WM_PAINT | WM_PRINTCLIENT | WM_ERASEBKGND)
        && chrome::panel(parent as _)
        && unsafe { SendMessageW(hwnd, LVM_GETVIEW, 0, 0) } == LV_VIEW_ICON as isize
    {
        if message != WM_ERASEBKGND {
            chrome::paint_icon_list(
                parent as _,
                (message == WM_PRINTCLIENT).then_some(wparam as HDC),
            );
        }
        return 1;
    }
    if message == WM_MOUSEWHEEL && wparam & MK_CONTROL as usize != 0 && chrome::panel(parent as _) {
        emit(Event::Zoom(parent, ((wparam >> 16) as u16) as i16 as i32));
        return 0;
    }
    let scrolled = matches!(
        message,
        LVM_SCROLL | LVM_ENSUREVISIBLE | WM_VSCROLL | WM_HSCROLL | WM_MOUSEWHEEL | WM_KEYDOWN
    );
    if scrolled || matches!(message, LVM_SETVIEW | LVM_SETITEMCOUNT) {
        motion::reset_colors(hwnd);
    }
    if message == WM_MOUSEMOVE {
        let mut hit = LVHITTESTINFO {
            pt: POINT {
                x: (lparam & 0xffff) as i16 as i32,
                y: ((lparam >> 16) & 0xffff) as i16 as i32,
            },
            ..unsafe { std::mem::zeroed() }
        };
        unsafe {
            let index = SendMessageW(
                hwnd,
                LVM_HITTEST,
                0,
                &mut hit as *mut LVHITTESTINFO as isize,
            ) + 1;
            let key = wide("DPRowHot");
            let previous = GetPropW(hwnd, key.as_ptr()) as isize;
            if previous != index {
                SetPropW(hwnd, key.as_ptr(), index as usize as _);
                InvalidateRect(hwnd, null(), 0);
            }
            let mut track = TRACKMOUSEEVENT {
                cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                dwFlags: TME_LEAVE,
                hwndTrack: hwnd,
                dwHoverTime: 0,
            };
            TrackMouseEvent(&mut track);
        }
    }
    if message == WM_MOUSELEAVE || message == WM_NCDESTROY {
        unsafe {
            let previous = RemovePropW(hwnd, wide("DPRowHot").as_ptr()) as isize;
            if message == WM_MOUSELEAVE && previous > 0 {
                InvalidateRect(hwnd, null(), 0);
            }
        }
    }
    if matches!(message, WM_LBUTTONUP | WM_KEYUP) {
        unsafe {
            InvalidateRect(hwnd, null(), 0);
        }
    }
    if message == WM_NOTIFY {
        let header = unsafe { &*(lparam as *const NMHDR) };
        if matches!(
            header.code,
            HDN_BEGINTRACKA | HDN_BEGINTRACKW | HDN_ITEMCHANGINGA | HDN_ITEMCHANGINGW
        ) {
            let item = unsafe { &*(lparam as *const NMHEADERW) };
            let hidden = VIEWS.with(|views| {
                views.borrow().get(&parent).is_some_and(|view| {
                    !view.is_main && view.columns.get(item.iItem as usize) == Some(&false)
                })
            });
            if hidden
                && (matches!(header.code, HDN_BEGINTRACKA | HDN_BEGINTRACKW)
                    || unsafe {
                        item.pitem
                            .as_ref()
                            .is_some_and(|item| item.mask & HDI_WIDTH != 0 && item.cxy != 0)
                    })
            {
                // A zero-width column is still a native header item. Veto
                // dragging/autosizing it back into view while it is disabled.
                return 1;
            }
        }
        if matches!(header.code, HDN_ENDTRACKW | HDN_ENDDRAG) {
            emit(Event::Columns(parent));
        }
    }
    if message == WM_KEYDOWN {
        let ctrl = unsafe { GetKeyState(VK_CONTROL as i32) } < 0;
        let command = match wparam as u16 {
            VK_RETURN => Some(OPEN),
            VK_DELETE => Some(DELETE),
            VK_F2 => Some(RENAME),
            VK_F5 => Some(REFRESH),
            VK_BACK => Some(UP),
            0x43 if ctrl => Some(COPY),
            0x58 if ctrl => Some(CUT),
            0x56 if ctrl => Some(PASTE),
            0x41 if ctrl => {
                let mut item: LVITEMW = unsafe { std::mem::zeroed() };
                item.state = LVIS_SELECTED;
                item.stateMask = LVIS_SELECTED;
                unsafe {
                    SendMessageW(
                        hwnd,
                        LVM_SETITEMSTATE,
                        usize::MAX,
                        &item as *const _ as isize,
                    );
                }
                return 0;
            }
            0x46 if ctrl => {
                emit(Event::Command(parent, SEARCH_TOGGLE));
                return 0;
            }
            _ => None,
        };
        if let Some(command) = command {
            emit(Event::Command(parent, command));
            return 0;
        }
    }
    // The native smooth-scroll path moves pixels and paints intermediate item
    // positions synchronously. Our grid owns its complete buffered frame: move
    // the native viewport atomically, then repaint it once at the final offset.
    let scroll_update = (scrolled
        && chrome::panel(parent as _)
        && unsafe { SendMessageW(hwnd, LVM_GETVIEW, 0, 0) } == LV_VIEW_ICON as isize)
        .then(|| content::Update::new(parent as _));
    let result = unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
    drop(scroll_update);
    if scrolled {
        marquee::refresh(hwnd, parent as _);
    }
    if matches!(message, WM_SIZE | LVM_SETITEMCOUNT | LVM_SETVIEW) {
        icon_grid::relayout(parent as _);
    }
    result
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message != 0 && TASKBAR_CREATED.with(|registered| registered.get() == message) {
        if unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } == 1 {
            emit(Event::TaskbarCreated);
        }
        return 0;
    }
    if let Some(result) = shell::menu_message(message, wparam, lparam) {
        return result;
    }
    if let Some(result) = menus::message(hwnd, message, lparam) {
        return result;
    }
    if let Some(result) = titlebar::message(hwnd, message, wparam, lparam) {
        return result;
    }
    content::dismiss_selection(hwnd, message, wparam, lparam);
    if chrome::panel(hwnd)
        && matches!(message, WM_MOUSEMOVE | WM_MOUSELEAVE | WM_LBUTTONDOWN)
        && chrome::search_header_mouse(hwnd, message, lparam)
    {
        return 0;
    }
    match message {
        WM_NCCREATE => {
            let create = unsafe { &*(lparam as *const CREATESTRUCTW) };
            if matches!(create.lpCreateParams as usize, 1 | 2) {
                unsafe {
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
                }
            }
        }
        WM_NCCALCSIZE if chrome::panel(hwnd) => return 0,
        // The panel paints its complete frame in the client area. Let Windows
        // update activation state without drawing a standard resize border over
        // it, especially when a popup closes or Explorer takes focus.
        WM_NCACTIVATE if chrome::panel(hwnd) && unsafe { IsIconic(hwnd) } == 0 => {
            return unsafe { DefWindowProcW(hwnd, message, wparam, -1) };
        }
        WM_NCHITTEST if chrome::panel(hwnd) => return chrome::hit_test(hwnd, lparam),
        WM_NCLBUTTONDBLCLK if chrome::panel(hwnd) && wparam == HTCAPTION as usize => {
            emit(Event::HeaderDoubleClick(hwnd as usize));
            return 0;
        }
        WM_LBUTTONDBLCLK
            if chrome::panel(hwnd)
                && ((lparam >> 16) as i16 as i32)
                    < chrome::scale(hwnd, chrome::header_height(hwnd)) =>
        {
            emit(Event::HeaderDoubleClick(hwnd as usize));
            return 0;
        }
        WM_PAINT if unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } != 0 => {
            chrome::frame(hwnd, None);
            return 0;
        }
        WM_PRINTCLIENT if unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } != 0 => {
            chrome::frame(hwnd, Some(wparam as HDC));
            return 0;
        }
        WM_NCDESTROY => {
            glass::release(hwnd);
            icon_grid::forget(hwnd);
            chrome::release(hwnd);
        }
        WM_COMMAND => {
            let id = (wparam & 0xffff) as u16;
            let code = (wparam >> 16) as u16;
            if id == SEARCH as u16 && code == EN_CHANGE as u16 {
                emit(Event::Search(hwnd as usize));
            } else if code == BN_CLICKED as u16 || lparam == 0 {
                emit(Event::Command(hwnd as usize, id));
            }
            return 0;
        }
        WM_NOTIFY => {
            let header = unsafe { &*(lparam as *const NMHDR) };
            if header.code == TTN_GETDISPINFOW {
                let info = unsafe { &mut *(lparam as *mut NMTTDISPINFOW) };
                let caption = text(info.hdr.idFrom as HWND);
                copy_text(
                    info.szText.as_mut_ptr(),
                    info.szText.len() as i32,
                    &caption.encode_utf16().collect::<Vec<_>>(),
                );
                return 0;
            }
            if header.code == NM_CUSTOMDRAW && header.idFrom == LIST as usize {
                return chrome::custom_list(hwnd, unsafe { &mut *(lparam as *mut NMLVCUSTOMDRAW) });
            }
            match header.code {
                LVN_GETDISPINFOW => {
                    display_info(hwnd, lparam as *mut _);
                    return 0;
                }
                NM_CLICK | NM_DBLCLK if header.idFrom == LIST as usize => {
                    let single = VIEWS.with(|views| {
                        views
                            .borrow()
                            .get(&(hwnd as usize))
                            .is_some_and(|view| view.single_click)
                    });
                    if (single && header.code == NM_CLICK) || (!single && header.code == NM_DBLCLK)
                    {
                        let item = unsafe { &*(lparam as *const NMITEMACTIVATE) };
                        if item.iItem >= 0 {
                            emit(Event::Activate(hwnd as usize, item.iItem as usize));
                        }
                    }
                }
                LVN_BEGINDRAG => emit(Event::Drag(hwnd as usize)),
                LVN_COLUMNCLICK => {
                    let item = unsafe { &*(lparam as *const NMLISTVIEW) };
                    emit(Event::Column(hwnd as usize, item.iSubItem as usize));
                }
                TCN_SELCHANGE => {
                    let index = unsafe { SendMessageW(header.hwndFrom, TCM_GETCURSEL, 0, 0) };
                    if index >= 0 {
                        emit(Event::Tab(hwnd as usize, index as usize));
                    }
                }
                HDN_ENDTRACKW | HDN_ENDDRAG => emit(Event::Columns(hwnd as usize)),
                _ => {}
            }
            return 0;
        }
        WM_SIZE => {
            // Expansion already laid out the content at its final size. Only
            // expose more of that surface; do not rebuild native tab geometry,
            // scrollbars and child layouts at every intermediate height.
            if !motion::resizing(hwnd) {
                layout(hwnd);
            }
            return 0;
        }
        WM_ENTERSIZEMOVE => {
            motion::cancel_resize(hwnd);
            icon_grid::begin_resize(hwnd);
        }
        WM_EXITSIZEMOVE => {
            icon_grid::end_resize(hwnd);
            emit(Event::Geometry(hwnd as usize));
            return 0;
        }
        WM_DPICHANGED => {
            let rect = unsafe { &*(lparam as *const RECT) };
            unsafe {
                SetWindowPos(
                    hwnd,
                    null_mut(),
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    SWP_NOZORDER | SWP_NOACTIVATE,
                );
            }
            chrome::restyle(hwnd);
            chrome::resize_assets(hwnd);
            emit(Event::DisplayScale(hwnd as usize));
            layout(hwnd);
            return 0;
        }
        WM_GETMINMAXINFO => {
            let info = unsafe { &mut *(lparam as *mut MINMAXINFO) };
            let main = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } == 1;
            info.ptMinTrackSize = POINT {
                x: chrome::scale(hwnd, if main { 820 } else { 220 }),
                y: chrome::scale(
                    hwnd,
                    if main {
                        500
                    } else if motion::resizing(hwnd)
                        || VIEWS.with(|views| {
                            views
                                .borrow()
                                .get(&(hwnd as usize))
                                .is_some_and(|v| v.collapsed)
                        })
                    {
                        PANEL_HEADER_HEIGHT
                    } else {
                        220
                    },
                ),
            };
            return 0;
        }
        WM_CLOSE => {
            emit(Event::Close(hwnd as usize));
            return 0;
        }
        WM_DESTROY => {
            PANEL_PALETTES.with(|palettes| {
                palettes.borrow_mut().remove(&(hwnd as usize));
            });
            VIEWS.with(|views| {
                views.borrow_mut().remove(&(hwnd as usize));
            });
            return 0;
        }
        WM_HOTKEY => {
            emit(Event::Hotkey(wparam as i32));
            return 0;
        }
        WM_TIMER => {
            if wparam == ZOOM_TIMER {
                emit(Event::ApplyZoom(hwnd as usize));
                return 0;
            }
            if wparam == ZOOM_STATUS_TIMER {
                emit(Event::HideZoom(hwnd as usize));
                return 0;
            }
            if wparam == HOVER_TIMER && chrome::panel(hwnd) {
                emit(Event::Hover(hwnd as usize));
            } else {
                emit(Event::Timer(wparam));
            }
            return 0;
        }
        WM_MOUSEWHEEL if chrome::panel(hwnd) && wparam & MK_CONTROL as usize != 0 => {
            emit(Event::Zoom(
                hwnd as usize,
                ((wparam >> 16) as u16) as i16 as i32,
            ));
            return 0;
        }
        WM_SETCURSOR if chrome::panel(hwnd) => {
            let start = VIEWS.with(|views| {
                if let Some(view) = views.borrow_mut().get_mut(&(hwnd as usize)) {
                    if view.options.hover && view.collapsed && !view.hover_tracking {
                        view.hover_tracking = true;
                        return true;
                    }
                }
                false
            });
            if start {
                unsafe {
                    SetTimer(hwnd, HOVER_TIMER, 32, None);
                }
                emit(Event::Hover(hwnd as usize));
            }
        }
        TRAY_MESSAGE => {
            emit(Event::Tray(lparam as u32 & 0xffff));
            return 0;
        }
        WM_CONTEXTMENU => {
            let mut point = POINT {
                x: (lparam & 0xffff) as i16 as i32,
                y: ((lparam >> 16) & 0xffff) as i16 as i32,
            };
            if point.x == -1 && point.y == -1 {
                unsafe {
                    GetCursorPos(&mut point);
                }
            }
            emit(Event::Context(hwnd as usize, point));
            return 0;
        }
        WM_DROPFILES => {
            let drop = wparam as HDROP;
            let paths = shell::dropped_paths(drop);
            let mut point = POINT::default();
            let client = unsafe { DragQueryPoint(drop, &mut point) } != 0;
            let folder = if client {
                unsafe { ClientToScreen(hwnd, &mut point) };
                drag_drop::folder_at(hwnd, point)
            } else {
                None
            };
            unsafe {
                DragFinish(drop);
            }
            let copy = unsafe { GetKeyState(VK_CONTROL as i32) } < 0;
            let shift = unsafe { GetKeyState(VK_SHIFT as i32) } < 0;
            emit(Event::Drop(hwnd as usize, paths, copy, shift, folder));
            return 0;
        }
        WM_ERASEBKGND if unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } != 0 => {
            // The buffered frame paints the background and foreground together.
            return 1;
        }
        WM_ERASEBKGND => {
            let mut rect = RECT::default();
            unsafe {
                GetClientRect(hwnd, &mut rect);
            }
            with_palette(hwnd, |palette| unsafe {
                FillRect(wparam as HDC, &rect, palette.brush);
            });
            return 1;
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT | WM_CTLCOLORLISTBOX => {
            return with_palette(hwnd, |p| {
                let edit_background = if chrome::panel(hwnd) {
                    chrome::toolbar_background(hwnd, p)
                } else {
                    p.surface
                };
                unsafe {
                    SetTextColor(
                        wparam as HDC,
                        if matches!(GetDlgCtrlID(lparam as HWND), SUBTITLE | SOURCE | STATUS) {
                            p.muted
                        } else {
                            p.text
                        },
                    );
                    SetBkColor(
                        wparam as HDC,
                        if message == WM_CTLCOLOREDIT {
                            edit_background
                        } else {
                            p.background
                        },
                    );
                }
                if message == WM_CTLCOLOREDIT {
                    if edit_background == p.background {
                        p.brush as isize
                    } else {
                        p.surface_brush as isize
                    }
                } else {
                    p.brush as isize
                }
            });
        }
        WM_DRAWITEM if lparam != 0 => {
            let draw = unsafe { &*(lparam as *const DRAWITEMSTRUCT) };
            if draw.CtlType == ODT_BUTTON {
                chrome::draw_item(draw, chrome::draw_button);
                return 1;
            }
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

fn popup(owner: HWND, point: Option<POINT>, items: &[(u16, String)]) -> Option<u16> {
    menus::popup(owner, point, items)
}
