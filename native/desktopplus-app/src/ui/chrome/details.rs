//! Buffered report rows; native geometry remains authoritative for input and scrolling.
use super::*;

const HEADER_HOT: &str = "DPHeaderHot";

pub(in crate::ui) fn set_sort(parent: HWND, column: usize, descending: bool) {
    unsafe {
        let header = SendMessageW(control(parent, LIST), LVM_GETHEADER, 0, 0) as HWND;
        for i in 0..SendMessageW(header, HDM_GETITEMCOUNT, 0, 0) as usize {
            let mut item = HDITEMW {
                mask: HDI_FORMAT,
                ..std::mem::zeroed()
            };
            SendMessageW(header, HDM_GETITEMW, i, &mut item as *mut _ as isize);
            item.fmt &= !(HDF_SORTUP | HDF_SORTDOWN);
            if i == column {
                item.fmt |= if descending { HDF_SORTDOWN } else { HDF_SORTUP };
            }
            SendMessageW(header, HDM_SETITEMW, i, &item as *const _ as isize);
        }
        InvalidateRect(header, null(), 0);
    }
}

fn column_rect(header: HWND, list: HWND, index: usize) -> RECT {
    let mut area = RECT::default();
    unsafe {
        SendMessageW(header, HDM_GETITEMRECT, index, &mut area as *mut _ as isize);
        MapWindowPoints(header, list, (&mut area as *mut RECT).cast(), 2);
    }
    area
}

pub(in crate::ui) fn paint_details(parent: HWND, print: Option<HDC>) {
    let list = control(parent, LIST);
    unsafe {
        let mut ps = PAINTSTRUCT::default();
        let dc = print.unwrap_or_else(|| BeginPaint(list, &mut ps));
        let mut bounds = RECT::default();
        GetClientRect(list, &mut bounds);
        let header = SendMessageW(list, LVM_GETHEADER, 0, 0) as HWND;
        let mut header_bounds = RECT::default();
        GetWindowRect(header, &mut header_bounds);
        MapWindowPoints(
            null_mut(),
            list,
            (&mut header_bounds as *mut RECT).cast(),
            2,
        );
        bounds.top = header_bounds.bottom.clamp(bounds.top, bounds.bottom);
        // The header is a separately painted child. Exclude it from both the
        // clear AND the final buffer blit, including WM_PRINTCLIENT: otherwise
        // every row hover/reveal frame erases the captions until a later paint.
        // Keep one alpha conversion for all rows and the space to their right.
        back_buffer::paint(dc, bounds, glass::for_window(list), |dc| {
            with_palette(parent, |p| fill(dc, &bounds, p.background));
            draw_rows(parent, list, header, dc, bounds);
            empty(list, parent, dc);
        });
        if print.is_none() {
            EndPaint(list, &ps);
        }
    }
}

fn draw_rows(parent: HWND, list: HWND, header: HWND, dc: HDC, bounds: RECT) {
    let rows = VIEWS
        .with(|views| {
            views
                .borrow()
                .get(&(parent as usize))
                .map(|v| v.rows.clone())
        })
        .unwrap_or_default();
    unsafe {
        let start = SendMessageW(list, LVM_GETTOPINDEX, 0, 0).max(0) as usize;
        let count = SendMessageW(list, LVM_GETCOUNTPERPAGE, 0, 0).max(0) as usize + 2;
        let end = rows.len().min(start.saturating_add(count));
        let columns: Vec<_> = (0..crate::metadata::COLUMNS.len())
            .map(|i| (i, column_rect(header, list, i)))
            .filter(|(_, r)| r.right > r.left && r.right > 0 && r.left < bounds.right)
            .collect();
        let hot = GetPropW(list, wide("DPRowHot").as_ptr()) as usize;
        let padding = content_scale(parent, 12);
        let spec = images::spec(parent);
        with_palette(parent, |p| {
            for (index, row) in rows.iter().enumerate().take(end).skip(start) {
                let mut area = RECT {
                    left: LVIR_BOUNDS as i32,
                    ..Default::default()
                };
                if SendMessageW(list, LVM_GETITEMRECT, index, &mut area as *mut _ as isize) == 0 {
                    continue;
                }
                area.left = bounds.left;
                area.right = bounds.right;
                if RectVisible(dc, &area) == 0 {
                    continue;
                }
                let state = SendMessageW(
                    list,
                    LVM_GETITEMSTATE,
                    index,
                    (LVIS_SELECTED | LVIS_FOCUSED) as isize,
                ) as u32;
                let selected = state & LVIS_SELECTED != 0;
                let focused = state & LVIS_FOCUSED != 0 && GetFocus() == list;
                let background = motion::color_in(
                    list,
                    index as u32,
                    if selected {
                        p.selected
                    } else if hot == index + 1 {
                        p.hover
                    } else {
                        p.background
                    },
                    area,
                );
                let mut card = area;
                InflateRect(
                    &mut card,
                    -content_scale(parent, 2),
                    -content_scale(parent, 2),
                );
                if background != p.background || focused {
                    rounded(
                        dc,
                        &card,
                        content_scale(parent, 8),
                        background,
                        if focused {
                            blend(p.background, p.accent, 55)
                        } else {
                            background
                        },
                    );
                }
                if selected {
                    let mark = RECT {
                        left: card.left + content_scale(parent, 2),
                        right: card.left + content_scale(parent, 5),
                        top: card.top + content_scale(parent, 8),
                        bottom: card.bottom - content_scale(parent, 8),
                    };
                    rounded(dc, &mark, content_scale(parent, 3), p.accent, p.accent);
                }
                for (column, rect) in &columns {
                    let cell = RECT {
                        left: rect.left,
                        right: rect.right,
                        ..area
                    };
                    let saved = SaveDC(dc);
                    IntersectClipRect(dc, cell.left, cell.top, cell.right, cell.bottom);
                    let mut text = RECT {
                        left: cell.left + padding,
                        right: cell.right - padding,
                        ..cell
                    };
                    if *column == 0 {
                        images::paint(
                            parent,
                            row,
                            dc,
                            text.left,
                            area.top + (area.bottom - area.top - spec.height) / 2,
                        );
                        text.left += spec.edge + content_scale(parent, 10);
                    }
                    label(
                        dc,
                        &cell_label(parent, index, *column),
                        text,
                        font(parent, 5),
                        if *column == 0 || selected {
                            p.text
                        } else {
                            p.muted
                        },
                        DT_SINGLELINE
                            | DT_VCENTER
                            | DT_END_ELLIPSIS
                            | if *column == 2 { DT_RIGHT } else { DT_LEFT },
                    );
                    RestoreDC(dc, saved);
                }
            }
        });
        images::prefetch_details(parent);
    }
}

pub(super) fn header_message(
    hwnd: HWND,
    parent: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
) -> Option<LRESULT> {
    if matches!(message, WM_PAINT | WM_PRINTCLIENT | WM_ERASEBKGND)
        && content::updating(unsafe { GetParent(hwnd) })
    {
        if message == WM_PAINT {
            unsafe {
                let mut ps = PAINTSTRUCT::default();
                BeginPaint(hwnd, &mut ps);
                EndPaint(hwnd, &ps);
            }
        }
        return Some(1);
    }
    unsafe {
        if matches!(message, WM_MOUSEMOVE | WM_MOUSELEAVE | WM_NCDESTROY) {
            let key = wide(HEADER_HOT);
            let previous = GetPropW(hwnd, key.as_ptr()) as usize;
            let hot = if message == WM_MOUSEMOVE {
                let mut hit = HDHITTESTINFO {
                    pt: POINT {
                        x: l as i16 as i32,
                        y: (l >> 16) as i16 as i32,
                    },
                    ..std::mem::zeroed()
                };
                let index = SendMessageW(hwnd, HDM_HITTEST, 0, &mut hit as *mut _ as isize);
                let mut tracking = TRACKMOUSEEVENT {
                    cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                    dwFlags: TME_LEAVE,
                    hwndTrack: hwnd,
                    dwHoverTime: 0,
                };
                TrackMouseEvent(&mut tracking);
                (index + 1).max(0) as usize
            } else {
                0
            };
            if hot != previous {
                SetPropW(hwnd, key.as_ptr(), hot as _);
                InvalidateRect(hwnd, null(), 0);
            }
            if message == WM_NCDESTROY {
                RemovePropW(hwnd, key.as_ptr());
            }
        }
        if message == HDM_LAYOUT {
            let result = DefSubclassProc(hwnd, message, w, l);
            let layout = &mut *(l as *mut HDLAYOUT);
            let height = content_scale(parent, 38);
            (*layout.prc).top = height;
            (*layout.pwpos).cy = height;
            return Some(result);
        }
    }
    if matches!(message, WM_PAINT | WM_PRINTCLIENT) {
        paint(
            hwnd,
            (message == WM_PRINTCLIENT).then_some(w as HDC),
            |dc, bounds| {
                with_palette(parent, |p| unsafe {
                    fill(dc, &bounds, p.background);
                    let hot = GetPropW(hwnd, wide(HEADER_HOT).as_ptr()) as usize;
                    let padding = content_scale(parent, 12);
                    for index in 0..SendMessageW(hwnd, HDM_GETITEMCOUNT, 0, 0) as usize {
                        let area = column_rect(hwnd, hwnd, index);
                        if area.right <= area.left
                            || area.right <= bounds.left
                            || area.left >= bounds.right
                        {
                            continue;
                        }
                        let saved = SaveDC(dc);
                        IntersectClipRect(dc, area.left, area.top, area.right, area.bottom);
                        let mut caption = [0u16; 256];
                        let mut item = HDITEMW {
                            mask: HDI_TEXT | HDI_FORMAT,
                            pszText: caption.as_mut_ptr(),
                            cchTextMax: 256,
                            ..std::mem::zeroed()
                        };
                        SendMessageW(hwnd, HDM_GETITEMW, index, &mut item as *mut _ as isize);
                        let sorted = item.fmt & (HDF_SORTUP | HDF_SORTDOWN) != 0;
                        if hot == index + 1 {
                            let mut card = area;
                            InflateRect(&mut card, -2, -content_scale(parent, 4));
                            rounded(dc, &card, content_scale(parent, 6), p.hover, p.hover);
                        }
                        // Short divider handles show that columns can be resized.
                        fill(
                            dc,
                            &RECT {
                                left: area.right - 1,
                                top: area.top + content_scale(parent, 12),
                                bottom: area.bottom - content_scale(parent, 12),
                                ..area
                            },
                            p.border,
                        );
                        let mut text = RECT {
                            left: area.left + padding,
                            right: area.right - padding,
                            ..area
                        };
                        if sorted {
                            let arrow = RECT {
                                left: text.right - content_scale(parent, 10),
                                ..text
                            };
                            label(
                                dc,
                                if item.fmt & HDF_SORTDOWN != 0 {
                                    "\u{e70d}"
                                } else {
                                    "\u{e70e}"
                                },
                                arrow,
                                font(parent, 6),
                                p.accent,
                                DT_SINGLELINE | DT_VCENTER | DT_CENTER,
                            );
                            text.right = arrow.left - content_scale(parent, 6);
                        }
                        let caption = String::from_utf16_lossy(
                            &caption[..caption
                                .iter()
                                .position(|c| *c == 0)
                                .unwrap_or(caption.len())],
                        );
                        label(
                            dc,
                            &caption,
                            text,
                            font(parent, 5),
                            if sorted { p.text } else { p.muted },
                            DT_SINGLELINE
                                | DT_VCENTER
                                | DT_END_ELLIPSIS
                                | if index == 2 { DT_RIGHT } else { DT_LEFT },
                        );
                        RestoreDC(dc, saved);
                    }
                    fill(
                        dc,
                        &RECT {
                            top: bounds.bottom - 1,
                            ..bounds
                        },
                        p.border,
                    );
                });
            },
        );
        return Some(0);
    }
    None
}
