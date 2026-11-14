//! Workspace overview, using ordinary accessible Win32 controls for every action.
use super::*;

pub const NAVIGATION: &[(u16, &str)] = &[
    (OVERVIEW, "Overview"),
    (TOGGLE, "Toggle panels"),
    (LAYOUTS, "Layouts"),
    (AUTO_SORT, "Auto-sort"),
    (BACKUPS, "Backups"),
    (GENERAL, "General"),
    (THEMES, "Appearance"),
    (SHORTCUTS, "Keyboard shortcuts"),
];
pub const PREFERENCES: &[u16] = &[GENERAL, THEMES, SHORTCUTS];
pub fn create(hwnd: HWND) {
    titlebar::create(hwnd);
    child(hwnd, "STATIC", "Your workspace", TITLE, 0);
    child(hwnd, "STATIC", "Your desktop, organized.", SUBTITLE, 0);
    for (id, caption) in [(NEW_FOLDER, "+ Folder panel"), (NEW_LIST, "+ Pinned list")]
        .into_iter()
        .chain(NAVIGATION.iter().copied())
    {
        button(hwnd, caption, id);
    }
    create_list(
        hwnd,
        &[
            ("Panel", 220),
            ("Folder / source", 420),
            ("Tabs", 60),
            ("Visible", 90),
        ],
    );
    child(hwnd, "STATIC", "Ready", STATUS, 0);
    VIEWS.with(|views| {
        views.borrow_mut().insert(
            hwnd as usize,
            ViewData {
                source: None,
                rows: Arc::new(Vec::new()),
                main: Vec::new(),
                is_main: true,
                workspace_page: OVERVIEW,
                photos: false,
                details: false,
                single_click: false,
                columns: [false; 10],
                collapsed: false,
                tab_count: 0,
                kind: String::new(),
                loaded: true,
                searching: false,
                search_open: false,
                options: panel_options::Options::default(),
                hover_tracking: false,
                zoom: 1.0,
            },
        );
    });
    super::layout(hwnd);
}
pub fn form_id(command: u16) -> i32 {
    900 + command as i32
}
pub fn active(hwnd: HWND) -> u16 {
    VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map_or(OVERVIEW, |view| view.workspace_page)
    })
}
pub fn select(hwnd: HWND, command: u16) {
    VIEWS.with(|views| {
        if let Some(view) = views.borrow_mut().get_mut(&(hwnd as usize)) {
            view.workspace_page = command;
        }
    });
    for id in [
        TITLE,
        SUBTITLE,
        LIST,
        STATUS,
        NEW_FOLDER as i32,
        NEW_LIST as i32,
    ] {
        unsafe {
            ShowWindow(
                control(hwnd, id),
                if command == OVERVIEW {
                    SW_SHOWNA
                } else {
                    SW_HIDE
                },
            );
        }
    }
    for page in PREFERENCES {
        unsafe {
            ShowWindow(
                control(hwnd, form_id(*page)),
                if command == *page { SW_SHOWNA } else { SW_HIDE },
            );
        }
    }
    super::layout(hwnd);
    unsafe {
        RedrawWindow(hwnd, null(), null_mut(), RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
}
pub fn sidebar(width: i32) -> i32 {
    if width < 900 { 220 } else { 240 }
}
pub fn layout(hwnd: HWND, width: i32, height: i32) {
    titlebar::layout(hwnd, width);
    let top = titlebar::HEIGHT;
    let rail = sidebar(width);
    let left = rail + 28;
    let body = width - left - 28;
    move_control(hwnd, TITLE, left, top + 30, body, 44);
    move_control(hwnd, SUBTITLE, left + 2, top + 80, body - 4, 24);
    let step = if height < 540 { 40 } else { 44 };
    for (i, (id, _)) in NAVIGATION.iter().enumerate() {
        move_control(
            hwnd,
            *id as i32,
            14,
            top + 60 + i as i32 * step,
            rail - 28,
            40,
        );
    }
    for command in PREFERENCES {
        move_control(
            hwnd,
            form_id(*command),
            rail + 4,
            top + 12,
            width - rail - 4,
            height - top - 12,
        );
    }
    let card = (body - 14) / 2;
    move_control(hwnd, NEW_FOLDER as i32, left, top + 128, card, 84);
    move_control(
        hwnd,
        NEW_LIST as i32,
        left + card + 14,
        top + 128,
        body - card - 14,
        84,
    );
    move_control(
        hwnd,
        LIST,
        left + 1,
        top + 278,
        body - 2,
        height - top - 338,
    );
    move_control(hwnd, STATUS, left + 2, height - 40, body - 4, 24);
    for (index, w) in [(0, 180), (1, (body - 350).max(100)), (2, 54), (3, 108)] {
        unsafe {
            SendMessageW(
                control(hwnd, LIST),
                LVM_SETCOLUMNWIDTH,
                index,
                chrome::scale(hwnd, w) as isize,
            );
        }
    }
}
fn area(hwnd: HWND, x: i32, y: i32, w: i32, h: i32) -> RECT {
    RECT {
        left: chrome::scale(hwnd, x),
        top: chrome::scale(hwnd, y),
        right: chrome::scale(hwnd, x + w),
        bottom: chrome::scale(hwnd, y + h),
    }
}
pub fn paint(hwnd: HWND, dc: HDC, bounds: RECT) {
    let width = bounds.right * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let height = bounds.bottom * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let rail = sidebar(width);
    let top = titlebar::HEIGHT;
    with_palette(hwnd, |p| {
        let rail_color = blend(p.background, p.surface, 55);
        chrome::fill(dc, &area(hwnd, 0, top, rail, height - top), rail_color);
        chrome::fill(dc, &area(hwnd, rail, top, 1, height - top), p.border);
        titlebar::paint(hwnd, dc, bounds);
        chrome::label(
            dc,
            &crate::locale::text("WORKSPACE"),
            area(hwnd, 24, top + 28, rail - 40, 22),
            chrome::font(hwnd, 0),
            p.muted,
            DT_VCENTER | DT_SINGLELINE,
        );
        if active(hwnd) != OVERVIEW {
            return;
        }
        let count = VIEWS.with(|v| v.borrow().get(&(hwnd as usize)).map_or(0, |v| v.main.len()));
        chrome::label(
            dc,
            &crate::locale::text("Your panels"),
            area(hwnd, rail + 28, top + 236, 180, 30),
            chrome::font(hwnd, 1),
            p.text,
            DT_VCENTER | DT_SINGLELINE,
        );
        chrome::label(
            dc,
            &format!("{count} {}", crate::locale::text("panels")),
            area(hwnd, width - 180, top + 236, 150, 30),
            chrome::font(hwnd, 0),
            p.muted,
            DT_RIGHT | DT_VCENTER | DT_SINGLELINE,
        );
        chrome::rounded(
            dc,
            &area(
                hwnd,
                rail + 27,
                top + 277,
                width - rail - 54,
                height - top - 336,
            ),
            chrome::scale(hwnd, 16),
            p.background,
            p.border,
        );
    });
}
pub fn draw_button(draw: &DRAWITEMSTRUCT) {
    let hwnd = unsafe { GetParent(draw.hwndItem) };
    let id = draw.CtlID as u16;
    let hot = chrome::is_hot(draw.hwndItem);
    let pressed = draw.itemState & ODS_SELECTED != 0;
    let focused = chrome::keyboard_focus(draw.hwndItem);
    let card = matches!(id, NEW_FOLDER | NEW_LIST);
    let active = active(hwnd) == id;
    let icon = match id {
        OVERVIEW => "\u{e80f}",
        GENERAL => "\u{e946}",
        THEMES => "\u{e790}",
        SHORTCUTS => "\u{e765}",
        NEW_FOLDER => "\u{e8b7}",
        NEW_LIST => "\u{e718}",
        TOGGLE => "\u{e890}",
        LAYOUTS => "\u{e8a9}",
        BACKUPS => "\u{e81c}",
        AUTO_SORT => "\u{e8cb}",
        _ => "\u{e713}",
    };
    with_palette(hwnd, |p| {
        let rail = blend(p.background, p.surface, 55);
        chrome::fill(
            draw.hDC,
            &draw.rcItem,
            if card { p.background } else { rail },
        );
        let target = if pressed || active {
            p.selected
        } else if hot {
            p.hover
        } else if card {
            p.surface
        } else {
            rail
        };
        let mut r = draw.rcItem;
        unsafe {
            InflateRect(&mut r, -1, -1);
        }
        let displacement = motion::scalar(draw.hwndItem, 0, if pressed { 2.0 } else { 0.0 });
        r.top += displacement.round() as i32;
        chrome::rounded(
            draw.hDC,
            &r,
            chrome::scale(hwnd, 16),
            motion::color(draw.hwndItem, 0, target),
            motion::color(
                draw.hwndItem,
                1,
                if focused || (hot && card) {
                    p.accent
                } else if card {
                    p.border
                } else {
                    target
                },
            ),
        );
        if active {
            chrome::rounded(
                draw.hDC,
                &RECT {
                    left: r.left,
                    top: r.top + chrome::scale(hwnd, 10),
                    right: r.left + chrome::scale(hwnd, 3),
                    bottom: r.bottom - chrome::scale(hwnd, 10),
                },
                chrome::scale(hwnd, 3),
                p.accent,
                p.accent,
            );
        }
        let s = |v| chrome::scale(hwnd, v);
        let icon_rect = RECT {
            left: r.left + s(14),
            top: r.top + if card { s(20) } else { 0 },
            right: r.left + s(if card { 54 } else { 40 }),
            bottom: if card { r.top + s(60) } else { r.bottom },
        };
        if card {
            chrome::rounded(draw.hDC, &icon_rect, s(12), p.selected, p.selected);
        }
        chrome::label(
            draw.hDC,
            icon,
            icon_rect,
            chrome::font(hwnd, 3),
            if hot || card || active {
                p.accent
            } else {
                p.muted
            },
            DT_CENTER | DT_VCENTER | DT_SINGLELINE,
        );
        let caption = match id {
            NEW_FOLDER => crate::locale::text("Folder panel"),
            NEW_LIST => crate::locale::text("Pinned list"),
            TOGGLE => crate::locale::text("Toggle panels"),
            _ => text(draw.hwndItem),
        };
        let label = RECT {
            left: r.left + s(if card { 68 } else { 50 }),
            top: if card { r.top + s(17) } else { r.top },
            right: r.right - s(12),
            bottom: if card { r.top + s(42) } else { r.bottom },
        };
        chrome::label(
            draw.hDC,
            &caption,
            label,
            chrome::font(hwnd, usize::from(card || active)),
            if active { p.accent } else { p.text },
            DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
        );
        if card {
            chrome::label(
                draw.hDC,
                &crate::locale::text(if id == NEW_FOLDER {
                    "Bring a folder to your desktop"
                } else {
                    "Keep your favorites together"
                }),
                RECT {
                    top: r.top + s(43),
                    bottom: r.top + s(66),
                    ..label
                },
                chrome::font(hwnd, 0),
                p.muted,
                DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
        }
    });
}
