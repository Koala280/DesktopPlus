//! Event-driven GDI chrome around the native, virtualized Windows controls.
use super::*;

mod back_buffer;
mod details;
mod shapes;

pub(super) use details::{paint_details, set_sort};

#[cfg(test)]
mod header_preview;
#[cfg(test)]
mod paint_tests;

const FONTS: &[u16] = &[68, 80, 70, 111, 110, 116, 115, 0];
const HOVER: &[u16] = &[68, 80, 72, 111, 118, 101, 114, 0];
const SEARCH_HOVER_BLOCKED: &[u16] = &[68, 80, 83, 101, 97, 114, 99, 104, 66, 108, 111, 99, 107, 0];
const ROW_IMAGES: &[u16] = &[68, 80, 82, 111, 119, 115, 0];
struct Fonts([HFONT; 7]);
impl Drop for Fonts {
    fn drop(&mut self) {
        for font in self.0 {
            unsafe {
                DeleteObject(font);
            }
        }
    }
}
pub(super) fn scale(hwnd: HWND, value: i32) -> i32 {
    (value as i64 * unsafe { GetDpiForWindow(hwnd) }.max(96) as i64 / 96) as i32
}
pub(super) fn zoom(hwnd: HWND) -> f64 {
    VIEWS.with(|v| v.borrow().get(&(hwnd as usize)).map_or(1.0, |v| v.zoom))
}
pub(super) fn content_scale(hwnd: HWND, value: i32) -> i32 {
    ((value as f64) * zoom(hwnd) * (unsafe { GetDpiForWindow(hwnd) }.max(96) as f64) / 96.0)
        .round()
        .max(1.0) as i32
}
pub(super) fn create_font(family: &str, size: f64, weight: i32, dpi: u32) -> HFONT {
    // Rasterize at the actual monitor size, using the same family and ClearType
    // smoothing for native inputs and DirectWrite labels.
    let pixels = (size * dpi.max(96) as f64 / 96.0).round().max(1.0) as i32;
    let family = text_render::ui_family(family, size);
    unsafe {
        CreateFontW(
            -pixels,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            DEFAULT_CHARSET as u32,
            0,
            0,
            CLEARTYPE_NATURAL_QUALITY,
            0,
            wide(family).as_ptr(),
        )
    }
}
pub(super) fn font(hwnd: HWND, kind: usize) -> HFONT {
    let mut pointer = unsafe { GetPropW(hwnd, FONTS.as_ptr()) } as *mut Fonts;
    if pointer.is_null() {
        let (family, size, title_size) =
            with_palette(hwnd, |p| (p.family.clone(), p.size, p.title_size));
        let ui_size = size.max(15.0);
        let fonts = [
            (ui_size, 400, family.as_str()),
            (ui_size, 500, family.as_str()),
            (28.0, 600, family.as_str()),
            (18.0, 400, "Segoe MDL2 Assets"),
            (title_size, 400, family.as_str()),
            (size * zoom(hwnd), 400, family.as_str()),
            (12.0, 400, "Segoe MDL2 Assets"),
        ]
        .map(|(size, weight, face)| {
            create_font(face, size, weight, unsafe { GetDpiForWindow(hwnd) })
        });
        pointer = Box::into_raw(Box::new(Fonts(fonts)));
        unsafe {
            SetPropW(hwnd, FONTS.as_ptr(), pointer.cast());
        }
    }
    unsafe { (*pointer).0[kind.min(6)] }
}
pub(super) fn release(hwnd: HWND) {
    let pointer = unsafe { RemovePropW(hwnd, FONTS.as_ptr()) } as *mut Fonts;
    if !pointer.is_null() {
        unsafe {
            drop(Box::from_raw(pointer));
        }
    }
    let images = unsafe { RemovePropW(hwnd, ROW_IMAGES.as_ptr()) };
    if !images.is_null() {
        unsafe {
            ImageList_Destroy(images as isize);
        }
    }
}
pub(super) fn restyle(hwnd: HWND) {
    let previous = unsafe { RemovePropW(hwnd, FONTS.as_ptr()) } as *mut Fonts;
    unsafe {
        let mut child = GetWindow(hwnd, GW_CHILD);
        while !child.is_null() {
            SendMessageW(
                child,
                WM_SETFONT,
                font(hwnd, if GetDlgCtrlID(child) == TITLE { 2 } else { 0 }) as usize,
                1,
            );
            child = GetWindow(child, GW_HWNDNEXT);
        }
    }
    if !previous.is_null() {
        unsafe {
            drop(Box::from_raw(previous));
        }
    }
    unsafe {
        InvalidateRect(hwnd, null(), 1);
    }
}
pub(super) fn fill(dc: HDC, rect: &RECT, color: u32) {
    unsafe {
        let brush = CreateSolidBrush(color);
        FillRect(dc, rect, brush);
        DeleteObject(brush);
    }
}
pub(super) fn rounded(dc: HDC, rect: &RECT, radius: i32, background: u32, border: u32) {
    if rect.right <= rect.left || rect.bottom <= rect.top {
        return;
    }
    if shapes::rounded(dc, rect, radius, background, border) {
        return;
    }
    unsafe {
        let brush = CreateSolidBrush(background);
        let pen = CreatePen(PS_SOLID, 1, border);
        let old_brush = SelectObject(dc, brush);
        let old_pen = SelectObject(dc, pen);
        RoundRect(
            dc,
            rect.left,
            rect.top,
            rect.right,
            rect.bottom,
            radius,
            radius,
        );
        SelectObject(dc, old_brush);
        SelectObject(dc, old_pen);
        DeleteObject(brush);
        DeleteObject(pen);
    }
}
pub(super) fn rounded_offset(
    dc: HDC,
    rect: &RECT,
    diameter: i32,
    background: u32,
    border: u32,
    offset: (f32, f32),
) {
    if !shapes::rounded_offset(dc, rect, diameter, background, border, offset) {
        let mut rect = *rect;
        unsafe {
            OffsetRect(&mut rect, offset.0.round() as i32, offset.1.round() as i32);
        }
        rounded(dc, &rect, diameter, background, border);
    }
}
pub(super) fn label(dc: HDC, value: &str, mut rect: RECT, font: HFONT, color: u32, flags: u32) {
    if text_render::draw(dc, value, rect, font, color, flags) {
        return;
    }
    unsafe {
        let old = SelectObject(dc, font);
        SetBkMode(dc, TRANSPARENT as i32);
        SetTextColor(dc, color);
        DrawTextW(dc, wide(value).as_ptr(), -1, &mut rect, flags | DT_NOPREFIX);
        SelectObject(dc, old);
    }
}
fn rect(hwnd: HWND, x: i32, y: i32, w: i32, h: i32) -> RECT {
    RECT {
        left: scale(hwnd, x),
        top: scale(hwnd, y),
        right: scale(hwnd, x + w),
        bottom: scale(hwnd, y + h),
    }
}
/// Print DCs have no associated HWND. Use the actual control for its material,
/// including nonclient scrollbar tracks and the corner where they intersect.
pub(super) fn buffered_for(hwnd: HWND, dc: HDC, bounds: RECT, draw: impl FnOnce(HDC)) {
    back_buffer::paint(dc, bounds, glass::for_window(hwnd), draw);
}
pub(super) fn draw_item(item: &DRAWITEMSTRUCT, draw: impl FnOnce(&DRAWITEMSTRUCT)) {
    back_buffer::paint(
        item.hDC,
        item.rcItem,
        glass::for_window(item.hwndItem),
        |dc| {
            let buffered_item = DRAWITEMSTRUCT { hDC: dc, ..*item };
            draw(&buffered_item);
        },
    );
}
pub(super) fn paint(hwnd: HWND, print: Option<HDC>, draw: impl FnOnce(HDC, RECT)) {
    unsafe {
        let mut ps = PAINTSTRUCT::default();
        let dc = print.unwrap_or_else(|| BeginPaint(hwnd, &mut ps));
        let mut bounds = RECT::default();
        GetClientRect(hwnd, &mut bounds);
        back_buffer::paint(dc, bounds, glass::for_window(hwnd), |buffer| {
            draw(buffer, bounds)
        });
        if print.is_none() {
            EndPaint(hwnd, &ps);
        }
    }
}
pub(super) fn panel(hwnd: HWND) -> bool {
    unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) == 2 }
}
pub(super) struct HeaderLayout {
    pub style: HeaderStyle,
    pub height: i32,
    pub identity_top: i32,
    pub identity_height: i32,
    pub button_top: i32,
    pub context_left: i32,
    pub context_width: i32,
    pub identity_left: i32,
    pub identity_width: i32,
    pub search_left: i32,
    pub search_width: i32,
    pub search_progress: f64,
    pub search_icon_space: i32,
    pub settings: Option<i32>,
    pub movement: Option<i32>,
    pub up: Option<i32>,
    pub search_button: Option<i32>,
    pub empty: Option<i32>,
    pub collapse: Option<i32>,
    pub close: Option<i32>,
}
fn identity_bounds(width: i32, left: i32, right: i32, count: usize, alignment: &str) -> (i32, i32) {
    let available = (right - left).max(0);
    let desired = if count > 1 {
        (count.min(1000) as i32 * 128 + 6).min(available)
    } else {
        available
    };
    match alignment {
        "center" => {
            // Center against the panel itself, not the asymmetric toolbar gap.
            // Keep tabs compact instead of stretching each tab across half of it.
            let symmetric = (width - 2 * left.max(width - right)).max(0);
            // Tabs keep their readable width and shift together as search
            // claims space. A single centered title shrinks continuously.
            let content = if count > 1 {
                desired
            } else {
                desired.min(symmetric.max(76.min(available)))
            };
            (
                ((width - content) / 2).clamp(left, right.max(left) - content),
                content,
            )
        }
        "right" => (right - desired, desired),
        _ => (left, desired),
    }
}
pub(super) fn header_style(hwnd: HWND) -> HeaderStyle {
    VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map_or(HeaderStyle::default(), |view| view.options.header_style)
    })
}
pub(super) fn header_height(hwnd: HWND) -> i32 {
    VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map_or(PANEL_HEADER_HEIGHT, |view| {
                view.options.header_style.height(view.collapsed)
            })
    })
}
fn tab_background(style: HeaderStyle, p: &Palette) -> u32 {
    if style == HeaderStyle::Line {
        p.background
    } else {
        p.surface
    }
}
pub(super) fn toolbar_background(hwnd: HWND, p: &Palette) -> u32 {
    match header_style(hwnd) {
        HeaderStyle::Line => p.background,
        HeaderStyle::TwoRows if header_height(hwnd) > PANEL_HEADER_HEIGHT => p.background,
        _ => p.surface,
    }
}
pub(super) fn header_layout(hwnd: HWND, width: i32) -> HeaderLayout {
    let (collapsed, opened, folder, clearable, count, options) = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map(|v| {
                (
                    v.collapsed,
                    v.search_open,
                    v.kind == "Folder",
                    v.options.empty_button
                        && matches!(v.kind.as_str(), "Folder" | "List" | "RecycleBin"),
                    v.tab_count,
                    v.options.clone(),
                )
            })
            .unwrap_or_default()
    });
    let style = options.header_style;
    let height = style.height(collapsed);
    let two_rows = style == HeaderStyle::TwoRows && !collapsed;
    let identity_top = if style == HeaderStyle::Floating {
        if collapsed { 4 } else { 8 }
    } else {
        0
    };
    let identity_height = if style == HeaderStyle::Floating && collapsed {
        40
    } else {
        PANEL_HEADER_HEIGHT
    };
    let button_top = if two_rows {
        54
    } else {
        identity_top + (identity_height - 28) / 2
    };
    let inset = if style == HeaderStyle::Floating {
        18
    } else {
        10
    };
    let mut left = inset;
    let movement = (options.movement == "button").then(|| {
        let x = left;
        left += 30;
        x
    });
    let up = (folder && options.parent == "header" && !collapsed && width >= 300).then(|| {
        let x = left;
        left += 28;
        x
    });
    left += 4;
    let mut right = width - inset;
    let close = options.close.then(|| {
        right -= 26;
        let x = right;
        right -= 6;
        x
    });
    let collapse = (options.collapse != "doubleclick").then(|| {
        right -= 26;
        let x = right;
        right -= 6;
        x
    });
    let empty = (clearable && !collapsed).then(|| {
        right -= 26;
        let x = right;
        right -= 6;
        x
    });
    let settings = (width >= 560
        && (options.settings == "always" || (options.settings == "expandedonly" && !collapsed)))
        .then(|| {
            right -= 26;
            let x = right;
            right -= 6;
            x
        });
    let searchable =
        (!collapsed || !options.search_expanded) && (options.search != "hidden" || opened);
    let search = searchable && (opened || (options.search == "always" && right - left >= 292));
    let search_progress =
        motion::layout_scalar(hwnd, 0, if search { 1.0 } else { 0.0 }).clamp(0.0, 1.0);
    let search_button = (searchable && !search && search_progress == 0.0).then_some(right - 26);
    let search_width = if search || search_progress > 0.0 {
        let expanded = (right - left - 6).clamp(26, 200);
        26 + ((expanded - 26) as f64 * search_progress).round() as i32
    } else {
        0
    };
    let search_left = right - search_width;
    if search_width > 0 {
        right = search_left - 6;
    } else if searchable {
        right -= 32;
    }
    let available = (right - left).max(0);
    let (identity_left, content) = if two_rows {
        identity_bounds(width, 12, width - 12, count, &options.alignment)
    } else {
        identity_bounds(width, left, left + available, count, &options.alignment)
    };
    HeaderLayout {
        style,
        height,
        identity_top,
        identity_height,
        button_top,
        context_left: left,
        context_width: available,
        identity_left,
        identity_width: content,
        search_left,
        search_width,
        search_progress,
        // Keep the native edit clear of the glyph until it has faded out.
        search_icon_space: (96.0 * (1.0 - search_progress)).clamp(0.0, 32.0).round() as i32,
        settings,
        movement,
        up,
        search_button,
        empty,
        collapse,
        close,
    }
}
pub(super) fn frame(hwnd: HWND, print: Option<HDC>) {
    paint(hwnd, print, |dc, bounds| {
        with_palette(hwnd, |p| {
            fill(dc, &bounds, p.background);
            let w = bounds.right * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
            if panel(hwnd) {
                let (multiple, collapsed, alignment) = VIEWS.with(|views| {
                    views
                        .borrow()
                        .get(&(hwnd as usize))
                        .map(|v| (v.tab_count > 1, v.collapsed, v.options.alignment.clone()))
                        .unwrap_or_default()
                });
                let header = header_layout(hwnd, w);
                let collapsed = motion::collapse_target(hwnd).unwrap_or(collapsed);
                if header.style == HeaderStyle::Floating {
                    rounded(
                        dc,
                        &rect(
                            hwnd,
                            8,
                            header.identity_top - 3,
                            w - 16,
                            header.identity_height + 6,
                        ),
                        scale(hwnd, 16),
                        p.surface,
                        p.border,
                    );
                } else {
                    fill(
                        dc,
                        &rect(hwnd, 0, 0, w, PANEL_HEADER_HEIGHT),
                        tab_background(header.style, p),
                    );
                    fill(dc, &rect(hwnd, 0, PANEL_HEADER_HEIGHT - 1, w, 1), p.border);
                }
                if header.style == HeaderStyle::TwoRows && header.height > PANEL_HEADER_HEIGHT {
                    fill(dc, &rect(hwnd, 0, header.height - 1, w, 1), p.border);
                    label(
                        dc,
                        &text(control(hwnd, SOURCE)),
                        rect(
                            hwnd,
                            header.context_left + 4,
                            PANEL_HEADER_HEIGHT,
                            (header.context_width - 8).max(0),
                            header.height - PANEL_HEADER_HEIGHT,
                        ),
                        font(hwnd, 0),
                        p.muted,
                        DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                    );
                }
                if !multiple && header.identity_width > 8 {
                    label(
                        dc,
                        &text(hwnd),
                        rect(
                            hwnd,
                            header.identity_left + 4,
                            header.identity_top,
                            header.identity_width - 8,
                            header.identity_height,
                        ),
                        font(hwnd, 4),
                        motion::color(hwnd, 10, if collapsed { p.muted } else { p.text }),
                        DT_VCENTER
                            | DT_SINGLELINE
                            | DT_END_ELLIPSIS
                            | match alignment.as_str() {
                                "center" => DT_CENTER,
                                "right" => DT_RIGHT,
                                _ => DT_LEFT,
                            },
                    );
                }
                if header.search_width > 0 {
                    let focused = unsafe { GetFocus() == control(hwnd, SEARCH) };
                    rounded(
                        dc,
                        &rect(
                            hwnd,
                            header.search_left,
                            header.button_top - 1,
                            header.search_width,
                            30,
                        ),
                        scale(hwnd, 12),
                        toolbar_background(hwnd, p),
                        blend(
                            toolbar_background(hwnd, p),
                            if focused { p.accent } else { p.border },
                            (header.search_progress * 100.0).round() as u32,
                        ),
                    );
                    if header.search_progress < 2.0 / 3.0 {
                        label(
                            dc,
                            "\u{e721}",
                            rect(
                                hwnd,
                                header.search_left + header.search_width
                                    - 26
                                    - (8.0 * header.search_progress) as i32,
                                header.button_top,
                                26,
                                28,
                            ),
                            font(hwnd, 3),
                            blend(
                                p.muted,
                                toolbar_background(hwnd, p),
                                (header.search_progress * 150.0).round() as u32,
                            ),
                            DT_CENTER | DT_VCENTER | DT_SINGLELINE,
                        );
                    }
                }
            } else {
                dashboard::paint(hwnd, dc, bounds);
            }
        });
    });
}
pub(super) fn hit_test(hwnd: HWND, lparam: isize) -> isize {
    let movement = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map(|v| v.options.movement.clone())
            .unwrap_or_default()
    });
    if movement == "locked" {
        return HTCLIENT as isize;
    }
    let mut point = POINT {
        x: (lparam & 0xffff) as i16 as i32,
        y: ((lparam >> 16) & 0xffff) as i16 as i32,
    };
    let mut bounds = RECT::default();
    unsafe {
        ScreenToClient(hwnd, &mut point);
        GetClientRect(hwnd, &mut bounds);
    }
    let edge = scale(hwnd, 5);
    let x = point.x;
    let y = point.y;
    if search_contains(hwnd, point) {
        return HTCLIENT as isize;
    }
    let collapsed = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .is_some_and(|view| view.collapsed)
    });
    let hit = if x < edge && y < edge && !collapsed {
        HTTOPLEFT
    } else if x >= bounds.right - edge && y < edge && !collapsed {
        HTTOPRIGHT
    } else if x < edge && y >= bounds.bottom - edge && !collapsed {
        HTBOTTOMLEFT
    } else if x >= bounds.right - edge && y >= bounds.bottom - edge && !collapsed {
        HTBOTTOMRIGHT
    } else if x < edge {
        HTLEFT
    } else if x >= bounds.right - edge {
        HTRIGHT
    } else if y < edge && !collapsed {
        HTTOP
    } else if y >= bounds.bottom - edge && !collapsed {
        HTBOTTOM
    } else if y < scale(hwnd, header_height(hwnd)) && movement != "button" {
        HTCAPTION
    } else {
        HTCLIENT
    };
    hit as isize
}
pub(super) fn danger_color(palette: &Palette) -> u32 {
    let background = palette.surface;
    if (background & 255) + ((background >> 8) & 255) + ((background >> 16) & 255) < 384 {
        color("#FF9B9B", 0)
    } else {
        color("#AD3142", 0)
    }
}
fn glyph(id: u16, collapsed: bool) -> Option<&'static str> {
    match id {
        UP => Some("\u{e76b}"),
        HOME => Some("\u{e80f}"),
        VIEW => Some("\u{e8a9}"),
        PANEL_SETTINGS => Some("\u{e713}"),
        SEARCH_TOGGLE => Some("\u{e721}"),
        EMPTY_PANEL => Some("\u{e74d}"),
        COLLAPSE => Some(if collapsed { "\u{e710}" } else { "\u{e738}" }),
        HIDE_PANEL => Some("\u{e711}"),
        MOVE_PANEL => Some("\u{e700}"),
        _ => None,
    }
}
pub(super) fn draw_button(draw: &DRAWITEMSTRUCT) {
    let parent = unsafe { GetParent(draw.hwndItem) };
    if unsafe { GetWindowLongPtrW(parent, GWLP_USERDATA) } == 1 {
        dashboard::draw_button(draw);
        return;
    }
    let id = draw.CtlID as u16;
    let hot = !unsafe { GetPropW(draw.hwndItem, HOVER.as_ptr()) }.is_null();
    let focused = keyboard_focus(draw.hwndItem);
    let pressed = draw.itemState & ODS_SELECTED != 0;
    let is_panel = panel(parent);
    let collapsed = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(parent as usize))
            .is_some_and(|v| v.collapsed)
    });
    let caption = if is_panel {
        glyph(id, collapsed)
            .map(str::to_owned)
            .unwrap_or_else(|| text(draw.hwndItem))
    } else {
        text(draw.hwndItem)
    };
    with_palette(parent, |p| {
        let base = if is_panel {
            toolbar_background(parent, p)
        } else {
            p.surface
        };
        let primary = !is_panel && (id == NEW_FOLDER || id == IDOK as u16);
        let background = if primary {
            if pressed {
                blend(p.accent, p.background, 30)
            } else if hot {
                blend(p.accent, p.text, 15)
            } else {
                p.accent
            }
        } else if id == EMPTY_PANEL && (hot || pressed) {
            blend(p.surface, danger_color(p), if pressed { 20 } else { 10 })
        } else if pressed {
            p.selected
        } else if hot {
            p.hover
        } else {
            base
        };
        fill(
            draw.hDC,
            &draw.rcItem,
            if is_panel { base } else { p.background },
        );
        let mut area = draw.rcItem;
        unsafe {
            InflateRect(&mut area, -1, -1);
        }
        rounded(
            draw.hDC,
            &area,
            scale(parent, 12),
            motion::color(draw.hwndItem, 0, background),
            motion::color(
                draw.hwndItem,
                1,
                if focused { p.accent } else { background },
            ),
        );
        label(
            draw.hDC,
            &caption,
            area,
            font(
                parent,
                if is_panel && glyph(id, collapsed).is_some() {
                    6
                } else {
                    0
                },
            ),
            if primary {
                color("#10141D", 0)
            } else if hot && matches!(id, HIDE_PANEL | EMPTY_PANEL) {
                danger_color(p)
            } else if is_panel {
                motion::color(
                    draw.hwndItem,
                    2,
                    if hot || focused || pressed {
                        p.text
                    } else {
                        blend(base, p.muted, 80)
                    },
                )
            } else {
                p.text
            },
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
        );
    });
}
pub(super) fn setup_button(hwnd: HWND) {
    unsafe {
        SetWindowSubclass(hwnd, Some(button_proc), 2, 0);
    }
}
pub(super) fn is_hot(hwnd: HWND) -> bool {
    !unsafe { GetPropW(hwnd, HOVER.as_ptr()) }.is_null()
}
pub(super) fn keyboard_focus(hwnd: HWND) -> bool {
    unsafe {
        GetFocus() == hwnd
            && SendMessageW(hwnd, WM_QUERYUISTATE, 0, 0) & UISF_HIDEFOCUS as isize == 0
    }
}
pub(super) fn tooltips(parent: HWND, controls: &[u16]) {
    unsafe {
        let tooltip = CreateWindowExW(
            WS_EX_TOPMOST,
            wide("tooltips_class32").as_ptr(),
            null(),
            WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            parent,
            null_mut(),
            GetModuleHandleW(null()),
            null(),
        );
        SendMessageW(tooltip, TTM_SETMAXTIPWIDTH, 0, scale(parent, 280) as isize);
        for id in controls {
            let tool = TTTOOLINFOW {
                cbSize: std::mem::size_of::<TTTOOLINFOW>() as u32,
                uFlags: TTF_IDISHWND | TTF_SUBCLASS,
                hwnd: parent,
                uId: control(parent, *id as i32) as usize,
                lpszText: usize::MAX as _,
                ..std::mem::zeroed()
            };
            SendMessageW(tooltip, TTM_ADDTOOLW, 0, &tool as *const _ as isize);
        }
    }
}
unsafe extern "system" fn button_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if message == WM_LBUTTONDOWN && unsafe { GetDlgCtrlID(hwnd) } == MOVE_PANEL as i32 {
        unsafe {
            let mut point = POINT {
                x: (l & 0xffff) as i16 as i32,
                y: ((l >> 16) & 0xffff) as i16 as i32,
            };
            ClientToScreen(hwnd, &mut point);
            let screen = ((point.y as u16 as u32) << 16) | point.x as u16 as u32;
            ReleaseCapture();
            SendMessageW(
                GetParent(hwnd),
                WM_NCLBUTTONDOWN,
                HTCAPTION as usize,
                screen as isize,
            );
        }
        return 0;
    }
    match message {
        // WM_DRAWITEM paints the entire button into a buffer. Erasing it first
        // would expose a blank frame during hover, focus and pressed changes.
        WM_ERASEBKGND => return 1,
        WM_MOUSEMOVE => unsafe {
            if GetPropW(hwnd, HOVER.as_ptr()).is_null() {
                SetPropW(hwnd, HOVER.as_ptr(), 1usize as _);
                let mut track = TRACKMOUSEEVENT {
                    cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                    dwFlags: TME_LEAVE,
                    hwndTrack: hwnd,
                    dwHoverTime: 0,
                };
                TrackMouseEvent(&mut track);
                InvalidateRect(hwnd, null(), 0);
                if GetDlgCtrlID(hwnd) == SEARCH_TOGGLE as i32
                    && GetPropW(hwnd, SEARCH_HOVER_BLOCKED.as_ptr()).is_null()
                {
                    emit(Event::SearchVisibility(GetParent(hwnd) as usize, true));
                }
            }
        },
        WM_MOUSELEAVE => unsafe {
            RemovePropW(hwnd, HOVER.as_ptr());
            InvalidateRect(hwnd, null(), 0);
            if GetDlgCtrlID(hwnd) == SEARCH_TOGGLE as i32 {
                refresh_search_hover(GetParent(hwnd));
                search_mouse_leave(GetParent(hwnd));
            }
        },
        WM_NCDESTROY => unsafe {
            RemovePropW(hwnd, HOVER.as_ptr());
            RemovePropW(hwnd, SEARCH_HOVER_BLOCKED.as_ptr());
            RemoveWindowSubclass(hwnd, Some(button_proc), 2);
        },
        _ => {}
    }
    let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
    if message == WM_SETFOCUS {
        unsafe {
            PostMessageW(GetParent(hwnd), form::REVEAL_FOCUS, hwnd as usize, 0);
        }
    }
    if matches!(message, WM_SETFOCUS | WM_KILLFOCUS | WM_UPDATEUISTATE) {
        unsafe {
            InvalidateRect(hwnd, null(), 0);
        }
    }
    result
}
pub(super) fn setup_tabs(hwnd: HWND) {
    unsafe {
        SetWindowTheme(hwnd, wide("").as_ptr(), wide("").as_ptr());
        SendMessageW(hwnd, TCM_SETMINTABWIDTH, 0, 1);
        SetWindowSubclass(hwnd, Some(tabs_proc), 3, 0);
    }
}
fn draw_tabs(hwnd: HWND, print: Option<HDC>) {
    let parent = unsafe { GetParent(hwnd) };
    let style = header_style(parent);
    let collapsed = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(parent as usize))
            .is_some_and(|view| view.collapsed)
    });
    let collapsed = motion::collapse_target(parent).unwrap_or(collapsed);
    paint(hwnd, print, |dc, bounds| {
        with_palette(parent, |p| {
            let base = tab_background(style, p);
            fill(dc, &bounds, base);
            if style != HeaderStyle::Floating {
                fill(
                    dc,
                    &RECT {
                        top: bounds.bottom - scale(parent, 1).max(1),
                        ..bounds
                    },
                    p.border,
                );
            }
            if style == HeaderStyle::Segmented {
                rounded(
                    dc,
                    &RECT {
                        left: scale(parent, 2),
                        top: scale(parent, 6),
                        right: bounds.right - scale(parent, 2),
                        bottom: bounds.bottom - scale(parent, 6),
                    },
                    scale(parent, 16),
                    p.background,
                    p.border,
                );
            }
            let count = unsafe { SendMessageW(hwnd, TCM_GETITEMCOUNT, 0, 0) };
            let selected = unsafe { SendMessageW(hwnd, TCM_GETCURSEL, 0, 0) };
            let hovered = unsafe { GetPropW(hwnd, HOVER.as_ptr()) } as isize - 1;
            let mut items = Vec::new();
            for index in 0..count {
                let mut area = RECT::default();
                let mut caption = [0u16; 256];
                let mut item = TCITEMW {
                    mask: TCIF_TEXT,
                    pszText: caption.as_mut_ptr(),
                    cchTextMax: 256,
                    ..unsafe { std::mem::zeroed() }
                };
                unsafe {
                    SendMessageW(
                        hwnd,
                        TCM_GETITEMRECT,
                        index as usize,
                        &mut area as *mut _ as isize,
                    );
                    SendMessageW(
                        hwnd,
                        TCM_GETITEMW,
                        index as usize,
                        &mut item as *mut _ as isize,
                    );
                }
                area.top = 0;
                area.bottom = bounds.bottom;
                let hover_color = motion::color(
                    hwnd,
                    index as u32 * 4 + 2,
                    if index == hovered { p.hover } else { base },
                );
                if hover_color != base {
                    let hover = RECT {
                        left: area.left + scale(parent, 5),
                        top: scale(parent, 9),
                        right: area.right - scale(parent, 5),
                        bottom: bounds.bottom - scale(parent, 9),
                    };
                    rounded(dc, &hover, scale(parent, 10), hover_color, hover_color);
                }
                if style == HeaderStyle::Floating && index > 0 {
                    fill(
                        dc,
                        &RECT {
                            left: area.left,
                            top: scale(parent, 13),
                            right: area.left + scale(parent, 1),
                            bottom: bounds.bottom - scale(parent, 13),
                        },
                        p.border,
                    );
                }
                items.push((
                    area,
                    String::from_utf16_lossy(
                        &caption[..caption.iter().position(|c| *c == 0).unwrap_or(256)],
                    ),
                ));
            }
            if selected >= 0 && !items.is_empty() {
                // Animate selection in tab units so a resizing header and its
                // selected outline use the same geometry on every frame.
                let position = motion::scalar(hwnd, 1000, selected as f64)
                    .clamp(0.0, (items.len() - 1) as f64);
                let from = items[position.floor() as usize].0;
                let to = items[position.ceil() as usize].0;
                let fraction = position.fract();
                let left = from.left as f64 + (to.left - from.left) as f64 * fraction;
                let right = from.right as f64 + (to.right - from.right) as f64 * fraction;
                let visibility =
                    motion::scalar(hwnd, 1002, if collapsed { 0.0 } else { 1.0 }).clamp(0.0, 1.0);
                if visibility > 0.0 {
                    paint_tab_selection(
                        dc,
                        parent,
                        style,
                        p,
                        [left, right, bounds.bottom as f64],
                        visibility,
                    );
                }
            }
            for (index, (mut area, caption)) in items.into_iter().enumerate() {
                let active = index as isize == selected && !collapsed;
                area.left += scale(parent, 10);
                area.right -= scale(parent, 10);
                if area.right <= area.left {
                    continue;
                }
                label(
                    dc,
                    &caption,
                    area,
                    font(parent, 1),
                    motion::color(
                        hwnd,
                        index as u32 * 4 + 3,
                        if active && style == HeaderStyle::Floating {
                            p.accent
                        } else if active {
                            p.text
                        } else {
                            p.muted
                        },
                    ),
                    DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
            }
        });
    });
}
fn paint_tab_selection(
    dc: HDC,
    hwnd: HWND,
    style: HeaderStyle,
    p: &Palette,
    bounds: [f64; 3],
    visibility: f64,
) {
    let [left, right, bottom] = bounds;
    let base = tab_background(style, p);
    let opacity = (visibility.clamp(0.0, 1.0) * 100.0).round() as u32;
    let accent = blend(base, p.accent, opacity);
    let width = (right - left).round() as i32;
    let bottom = bottom.round() as i32;
    match style {
        HeaderStyle::Line => {
            let inset = scale(hwnd, 14).min(width / 4);
            rounded_offset(
                dc,
                &RECT {
                    left: inset,
                    top: bottom - scale(hwnd, 3),
                    right: width - inset,
                    bottom,
                },
                scale(hwnd, 3),
                accent,
                accent,
                (left as f32, 0.0),
            );
        }
        HeaderStyle::Pills | HeaderStyle::Segmented => {
            let inset = scale(hwnd, 5);
            let top = scale(
                hwnd,
                if style == HeaderStyle::Segmented {
                    9
                } else {
                    8
                },
            );
            let background = blend(
                base,
                if style == HeaderStyle::Pills {
                    p.selected
                } else {
                    p.hover
                },
                opacity,
            );
            let border = if style == HeaderStyle::Segmented {
                blend(base, p.border, opacity)
            } else {
                background
            };
            rounded_offset(
                dc,
                &RECT {
                    left: inset,
                    top,
                    right: width - inset,
                    bottom: bottom - top,
                },
                scale(hwnd, 12),
                background,
                border,
                (left as f32, 0.0),
            );
        }
        HeaderStyle::TwoRows => {
            let background = blend(base, p.background, opacity);
            let border = blend(base, p.border, opacity);
            if !shapes::tab(
                dc,
                [
                    left as f32,
                    scale(hwnd, 7) as f32,
                    right as f32,
                    bottom as f32,
                ],
                scale(hwnd, 8) as f32,
                background,
                border,
            ) {
                rounded_offset(
                    dc,
                    &RECT {
                        left: 2,
                        top: scale(hwnd, 7),
                        right: width - 2,
                        bottom,
                    },
                    scale(hwnd, 12),
                    background,
                    border,
                    (left as f32, 0.0),
                );
            }
        }
        HeaderStyle::Floating => {
            let half = scale(hwnd, 2).max(1);
            rounded_offset(
                dc,
                &RECT {
                    left: -half,
                    top: bottom - scale(hwnd, 10),
                    right: half,
                    bottom: bottom - scale(hwnd, 6),
                },
                half * 2,
                accent,
                accent,
                (((left + right) / 2.0) as f32, 0.0),
            );
        }
    }
}
unsafe extern "system" fn tabs_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    match message {
        WM_NCHITTEST => unsafe {
            let mut point = POINT {
                x: (l & 0xffff) as i16 as i32,
                y: ((l >> 16) & 0xffff) as i16 as i32,
            };
            ScreenToClient(hwnd, &mut point);
            let mut hit = TCHITTESTINFO {
                pt: point,
                flags: 0,
            };
            if SendMessageW(hwnd, TCM_HITTEST, 0, &mut hit as *mut _ as isize) < 0 {
                return HTTRANSPARENT as isize;
            }
        },
        WM_PAINT => {
            draw_tabs(hwnd, None);
            return 0;
        }
        WM_PRINTCLIENT => {
            draw_tabs(hwnd, Some(w as HDC));
            return 0;
        }
        WM_ERASEBKGND => return 1,
        WM_MOUSEMOVE => unsafe {
            let mut hit = TCHITTESTINFO {
                pt: POINT {
                    x: (l & 0xffff) as i16 as i32,
                    y: ((l >> 16) & 0xffff) as i16 as i32,
                },
                flags: 0,
            };
            let index = SendMessageW(hwnd, TCM_HITTEST, 0, &mut hit as *mut _ as isize) + 1;
            if GetPropW(hwnd, HOVER.as_ptr()) as isize != index {
                SetPropW(hwnd, HOVER.as_ptr(), index as _);
                InvalidateRect(hwnd, null(), 0);
            }
            let mut track = TRACKMOUSEEVENT {
                cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                dwFlags: TME_LEAVE,
                hwndTrack: hwnd,
                dwHoverTime: 0,
            };
            TrackMouseEvent(&mut track);
        },
        WM_MOUSELEAVE => unsafe {
            RemovePropW(hwnd, HOVER.as_ptr());
            InvalidateRect(hwnd, null(), 0);
        },
        WM_NCDESTROY => unsafe {
            RemovePropW(hwnd, HOVER.as_ptr());
        },
        WM_LBUTTONUP | WM_KEYUP => unsafe {
            let result = DefSubclassProc(hwnd, message, w, l);
            InvalidateRect(hwnd, null(), 0);
            return result;
        },
        _ => {}
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}
pub(super) fn setup_search(hwnd: HWND) {
    unsafe {
        SetWindowSubclass(hwnd, Some(search_proc), 4, 0);
        SendMessageW(
            hwnd,
            EM_SETMARGINS,
            EC_LEFTMARGIN as usize | EC_RIGHTMARGIN as usize,
            0,
        );
    }
}
fn search_contains(hwnd: HWND, point: POINT) -> bool {
    let mut bounds = RECT::default();
    unsafe {
        GetClientRect(hwnd, &mut bounds);
    }
    let width = bounds.right * 96 / unsafe { GetDpiForWindow(hwnd) }.max(96) as i32;
    let header = header_layout(hwnd, width);
    header.search_width > 0
        && unsafe {
            PtInRect(
                &rect(
                    hwnd,
                    header.search_left,
                    header.button_top - 1,
                    header.search_width,
                    30,
                ),
                point,
            ) != 0
        }
}
pub(super) fn search_mouse_leave(hwnd: HWND) {
    let opened = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .is_some_and(|v| v.search_open)
    });
    if !opened {
        return;
    }
    let mut point = POINT::default();
    unsafe {
        if GetCursorPos(&mut point) == 0 {
            return;
        }
        ScreenToClient(hwnd, &mut point);
    }
    if !search_contains(hwnd, point) {
        emit(Event::SearchVisibility(hwnd as usize, false));
    }
}
pub(super) fn search_header_mouse(hwnd: HWND, message: u32, lparam: LPARAM) -> bool {
    if message == WM_MOUSEMOVE {
        unsafe {
            let mut track = TRACKMOUSEEVENT {
                cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                dwFlags: TME_LEAVE,
                hwndTrack: hwnd,
                dwHoverTime: 0,
            };
            TrackMouseEvent(&mut track);
        }
    }
    if matches!(message, WM_MOUSEMOVE | WM_MOUSELEAVE) {
        search_mouse_leave(hwnd);
    }
    if message == WM_LBUTTONDOWN
        && search_contains(
            hwnd,
            POINT {
                x: (lparam & 0xffff) as i16 as i32,
                y: ((lparam >> 16) & 0xffff) as i16 as i32,
            },
        )
    {
        unsafe {
            SetFocus(control(hwnd, SEARCH));
        }
        return true;
    }
    if message == WM_LBUTTONDOWN {
        emit(Event::SearchVisibility(hwnd as usize, false));
    }
    false
}
pub(super) fn invalidate_header(hwnd: HWND) {
    unsafe {
        let mut area = RECT::default();
        GetClientRect(hwnd, &mut area);
        area.bottom = scale(hwnd, header_height(hwnd));
        InvalidateRect(hwnd, &area, 0);
    }
}
pub(super) fn block_search_hover(hwnd: HWND) {
    unsafe {
        SetPropW(
            control(hwnd, SEARCH_TOGGLE as i32),
            SEARCH_HOVER_BLOCKED.as_ptr(),
            1usize as _,
        );
    }
}
pub(super) fn refresh_search_hover(hwnd: HWND) {
    let button = control(hwnd, SEARCH_TOGGLE as i32);
    unsafe {
        if IsWindowVisible(button) == 0 || GetPropW(button, SEARCH_HOVER_BLOCKED.as_ptr()).is_null()
        {
            return;
        }
        let mut point = POINT::default();
        let mut bounds = RECT::default();
        if GetCursorPos(&mut point) != 0
            && GetWindowRect(button, &mut bounds) != 0
            && PtInRect(&bounds, point) == 0
        {
            RemovePropW(button, SEARCH_HOVER_BLOCKED.as_ptr());
        }
    }
}
unsafe extern "system" fn search_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if message == WM_KEYDOWN && w == VK_ESCAPE as usize {
        emit(Event::SearchVisibility(
            unsafe { GetParent(hwnd) } as usize,
            false,
        ));
        return 0;
    }
    match message {
        WM_MOUSEMOVE => unsafe {
            if GetPropW(hwnd, HOVER.as_ptr()).is_null() {
                SetPropW(hwnd, HOVER.as_ptr(), 1usize as _);
                let mut track = TRACKMOUSEEVENT {
                    cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                    dwFlags: TME_LEAVE,
                    hwndTrack: hwnd,
                    dwHoverTime: 0,
                };
                TrackMouseEvent(&mut track);
                InvalidateRect(hwnd, null(), 1);
            }
        },
        WM_MOUSELEAVE => unsafe {
            RemovePropW(hwnd, HOVER.as_ptr());
            InvalidateRect(hwnd, null(), 1);
            search_mouse_leave(GetParent(hwnd));
        },
        WM_NCDESTROY => unsafe {
            RemovePropW(hwnd, HOVER.as_ptr());
        },
        _ => {}
    }
    let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
    if matches!(message, WM_SETFOCUS | WM_KILLFOCUS | WM_SETTEXT) {
        // The native edit can repaint directly, leaving our hint behind unless
        // its entire client area is invalidated when the hint's state changes.
        unsafe {
            InvalidateRect(hwnd, null(), 1);
        }
    }
    if matches!(message, WM_SETFOCUS | WM_KILLFOCUS) {
        invalidate_header(unsafe { GetParent(hwnd) });
        if message == WM_KILLFOCUS {
            emit(Event::SearchVisibility(
                unsafe { GetParent(hwnd) } as usize,
                false,
            ));
        }
    }
    if matches!(message, WM_PAINT | WM_PRINTCLIENT)
        && text(hwnd).is_empty()
        && VIEWS.with(|views| {
            views
                .borrow()
                .get(&(unsafe { GetParent(hwnd) } as usize))
                .is_some_and(|v| v.search_open || v.options.search == "always")
        })
        && unsafe { GetFocus() != hwnd && GetPropW(hwnd, HOVER.as_ptr()).is_null() }
    {
        unsafe {
            let dc = if message == WM_PRINTCLIENT {
                w as HDC
            } else {
                GetDC(hwnd)
            };
            let mut area = RECT::default();
            GetClientRect(hwnd, &mut area);
            with_palette(GetParent(hwnd), |palette| {
                label(
                    dc,
                    &crate::locale::text("Search files and folders…"),
                    area,
                    font(GetParent(hwnd), 0),
                    palette.muted,
                    DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
                )
            });
            if message != WM_PRINTCLIENT {
                ReleaseDC(hwnd, dc);
            }
        }
    }
    result
}
pub(super) fn setup_list(list: HWND, parent: HWND, main: bool) {
    unsafe {
        SetWindowTheme(list, wide("DarkMode_Explorer").as_ptr(), null());
        let header = SendMessageW(list, LVM_GETHEADER, 0, 0) as HWND;
        SetWindowSubclass(header, Some(header_proc), 5, parent as usize);
        if main {
            resize_assets(parent);
        }
        SendMessageW(
            list,
            LVM_SETICONSPACING,
            0,
            ((scale(parent, 106) << 16) | scale(parent, 112)) as isize,
        );
    }
}
pub(super) fn resize_assets(parent: HWND) {
    let list = control(parent, LIST);
    if list.is_null() {
        return;
    }
    unsafe {
        if panel(parent) {
            images::configure(parent);
        } else if GetWindowLongPtrW(parent, GWLP_USERDATA) == 1 {
            let images = ImageList_Create(1, scale(parent, 54), ILC_COLOR32, 1, 0);
            let old = RemovePropW(parent, ROW_IMAGES.as_ptr());
            SendMessageW(list, LVM_SETIMAGELIST, LVSIL_SMALL as usize, images);
            SetPropW(parent, ROW_IMAGES.as_ptr(), images as _);
            if !old.is_null() {
                ImageList_Destroy(old as isize);
            }
        }
    }
}
unsafe extern "system" fn header_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    parent: usize,
) -> LRESULT {
    if panel(parent as _) {
        if let Some(result) = details::header_message(hwnd, parent as _, message, w, l) {
            return result;
        }
    }
    if matches!(message, WM_RBUTTONUP | WM_CONTEXTMENU) && panel(parent as _) {
        emit(Event::Command(parent, COLUMNS));
        return 0;
    }
    if message == HDM_LAYOUT {
        let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
        let layout = unsafe { &mut *(l as *mut HDLAYOUT) };
        unsafe {
            (*layout.pwpos).cy = content_scale(parent as _, 36);
            (*layout.prc).top = (*layout.pwpos).cy;
        }
        return result;
    }
    if matches!(message, WM_PAINT | WM_PRINTCLIENT) {
        paint(
            hwnd,
            (message == WM_PRINTCLIENT).then_some(w as HDC),
            |dc, bounds| {
                with_palette(parent as HWND, |p| {
                    fill(dc, &bounds, p.background);
                    let count = unsafe { SendMessageW(hwnd, HDM_GETITEMCOUNT, 0, 0) };
                    for index in 0..count {
                        let mut area = RECT::default();
                        let mut caption = [0u16; 256];
                        let mut item = HDITEMW {
                            mask: HDI_TEXT,
                            pszText: caption.as_mut_ptr(),
                            cchTextMax: 256,
                            ..unsafe { std::mem::zeroed() }
                        };
                        unsafe {
                            SendMessageW(
                                hwnd,
                                HDM_GETITEMRECT,
                                index as usize,
                                &mut area as *mut _ as isize,
                            );
                            SendMessageW(
                                hwnd,
                                HDM_GETITEMW,
                                index as usize,
                                &mut item as *mut _ as isize,
                            );
                        }
                        if area.right - area.left < 8 {
                            continue;
                        }
                        let value = String::from_utf16_lossy(
                            &caption[..caption.iter().position(|c| *c == 0).unwrap_or(256)],
                        );
                        area.left += scale(parent as _, 8);
                        area.right -= scale(parent as _, 8);
                        label(
                            dc,
                            &value,
                            area,
                            font(parent as _, 5),
                            p.muted,
                            DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                        );
                    }
                    fill(
                        dc,
                        &RECT {
                            top: bounds.bottom - 1,
                            ..bounds
                        },
                        p.border,
                    );
                })
            },
        );
        return 0;
    }
    if message == WM_ERASEBKGND {
        return 1;
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}
fn cell_label(hwnd: HWND, item: usize, column: usize) -> String {
    let mut buffer = [0u16; 2048];
    let mut notification: NMLVDISPINFOW = unsafe { std::mem::zeroed() };
    notification.item = LVITEMW {
        mask: LVIF_TEXT,
        iItem: item as i32,
        iSubItem: column as i32,
        pszText: buffer.as_mut_ptr(),
        cchTextMax: buffer.len() as i32,
        ..unsafe { std::mem::zeroed() }
    };
    display_info(hwnd, &mut notification);
    String::from_utf16_lossy(&buffer[..buffer.iter().position(|c| *c == 0).unwrap_or(buffer.len())])
}
pub(super) fn paint_icon_list(parent: HWND, print: Option<HDC>) {
    let list = control(parent, LIST);
    paint(list, print, |dc, bounds| {
        // Clear the entire damaged area, including gaps left by old columns.
        // Present background, icons and text in one buffer instead of letting
        // native erasure and custom post-paint expose intermediate frames.
        with_palette(parent, |p| fill(dc, &bounds, p.background));
        draw_icon_grid(parent, dc);
        empty(list, parent, dc);
        if let Some(area) = marquee::rectangle(list) {
            with_palette(parent, |p| {
                if !shapes::marquee(dc, &area, p.accent) {
                    unsafe {
                        let brush = CreateSolidBrush(p.accent);
                        FrameRect(dc, &area, brush);
                        DeleteObject(brush);
                    }
                }
            });
        }
    });
}
fn draw_icon_grid(hwnd: HWND, dc: HDC) {
    let list = control(hwnd, LIST);
    let rows = VIEWS
        .with(|views| views.borrow().get(&(hwnd as usize)).map(|v| v.rows.clone()))
        .unwrap_or_default();
    if rows.is_empty() {
        return;
    }
    unsafe {
        let mut client = RECT::default();
        GetClientRect(list, &mut client);
        let preview = icon_grid::preview(hwnd);
        let (width, height) = preview.map_or_else(|| images::cell(hwnd), |g| (g.width, g.height));
        let mut first = RECT {
            left: LVIR_BOUNDS as i32,
            ..Default::default()
        };
        SendMessageW(list, LVM_GETITEMRECT, 0, &mut first as *mut RECT as isize);
        let mut columns = preview.map_or(1, |g| g.columns);
        if preview.is_none() {
            for i in 1..rows.len().min(512) {
                let mut r = RECT {
                    left: LVIR_BOUNDS as i32,
                    ..Default::default()
                };
                SendMessageW(list, LVM_GETITEMRECT, i, &mut r as *mut RECT as isize);
                if r.top != first.top {
                    break;
                }
                columns += 1;
            }
        }
        let start = ((-first.top).max(0) / height) as usize * columns;
        let visible_rows = (client.bottom + (-first.top).max(0) % height + height - 1) / height;
        let end = (start + visible_rows.max(1) as usize * columns).min(rows.len());
        let spec = images::spec(hwnd);
        let padding = content_scale(hwnd, 5);
        let label_gap = content_scale(hwnd, 6);
        let label_font = font(hwnd, 5);
        let hot_item = GetPropW(list, wide("DPRowHot").as_ptr()) as usize;
        with_palette(hwnd, |p| {
            for (i, row) in rows.iter().enumerate().take(end).skip(start) {
                let left = first.left + (i % columns) as i32 * width;
                let top = first.top + (i / columns) as i32 * height;
                let rect = RECT {
                    left,
                    top,
                    right: left + width,
                    bottom: top + height,
                };
                if rect.bottom < 0 || rect.top > client.bottom || RectVisible(dc, &rect) == 0 {
                    continue;
                }
                let selected = SendMessageW(list, LVM_GETITEMSTATE, i, LVIS_SELECTED as isize) != 0;
                let hot = hot_item == i + 1;
                let background = motion::color_in(
                    list,
                    i as u32 * 2,
                    if selected {
                        p.selected
                    } else if hot {
                        p.hover
                    } else {
                        p.background
                    },
                    rect,
                );
                let border = motion::color_in(
                    list,
                    i as u32 * 2 + 1,
                    if selected {
                        p.accent
                    } else if hot {
                        p.border
                    } else {
                        p.background
                    },
                    rect,
                );
                // Most cells have no visible card. Avoid GDI+ path creation
                // and rasterization for a shape identical to the background.
                if background != p.background || border != p.background {
                    let mut card = rect;
                    InflateRect(&mut card, -scale(hwnd, 3), -scale(hwnd, 2));
                    rounded(dc, &card, scale(hwnd, 10), background, border);
                }
                let top = rect.top + padding;
                images::paint(hwnd, row, dc, rect.left + (width - spec.edge) / 2, top);
                let label_rect = RECT {
                    left: rect.left + padding,
                    right: rect.right - padding,
                    top: top + spec.edge + label_gap,
                    bottom: rect.bottom - padding,
                };
                label(
                    dc,
                    &String::from_utf16_lossy(&row.name),
                    label_rect,
                    label_font,
                    p.text,
                    DT_CENTER | DT_WORDBREAK | DT_END_ELLIPSIS,
                );
            }
        });
        images::prefetch(hwnd, &rows, start..end);
    }
}
pub(super) fn custom_list(hwnd: HWND, draw: &mut NMLVCUSTOMDRAW) -> LRESULT {
    let list = control(hwnd, LIST);
    let details = unsafe { SendMessageW(list, LVM_GETVIEW, 0, 0) == LV_VIEW_DETAILS as isize };
    // Panels own WM_PAINT and present one complete buffered content surface. The
    // native control also custom-draws synchronously while arranging items,
    // including during WM_SETREDRAW updates. Painting that path would overlay
    // new rows on the previous tab before the final spacing/scrollbars exist.
    if content::updating(list) || panel(hwnd) {
        return CDRF_SKIPDEFAULT as isize;
    }
    if draw.nmcd.dwDrawStage == CDDS_PREPAINT {
        return (CDRF_NOTIFYITEMDRAW | CDRF_NOTIFYPOSTPAINT) as isize;
    }
    let index = draw.nmcd.dwItemSpec;
    if draw.nmcd.dwDrawStage == CDDS_POSTPAINT {
        if !details {
            draw_icon_grid(hwnd, draw.nmcd.hdc);
        } else if panel(hwnd) {
            images::prefetch_details(hwnd);
        }
        empty(list, hwnd, draw.nmcd.hdc);
        return CDRF_DODEFAULT as isize;
    }
    if !details {
        return CDRF_SKIPDEFAULT as isize;
    }
    if draw.nmcd.dwDrawStage == CDDS_ITEMPREPAINT {
        return CDRF_NOTIFYSUBITEMDRAW as isize;
    }
    if draw.nmcd.dwDrawStage != CDDS_ITEMPREPAINT
        && draw.nmcd.dwDrawStage != CDDS_ITEMPREPAINT | CDDS_SUBITEM
    {
        return CDRF_DODEFAULT as isize;
    }
    let selected =
        unsafe { SendMessageW(list, LVM_GETITEMSTATE, index, LVIS_SELECTED as isize) != 0 };
    let hot = unsafe { GetPropW(list, wide("DPRowHot").as_ptr()) as usize == index + 1 };
    let (main, row) = VIEWS.with(|v| {
        v.borrow()
            .get(&(hwnd as usize))
            .map(|v| (v.is_main, v.rows.get(index).cloned()))
            .unwrap_or_default()
    });
    let mut area = draw.nmcd.rc;
    unsafe {
        let mut r = RECT {
            left: LVIR_BOUNDS as i32,
            ..Default::default()
        };
        if SendMessageW(list, LVM_GETITEMRECT, index, &mut r as *mut RECT as isize) != 0 {
            area = r;
        }
    }
    with_palette(hwnd, |p| {
        let background = if selected {
            p.selected
        } else if hot {
            p.hover
        } else if main {
            blend(p.background, p.surface, 30)
        } else {
            p.background
        };
        unsafe {
            let header = SendMessageW(list, LVM_GETHEADER, 0, 0) as HWND;
            let mut column = RECT::default();
            if SendMessageW(
                header,
                HDM_GETITEMRECT,
                draw.iSubItem as usize,
                &mut column as *mut RECT as isize,
            ) != 0
            {
                MapWindowPoints(header, list, (&mut column as *mut RECT).cast(), 2);
                area.left = column.left;
                area.right = column.right;
            }
        }
        if area.right - area.left < 1 {
            return;
        }
        back_buffer::paint(draw.nmcd.hdc, area, glass::for_window(list), |cell_dc| {
            // A row owns one transition across all columns, so damage spans its
            // complete visible width even when only one subitem is being painted.
            let mut row_area = RECT::default();
            unsafe {
                GetClientRect(list, &mut row_area);
            }
            row_area.top = area.top;
            row_area.bottom = area.bottom;
            let background = motion::color_in(list, index as u32, background, row_area);
            fill(cell_dc, &area, background);
            if main {
                fill(
                    cell_dc,
                    &RECT {
                        top: area.bottom - scale(hwnd, 4),
                        ..area
                    },
                    p.background,
                );
            }
            if selected && draw.iSubItem == 0 {
                rounded(
                    cell_dc,
                    &RECT {
                        left: area.left + scale(hwnd, 1),
                        right: area.left + scale(hwnd, 4),
                        top: area.top + scale(hwnd, 9),
                        bottom: area.bottom - scale(hwnd, 9),
                    },
                    scale(hwnd, 3),
                    p.accent,
                    p.accent,
                );
            }
            let mut text_area = area;
            if main && draw.iSubItem == 3 {
                dashboard::draw_visibility(hwnd, cell_dc, index, area);
                return;
            }
            text_area.left += content_scale(hwnd, 8);
            text_area.right -= content_scale(hwnd, 8);
            if draw.iSubItem == 0 {
                if let Some(row) = &row {
                    let spec = images::spec(hwnd);
                    images::paint(
                        hwnd,
                        row,
                        cell_dc,
                        area.left + content_scale(hwnd, 4),
                        area.top,
                    );
                    text_area.left = area.left + spec.edge + content_scale(hwnd, 10);
                }
            }
            label(
                cell_dc,
                &cell_label(hwnd, index, draw.iSubItem as usize),
                text_area,
                font(
                    hwnd,
                    if main {
                        usize::from(draw.iSubItem == 0)
                    } else {
                        5
                    },
                ),
                if draw.iSubItem == 0 { p.text } else { p.muted },
                DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
            );
        });
    });
    CDRF_SKIPDEFAULT as isize
}
fn empty(list: HWND, parent: HWND, dc: HDC) {
    // Native themes draw column separators into the unused report area.
    // Draw the unused space and empty state in the ListView's POSTPAINT buffer,
    // so no second, unbuffered pass erases icons after Windows presents a frame.
    unsafe {
        let count = SendMessageW(list, LVM_GETITEMCOUNT, 0, 0);
        if count > 0
            && !panel(parent)
            && SendMessageW(list, LVM_GETVIEW, 0, 0) == LV_VIEW_DETAILS as isize
        {
            let mut last = RECT {
                left: LVIR_BOUNDS as i32,
                ..RECT::default()
            };
            if SendMessageW(
                list,
                LVM_GETITEMRECT,
                count as usize - 1,
                &mut last as *mut _ as isize,
            ) != 0
            {
                let mut area = RECT::default();
                GetClientRect(list, &mut area);
                area.top = last.bottom.max(scale(parent, 36));
                if area.top < area.bottom {
                    with_palette(parent, |palette| {
                        back_buffer::paint(dc, area, glass::for_window(list), |dc| {
                            fill(dc, &area, palette.background)
                        })
                    });
                }
            }
        }
    }
    let info = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(parent as usize))
            .filter(|v| v.rows.iter().all(|row| row.parent_navigation) && v.main.is_empty())
            .map(|v| {
                (
                    v.is_main,
                    v.loaded,
                    v.searching,
                    v.photos,
                    v.kind.clone(),
                    !v.rows.is_empty(),
                )
            })
    });
    let Some((main, loaded, searching, photos, kind, parent_row)) = info else {
        return;
    };
    let (title, hint, icon) = if !loaded {
        ("Loading…", "", "\u{e895}")
    } else if searching {
        ("No results", "Try a different search term.", "\u{e721}")
    } else if main {
        (
            "Make room for what matters.",
            "Create a panel to keep your files close.",
            "\u{e8b7}",
        )
    } else if kind == "RecycleBin" {
        (
            "Recycle Bin is empty",
            "Deleted files will appear here.",
            "\u{e74d}",
        )
    } else if photos {
        (
            "No photos yet",
            "Add images to this folder to see them here.",
            "\u{eb9f}",
        )
    } else if kind == "List" {
        (
            "Your favorites, together",
            "Drop files or folders here to pin them.",
            "\u{e718}",
        )
    } else {
        (
            "This folder is empty",
            "Drop files or folders here to add them.",
            "\u{e8b7}",
        )
    };
    unsafe {
        let mut area = RECT::default();
        GetClientRect(list, &mut area);
        let top = (area.bottom / 2 - scale(parent, 64)).max(scale(parent, 48));
        let center = area.right / 2;
        with_palette(parent, |p| {
            let mut content = RECT {
                top: if SendMessageW(list, LVM_GETVIEW, 0, 0) == LV_VIEW_DETAILS as isize {
                    if panel(parent) {
                        content_scale(parent, 38)
                    } else {
                        scale(parent, 36)
                    }
                } else {
                    0
                },
                ..area
            };
            if parent_row {
                let mut row = RECT {
                    left: LVIR_BOUNDS as i32,
                    ..RECT::default()
                };
                if SendMessageW(list, LVM_GETITEMRECT, 0, &mut row as *mut RECT as isize) != 0 {
                    content.top = content.top.max(row.bottom);
                }
            }
            fill(dc, &content, p.background);
            let top = top.max(content.top + scale(parent, 12));
            let badge = RECT {
                left: center - scale(parent, 24),
                top,
                right: center + scale(parent, 24),
                bottom: top + scale(parent, 48),
            };
            rounded(dc, &badge, scale(parent, 24), p.surface, p.surface);
            label(
                dc,
                icon,
                badge,
                font(parent, 3),
                p.accent,
                DT_CENTER | DT_SINGLELINE | DT_VCENTER,
            );
            label(
                dc,
                &crate::locale::text(title),
                RECT {
                    left: 12,
                    top: top + scale(parent, 62),
                    right: area.right - 12,
                    bottom: top + scale(parent, 86),
                },
                font(parent, 1),
                p.text,
                DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
            );
            label(
                dc,
                &crate::locale::text(hint),
                RECT {
                    left: 18,
                    top: top + scale(parent, 92),
                    right: area.right - 18,
                    bottom: top + scale(parent, 136),
                },
                font(parent, 0),
                p.muted,
                DT_CENTER | DT_WORDBREAK,
            );
        });
    }
}
