//! Native menu navigation and accessibility, with the application's palette.
use super::*;
thread_local! { static ITEMS: RefCell<HashMap<usize, String>> = RefCell::new(HashMap::new()); }

pub fn popup(owner: HWND, point: Option<POINT>, items: &[(u16, String)]) -> Option<u16> {
    unsafe {
        let menu = CreatePopupMenu();
        if menu.is_null() {
            return None;
        }
        let captions: Vec<String> = items
            .iter()
            .map(|(_, text)| crate::locale::text(text))
            .collect();
        for (position, ((id, _), caption)) in items.iter().zip(&captions).enumerate() {
            AppendMenuW(menu, MF_STRING, *id as usize, wide(caption).as_ptr());
            {
                let key = caption as *const String as usize;
                ITEMS.with(|data| data.borrow_mut().insert(key, caption.clone()));
                let item = MENUITEMINFOW {
                    cbSize: std::mem::size_of::<MENUITEMINFOW>() as u32,
                    fMask: MIIM_FTYPE | MIIM_DATA | MIIM_STATE,
                    fType: MFT_OWNERDRAW,
                    fState: if *id == 0 { MFS_DISABLED } else { MFS_ENABLED },
                    dwItemData: key,
                    ..std::mem::zeroed()
                };
                SetMenuItemInfoW(menu, position as u32, 1, &item);
            }
        }
        let brush = with_palette(owner, |p| CreateSolidBrush(p.surface));
        SetMenuInfo(
            menu,
            &MENUINFO {
                cbSize: std::mem::size_of::<MENUINFO>() as u32,
                fMask: MIM_BACKGROUND,
                hbrBack: brush,
                ..std::mem::zeroed()
            },
        );
        let mut point = point.unwrap_or_default();
        if point.x == 0 && point.y == 0 {
            GetCursorPos(&mut point);
        }
        SetForegroundWindow(owner);
        let selected = TrackPopupMenu(
            menu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON,
            point.x,
            point.y,
            0,
            owner,
            null(),
        );
        DestroyMenu(menu);
        DeleteObject(brush);
        ITEMS.with(|data| {
            let mut data = data.borrow_mut();
            for caption in &captions {
                data.remove(&(caption as *const String as usize));
            }
        });
        PostMessageW(owner, WM_NULL, 0, 0);
        (selected != 0).then_some(selected as u16)
    }
}
pub fn message(hwnd: HWND, message: u32, lparam: LPARAM) -> Option<LRESULT> {
    if lparam == 0 {
        return None;
    }
    if message == WM_MEASUREITEM {
        let item = unsafe { &mut *(lparam as *mut MEASUREITEMSTRUCT) };
        if item.CtlType != ODT_MENU {
            return None;
        }
        let caption = ITEMS.with(|data| data.borrow().get(&item.itemData).cloned())?;
        let mut size = SIZE::default();
        unsafe {
            let dc = GetDC(hwnd);
            let old = SelectObject(dc, chrome::font(hwnd, 0));
            let value = wide(&caption);
            GetTextExtentPoint32W(dc, value.as_ptr(), value.len() as i32 - 1, &mut size);
            SelectObject(dc, old);
            ReleaseDC(hwnd, dc);
        }
        item.itemWidth = (size.cx + chrome::scale(hwnd, 72)).max(chrome::scale(hwnd, 220)) as u32;
        item.itemHeight = chrome::scale(hwnd, if caption.is_empty() { 12 } else { 36 }) as u32;
        return Some(1);
    }
    if message == WM_DRAWITEM {
        let item = unsafe { &*(lparam as *const DRAWITEMSTRUCT) };
        if item.CtlType != ODT_MENU {
            return None;
        }
        let caption = ITEMS.with(|data| data.borrow().get(&item.itemData).cloned())?;
        with_palette(hwnd, |p| {
            chrome::fill(item.hDC, &item.rcItem, p.surface);
            let mut area = item.rcItem;
            unsafe {
                InflateRect(&mut area, -chrome::scale(hwnd, 4), -chrome::scale(hwnd, 2));
            }
            if caption.is_empty() {
                let center = (area.top + area.bottom) / 2;
                area.left += chrome::scale(hwnd, 12);
                area.right -= chrome::scale(hwnd, 12);
                area.top = center;
                area.bottom = center + 1;
                chrome::fill(item.hDC, &area, p.border);
                return;
            }
            let selected = item.itemState & ODS_SELECTED != 0;
            if selected {
                chrome::rounded(
                    item.hDC,
                    &area,
                    chrome::scale(hwnd, 10),
                    p.selected,
                    p.selected,
                );
            }
            area.left += chrome::scale(hwnd, 12);
            area.right -= chrome::scale(hwnd, 12);
            let (title, shortcut) = caption.split_once('\t').unwrap_or((&caption, ""));
            if !shortcut.is_empty() {
                chrome::label(
                    item.hDC,
                    shortcut,
                    area,
                    chrome::font(hwnd, 0),
                    p.muted,
                    DT_RIGHT | DT_VCENTER | DT_SINGLELINE,
                );
                area.right -= chrome::scale(hwnd, 88);
            }
            chrome::label(
                item.hDC,
                title,
                area,
                chrome::font(hwnd, 0),
                if selected { p.accent } else { p.text },
                DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
        });
        return Some(1);
    }
    None
}
