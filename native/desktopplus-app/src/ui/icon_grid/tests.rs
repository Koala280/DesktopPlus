use super::*;

#[test]
fn cells_grow_until_another_column_fits_then_redistribute() {
    for minimum in [70, 100, 150, 200, 216, 432] {
        for columns in 2..16 {
            let start = minimum * columns;
            assert_eq!(cell_width(start, minimum), minimum);
            let before = cell_width(start + minimum - 1, minimum);
            assert!(before > minimum);
            assert!(before * columns < start + minimum);
            assert_eq!(cell_width(start + minimum, minimum), minimum);
            for available in start..start + minimum {
                let width = cell_width(available, minimum);
                assert!(width >= minimum && width <= minimum * 3 / 2);
                assert!(available - width * columns < columns);
            }
        }
    }
}

#[test]
fn narrow_or_empty_clients_keep_bounded_positive_spacing() {
    for available in [-1, 0, 1, 99, 100] {
        assert_eq!(cell_width(available, 100), 100);
    }
    assert_eq!(cell_width(125, 100), 125);
    assert_eq!(cell_width(199, 100), 150);
}

// Hidden native controls exercise Windows' actual wrapping and scrollbar
// behavior without launching the application or opening any visible windows.
struct Grid {
    parent: HWND,
    list: HWND,
    images: HIMAGELIST,
}

unsafe extern "system" fn parent_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if message == WM_NOTIFY && unsafe { (*(l as *const NMHDR)).code } == LVN_GETDISPINFOW {
        let info = unsafe { &mut *(l as *mut NMLVDISPINFOW) };
        if info.item.mask & LVIF_TEXT != 0 {
            let name = wide("Folder with a longer name");
            if info.item.cchTextMax > 0 {
                let length = (name.len() - 1).min(info.item.cchTextMax as usize - 1);
                unsafe {
                    std::ptr::copy_nonoverlapping(name.as_ptr(), info.item.pszText, length);
                    *info.item.pszText.add(length) = 0;
                }
            }
        }
        info.item.iImage = 0;
        return 0;
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}

impl Grid {
    fn new() -> Self {
        unsafe {
            InitCommonControlsEx(&INITCOMMONCONTROLSEX {
                dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
                dwICC: ICC_LISTVIEW_CLASSES,
            });
            let parent = CreateWindowExW(
                0,
                wide("STATIC").as_ptr(),
                null(),
                WS_POPUP,
                0,
                0,
                1000,
                800,
                null_mut(),
                null_mut(),
                null_mut(),
                null(),
            );
            assert!(!parent.is_null());
            SetWindowLongPtrW(parent, GWLP_USERDATA, 2);
            SetWindowSubclass(parent, Some(parent_proc), 2, 0);
            let list = CreateWindowExW(
                0,
                wide("SysListView32").as_ptr(),
                null(),
                WS_CHILD
                    | WS_VISIBLE
                    | LVS_ICON
                    | LVS_OWNERDATA
                    | LVS_AUTOARRANGE
                    | LVS_SHAREIMAGELISTS,
                0,
                0,
                700,
                300,
                parent,
                LIST as usize as _,
                null_mut(),
                null(),
            );
            assert!(!list.is_null());
            let images = ImageList_Create(48, 48, ILC_COLOR32, 1, 0);
            ImageList_SetImageCount(images, 1);
            SendMessageW(list, LVM_SETIMAGELIST, LVSIL_NORMAL as usize, images);
            SetWindowSubclass(list, Some(list_proc), 1, parent as usize);
            let grid = Self {
                parent,
                list,
                images,
            };
            grid.count(80);
            grid
        }
    }

    fn resize(&self, width: i32, height: i32) {
        layout(self.parent, 0, 0, width, height);
    }

    fn count(&self, count: usize) {
        unsafe {
            SendMessageW(self.list, LVM_SETITEMCOUNT, count, 0);
        }
    }

    fn client(&self) -> RECT {
        let mut client = RECT::default();
        unsafe {
            GetClientRect(self.list, &mut client);
        }
        client
    }

    fn item(&self, index: usize) -> RECT {
        let mut rect = RECT {
            left: LVIR_BOUNDS as i32,
            ..Default::default()
        };
        assert_ne!(
            unsafe {
                SendMessageW(
                    self.list,
                    LVM_GETITEMRECT,
                    index,
                    &mut rect as *mut RECT as isize,
                )
            },
            0
        );
        rect
    }

    fn assert_layout(&self) {
        let minimum = images::minimum_cell(self.parent).0;
        let client = self.client();
        let (width, height) = images::cell(self.parent);
        assert!(width >= minimum && width <= minimum * 3 / 2);
        let columns = (client.right / minimum).max(1) as usize;
        let count = unsafe { SendMessageW(self.list, LVM_GETITEMCOUNT, 0, 0) } as usize;
        if count == 0 {
            return;
        }
        let first = self.item(0);
        for index in 0..count.min(columns * 2) {
            let item = self.item(index);
            assert_eq!(
                item.left,
                first.left + (index % columns) as i32 * width,
                "item {index}, client {}x{}, spacing {width}x{height}",
                client.right,
                client.bottom
            );
            assert_eq!(
                item.top,
                first.top + (index / columns) as i32 * height,
                "item {index}, client {}x{}, spacing {width}x{height}",
                client.right,
                client.bottom
            );
            let mut hit = LVHITTESTINFO {
                pt: POINT {
                    x: item.left + width / 2,
                    y: item.top + 24,
                },
                ..Default::default()
            };
            if hit.pt.y >= 0 && hit.pt.y < client.bottom {
                assert_eq!(
                    unsafe { SendMessageW(self.list, LVM_HITTEST, 0, &mut hit as *mut _ as isize) },
                    index as isize,
                    "painted icon must hit item {index}"
                );
            }
        }
    }
}

impl Drop for Grid {
    fn drop(&mut self) {
        forget(self.parent);
        VIEWS.with(|views| views.borrow_mut().remove(&(self.parent as usize)));
        chrome::release(self.parent);
        unsafe {
            DestroyWindow(self.parent);
            ImageList_Destroy(self.images);
        }
    }
}

fn selection_mouse(grid: &Grid, message: u32, flags: u32, x: i32, y: i32) {
    let position = ((y as u16 as u32) << 16) | x as u16 as u32;
    unsafe { SendMessageW(grid.list, message, flags as usize, position as isize) };
}

#[test]
fn marquee_keeps_capture_outside_and_commits_on_release() {
    use windows_sys::Win32::System::SystemServices::MK_LBUTTON;
    let grid = Grid::new();
    grid.count(3);
    selection_mouse(&grid, WM_LBUTTONDOWN, MK_LBUTTON, 650, 250);
    assert_eq!(unsafe { GetCapture() }, grid.list);
    assert!(marquee::rectangle(grid.list).is_none());
    selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON, -40, -40);
    unsafe { SendMessageW(grid.list, WM_MOUSELEAVE, 0, 0) };
    assert_eq!(unsafe { GetCapture() }, grid.list);
    assert_eq!(selected(grid.parent), vec![0, 1, 2]);
    assert!(marquee::rectangle(grid.list).is_some());
    selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON, 640, 240);
    assert!(
        selected(grid.parent).is_empty(),
        "shrinking must remove old hits"
    );
    selection_mouse(&grid, WM_LBUTTONUP, 0, -40, -40);
    assert_eq!(selected(grid.parent), vec![0, 1, 2]);
    assert!(unsafe { GetCapture() }.is_null());
    assert!(marquee::rectangle(grid.list).is_none());
}

#[test]
fn marquee_modifiers_and_escape_preserve_the_initial_selection() {
    use windows_sys::Win32::System::SystemServices::{MK_LBUTTON, MK_SHIFT};
    for modifier in [MK_CONTROL, MK_SHIFT] {
        let grid = Grid::new();
        grid.count(3);
        let item = LVITEMW {
            stateMask: LVIS_SELECTED,
            state: LVIS_SELECTED,
            ..Default::default()
        };
        unsafe { SendMessageW(grid.list, LVM_SETITEMSTATE, 0, &item as *const _ as isize) };
        selection_mouse(&grid, WM_LBUTTONDOWN, MK_LBUTTON | modifier, 650, 250);
        selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON | modifier, -40, -40);
        assert_eq!(
            selected(grid.parent),
            if modifier == MK_CONTROL {
                vec![1, 2]
            } else {
                vec![0, 1, 2]
            }
        );
        selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON | modifier, 640, 240);
        assert_eq!(selected(grid.parent), vec![0]);
        selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON | modifier, -40, -40);
        unsafe { SendMessageW(grid.list, WM_KEYDOWN, VK_ESCAPE as usize, 0) };
        assert_eq!(selected(grid.parent), vec![0]);
        assert!(unsafe { GetCapture() }.is_null());
        assert!(marquee::rectangle(grid.list).is_none());
    }
}

#[test]
fn marquee_cleans_up_after_capture_loss_or_content_change() {
    use windows_sys::Win32::System::SystemServices::MK_LBUTTON;
    let grid = Grid::new();
    grid.count(3);
    for change in [
        WM_CANCELMODE,
        LVM_SETITEMCOUNT,
        LVM_SETVIEW,
        WM_CAPTURECHANGED,
    ] {
        selection_mouse(&grid, WM_LBUTTONDOWN, MK_LBUTTON, 650, 250);
        selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON, -40, -40);
        assert!(marquee::rectangle(grid.list).is_some());
        unsafe {
            if change == WM_CAPTURECHANGED {
                SetCapture(grid.parent);
            } else {
                SendMessageW(
                    grid.list,
                    change,
                    if change == LVM_SETITEMCOUNT { 3 } else { 0 },
                    0,
                );
            }
        }
        assert_ne!(unsafe { GetCapture() }, grid.list);
        assert!(marquee::rectangle(grid.list).is_none());
        if change == WM_CAPTURECHANGED {
            assert_eq!(
                unsafe { GetCapture() },
                grid.parent,
                "do not release the new owner's capture"
            );
            unsafe { ReleaseCapture() };
        }
    }
}

#[test]
fn marquee_scroll_keeps_its_anchor_attached_to_content() {
    use windows_sys::Win32::System::SystemServices::MK_LBUTTON;
    let grid = Grid::new();
    let start = (0..grid.client().right)
        .find_map(|x| {
            for y in 0..images::cell(grid.parent).1 {
                let mut hit = LVHITTESTINFO {
                    pt: POINT { x, y },
                    ..Default::default()
                };
                if unsafe { SendMessageW(grid.list, LVM_HITTEST, 0, &mut hit as *mut _ as isize) }
                    < 0
                {
                    return Some(hit.pt);
                }
            }
            None
        })
        .expect("grid must have blank space between icons");
    selection_mouse(&grid, WM_LBUTTONDOWN, MK_LBUTTON, start.x, start.y);
    assert_eq!(unsafe { GetCapture() }, grid.list);
    selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON, 250, 400);
    let before = marquee::rectangle(grid.list).unwrap();
    let first = grid.item(0).top;
    unsafe { SendMessageW(grid.list, LVM_SCROLL, 0, 108) };
    let after = marquee::rectangle(grid.list).unwrap();
    assert_eq!(after.top - before.top, grid.item(0).top - first);
    assert_eq!(after.bottom, before.bottom);
    assert!(!selected(grid.parent).is_empty());
    selection_mouse(&grid, WM_LBUTTONUP, 0, 250, 400);
    assert!(unsafe { GetCapture() }.is_null());
}

#[test]
fn marquee_is_in_the_buffered_frame_and_leaves_no_trails() {
    use windows_sys::Win32::System::SystemServices::MK_LBUTTON;
    let grid = Grid::new();
    grid.count(3);
    unsafe {
        let screen = GetDC(null_mut());
        let dc = CreateCompatibleDC(screen);
        let bitmap = CreateCompatibleBitmap(screen, 700, 300);
        ReleaseDC(null_mut(), screen);
        let previous = SelectObject(dc, bitmap);
        selection_mouse(&grid, WM_LBUTTONDOWN, MK_LBUTTON, 650, 250);
        selection_mouse(&grid, WM_MOUSEMOVE, MK_LBUTTON, 50, 150);
        SendMessageW(grid.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        GdiFlush();
        let background = with_palette(grid.parent, |p| p.background);
        let fill = GetPixel(dc, 300, 200);
        assert_ne!(
            fill, background,
            "rectangle must have a visible translucent fill"
        );
        assert_ne!(
            GetPixel(dc, 50, 200),
            fill,
            "rectangle must have an outline"
        );
        assert_eq!(GetPixel(dc, 30, 200), background);
        // Optional local preview of only the affected panel, without showing a window.
        if let Some(path) = std::env::var_os("DESKTOPPLUS_MARQUEE_PREVIEW") {
            let mut info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: 700,
                    biHeight: -300,
                    biPlanes: 1,
                    biBitCount: 32,
                    ..Default::default()
                },
                ..Default::default()
            };
            SelectObject(dc, previous);
            let mut pixels = vec![0u8; 700 * 300 * 4];
            assert_ne!(
                GetDIBits(
                    dc,
                    bitmap,
                    0,
                    300,
                    pixels.as_mut_ptr().cast(),
                    &mut info,
                    DIB_RGB_COLORS
                ),
                0
            );
            let mut bmp = Vec::new();
            bmp.extend_from_slice(b"BM");
            bmp.extend_from_slice(&(54u32 + pixels.len() as u32).to_le_bytes());
            bmp.extend_from_slice(&[0u8; 4]);
            bmp.extend_from_slice(&54u32.to_le_bytes());
            bmp.extend_from_slice(std::slice::from_raw_parts(
                (&info.bmiHeader as *const BITMAPINFOHEADER).cast::<u8>(),
                40,
            ));
            bmp.extend_from_slice(&pixels);
            std::fs::write(path, bmp).unwrap();
            SelectObject(dc, bitmap);
        }
        selection_mouse(&grid, WM_LBUTTONUP, 0, 50, 150);
        SendMessageW(grid.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
        GdiFlush();
        assert_eq!(GetPixel(dc, 300, 200), background);
        assert_eq!(GetPixel(dc, 50, 200), background);
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}

#[test]
fn wheel_pace_and_precision_input_match_in_icon_and_details_views() {
    for view in [LV_VIEW_ICON, LV_VIEW_DETAILS] {
        let coarse = Grid::new();
        let fine = Grid::new();
        unsafe {
            SendMessageW(coarse.list, LVM_SETVIEW, view as usize, 0);
            SendMessageW(fine.list, LVM_SETVIEW, view as usize, 0);
        }
        let start = coarse.item(0).top;
        let unit = if view == LV_VIEW_DETAILS {
            coarse.item(0).bottom - start
        } else {
            1
        };
        let step = chrome::scale(coarse.list, 48).max(unit);
        let wheel = |list, delta: i16| unsafe {
            SendMessageW(list, WM_MOUSEWHEEL, (delta as u16 as usize) << 16, 0);
        };
        wheel(coarse.list, -120);
        assert_eq!(start - coarse.item(0).top, step / unit * unit);
        for _ in 0..120 {
            wheel(fine.list, -1);
        }
        assert_eq!(fine.item(0).top, coarse.item(0).top);
        wheel(coarse.list, -360);
        for _ in 0..360 {
            wheel(fine.list, -1);
        }
        assert_eq!(start - coarse.item(0).top, 4 * step / unit * unit);
        assert_eq!(fine.item(0).top, coarse.item(0).top);
        // Whole-row rounding must not delay a reversed wheel notch.
        let before_reverse = fine.item(0).top;
        wheel(fine.list, 120);
        assert_eq!(fine.item(0).top - before_reverse, step / unit * unit);
        wheel(fine.list, i16::MAX);
        assert_eq!(fine.item(0).top, start);
        wheel(fine.list, -120);
        assert_eq!(start - fine.item(0).top, step / unit * unit);
    }
}

#[test]
fn horizontal_wheel_uses_the_same_pace_and_ctrl_wheel_keeps_zoom() {
    let grid = Grid::new();
    unsafe {
        SendMessageW(grid.list, LVM_SETVIEW, LV_VIEW_DETAILS as usize, 0);
        let column = LVCOLUMNW {
            mask: LVCF_WIDTH,
            cx: 2000,
            ..std::mem::zeroed()
        };
        SendMessageW(
            grid.list,
            LVM_INSERTCOLUMNW,
            0,
            &column as *const _ as isize,
        );
        let step = chrome::scale(grid.list, 48);
        for _ in 0..120 {
            SendMessageW(grid.list, WM_MOUSEHWHEEL, 1 << 16, 0);
        }
        assert_eq!(GetScrollPos(grid.list, SB_HORZ), step);
        SendMessageW(
            grid.list,
            WM_MOUSEHWHEEL,
            (120u16.wrapping_neg() as usize) << 16,
            0,
        );
        assert_eq!(GetScrollPos(grid.list, SB_HORZ), 0);
        let before = grid.item(0).top;
        SendMessageW(
            grid.list,
            WM_MOUSEWHEEL,
            (120 << 16) | MK_CONTROL as usize,
            0,
        );
        assert_eq!(grid.item(0).top, before);
        assert!(EVENTS.with(|events| matches!(
            events.borrow_mut().pop_back(),
            Some(Event::Zoom(parent, 120)) if parent == grid.parent as usize
        )));
    }
}

#[test]
fn scrolling_commits_the_native_viewport_and_preserves_redraw_state() {
    let grid = Grid::new();
    let before = grid.item(0).top;
    unsafe {
        SendMessageW(grid.list, LVM_SCROLL, 0, 108);
        assert_eq!(grid.item(0).top, before - 108);
        assert!(!content::updating(grid.list));
        assert_ne!(
            GetWindowLongPtrW(grid.list, GWL_STYLE) as u32 & WS_VISIBLE,
            0
        );
        SendMessageW(grid.list, LVM_ENSUREVISIBLE, 40, 0);
        let item = grid.item(40);
        let client = grid.client();
        assert!(item.top >= 0 && item.bottom <= client.bottom);
        assert!(!content::updating(grid.list));
        assert_ne!(
            GetWindowLongPtrW(grid.list, GWL_STYLE) as u32 & WS_VISIBLE,
            0
        );
    }
}

#[test]
fn grid_presents_complete_frames_and_clears_old_columns() {
    let grid = Grid::new();
    let row = Row {
        path: PathBuf::from("resize-paint-fixture"),
        name: "Folder name".encode_utf16().collect(),
        kind: Vec::new(),
        directory: true,
        size: 0,
        modified: 0,
        created: 0,
        hidden: false,
        parent_navigation: false,
    };
    VIEWS.with(|views| {
        views.borrow_mut().insert(
            grid.parent as usize,
            ViewData {
                source: None,
                rows: Arc::new(vec![row.clone(); 14]),
                main: Vec::new(),
                is_main: false,
                workspace_page: OVERVIEW,
                photos: false,
                details: false,
                single_click: false,
                columns: [false; 10],
                collapsed: false,
                tab_count: 1,
                kind: "Folder".into(),
                loaded: true,
                searching: false,
                search_open: false,
                options: Default::default(),
                hover_tracking: false,
                zoom: 1.0,
            },
        )
    });
    images::configure(grid.parent);
    let spec = images::spec(grid.parent);
    unsafe {
        let green = vec![0xff00ff00u32; (spec.edge * spec.height) as usize];
        let icon = CreateBitmap(spec.edge, spec.height, 1, 32, green.as_ptr().cast());
        images::insert(spec, row.path, 0, Some(shell::Bitmap(icon as isize)), false);
        let screen = GetDC(null_mut());
        let dc = CreateCompatibleDC(screen);
        let bitmap = CreateCompatibleBitmap(screen, 1000, 500);
        ReleaseDC(null_mut(), screen);
        let previous = SelectObject(dc, bitmap);
        grid.count(14);
        let background = with_palette(grid.parent, |p| p.background);
        begin_resize(grid.parent);
        for width in [699, 743, 799, 800, 801, 743, 699] {
            grid.resize(width, 300);
            let client = grid.client();
            chrome::fill(dc, &client, 0xff00ff);
            SendMessageW(grid.list, WM_ERASEBKGND, dc as usize, 0);
            assert_eq!(
                GetPixel(dc, 0, 0),
                0xff00ff,
                "erasure must not expose an empty frame"
            );
            SendMessageW(grid.list, WM_PRINTCLIENT, dc as usize, PRF_CLIENT as isize);
            GdiFlush();
            let layout = preview(grid.parent).unwrap();
            let first = grid.item(0);
            for index in 0..14 {
                let x =
                    first.left + (index % layout.columns) as i32 * layout.width + layout.width / 2;
                let y = first.top
                    + (index / layout.columns) as i32 * layout.height
                    + chrome::content_scale(grid.parent, 5)
                    + spec.height / 2;
                if y < client.bottom {
                    assert_eq!(
                        GetPixel(dc, x, y),
                        0x00ff00,
                        "icon {index} missing at width {width}"
                    );
                }
            }
            // The trailing gutter and space below the last row used to keep
            // fragments of the previous arrangement during native erasure.
            for y in [1, 100, 220, 299] {
                assert_eq!(GetPixel(dc, client.right - 1, y), background);
            }
            for x in (0..client.right).step_by(13) {
                assert_eq!(GetPixel(dc, x, 299), background);
            }
        }
        end_resize(grid.parent);
        grid.assert_layout();
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
}

#[test]
fn native_grid_reflows_at_boundaries_and_keeps_selection() {
    let grid = Grid::new();
    let minimum = images::minimum_cell(grid.parent).0;
    let selected = LVITEMW {
        state: LVIS_SELECTED,
        stateMask: LVIS_SELECTED,
        ..Default::default()
    };
    unsafe {
        SendMessageW(
            grid.list,
            LVM_SETITEMSTATE,
            8,
            &selected as *const _ as isize,
        );
    }
    for width in (minimum * 5 - 2..minimum * 8 + 2).chain((minimum * 5 - 2..minimum * 8 + 2).rev())
    {
        grid.resize(width, 300);
        grid.assert_layout();
        assert_ne!(
            unsafe { SendMessageW(grid.list, LVM_GETITEMSTATE, 8, LVIS_SELECTED as isize) },
            0
        );
    }
}

#[test]
fn native_grid_redistributes_after_scrollbar_and_view_changes() {
    let grid = Grid::new();
    for count in [0, 1, 3, 12, 80, 14, 7, 80, 0] {
        grid.count(count);
        for height in [300, 600, 200, 800] {
            grid.resize(743, height);
            grid.assert_layout();
        }
    }
    unsafe {
        SendMessageW(grid.list, LVM_SETVIEW, LV_VIEW_DETAILS as usize, 0);
    }
    grid.resize(781, 300);
    grid.count(80);
    unsafe {
        SendMessageW(grid.list, LVM_SETVIEW, LV_VIEW_ICON as usize, 0);
    }
    grid.assert_layout();
}

#[test]
fn native_grid_centers_full_rows_with_and_without_a_scrollbar() {
    let grid = Grid::new();
    let minimum = images::minimum_cell(grid.parent);
    for count in [80, 14, 3, 80] {
        grid.count(count);
        for width in [150, 199, 600, 677, 743, 800, 801] {
            for height in [300, 800] {
                grid.resize(width, height);
                grid.assert_layout();
                let mut bounds = RECT::default();
                unsafe {
                    GetWindowRect(grid.list, &mut bounds);
                    MapWindowPoints(
                        null_mut(),
                        grid.parent,
                        (&mut bounds as *mut RECT).cast(),
                        2,
                    );
                }
                let geometry = geometry(grid.parent).unwrap();
                let left = bounds.left + grid.item(0).left;
                let right = left + geometry.columns as i32 * geometry.width;
                assert!(
                    (left + right - width).abs() <= 1,
                    "grid must be centered: {count} items, {width}x{height}, edges {left}..{right}"
                );
                assert_eq!(bounds.right, width, "keep the scrollbar inside the panel");
                assert!(geometry.width >= minimum.0);
            }
        }
    }
    unsafe { SendMessageW(grid.list, LVM_SETVIEW, LV_VIEW_DETAILS as usize, 0) };
    let mut bounds = RECT::default();
    unsafe {
        GetWindowRect(grid.list, &mut bounds);
        MapWindowPoints(
            null_mut(),
            grid.parent,
            (&mut bounds as *mut RECT).cast(),
            2,
        );
    }
    assert_eq!(bounds.left, 0, "details view restores its full width");
}

#[test]
fn native_grid_recovers_a_column_when_the_scrollbar_becomes_unnecessary() {
    let grid = Grid::new();
    let (minimum, height) = images::minimum_cell(grid.parent);
    grid.count(14);
    for width in [
        minimum * 7 - 1,
        minimum * 7,
        minimum * 7 + 1,
        minimum * 7 - 1,
    ] {
        grid.resize(width, height * 2 + 8);
        grid.assert_layout();
        if width >= minimum * 7 {
            assert_eq!(grid.item(6).top, grid.item(0).top);
            assert_eq!(
                unsafe { GetWindowLongPtrW(grid.list, GWL_STYLE) } as u32 & WS_VSCROLL,
                0
            );
        }
    }
}

#[test]
fn reflow_preserves_visible_and_hidden_list_state() {
    let grid = Grid::new();
    for visible in [true, false, true] {
        unsafe {
            ShowWindow(grid.list, if visible { SW_SHOWNA } else { SW_HIDE });
        }
        for width in [743, 795, 801] {
            grid.resize(width, 300);
            assert_eq!(
                unsafe { GetWindowLongPtrW(grid.list, GWL_STYLE) } as u32 & WS_VISIBLE != 0,
                visible,
            );
        }
    }
}

#[test]
fn live_resize_defers_native_remeasurement_and_commits_the_same_grid() {
    let grid = Grid::new();
    let original = images::cell(grid.parent);
    begin_resize(grid.parent);
    for width in [710, 743, 800, 837, 735, 798] {
        grid.resize(width, 300);
        let layout = preview(grid.parent).unwrap();
        assert!(layout.width >= images::minimum_cell(grid.parent).0);
        assert_eq!(
            images::cell(grid.parent),
            original,
            "do not remeasure labels during pointer movement"
        );
    }
    let expected = preview(grid.parent).unwrap();
    end_resize(grid.parent);
    assert!(preview(grid.parent).is_none());
    assert_eq!(images::cell(grid.parent), (expected.width, expected.height));
    grid.assert_layout();
    let first = grid.item(0);
    for index in 0..expected.columns * 2 {
        let item = grid.item(index);
        assert_eq!(
            item.left,
            first.left + (index % expected.columns) as i32 * expected.width
        );
        assert_eq!(
            item.top,
            first.top + (index / expected.columns) as i32 * expected.height
        );
    }
    begin_resize(grid.parent);
    forget(grid.parent);
    assert!(preview(grid.parent).is_none());
}
