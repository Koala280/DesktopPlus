use super::*;

#[test]
fn returning_to_the_source_background_is_always_a_no_op() {
    // Include nested search results and pinned files from unrelated folders.
    let files = vec![
        PathBuf::from(r"C:\Panel\file.txt"),
        PathBuf::from(r"C:\Panel\Nested\result.txt"),
        PathBuf::from(r"D:\Elsewhere\pinned.txt"),
    ];
    assert_eq!(target(1, 1, None, &files), Target::Ignore);
    assert_eq!(target(1, 2, None, &files), Target::Background);
}

#[test]
fn folders_accept_drops_in_the_same_or_another_panel() {
    let files = vec![PathBuf::from(r"C:\Panel\file.txt")];
    let folder = PathBuf::from(r"C:\Panel\Destination");
    for destination in [1, 2] {
        assert_eq!(
            target(1, destination, Some(folder.clone()), &files),
            Target::Folder(folder.clone())
        );
    }
}

#[test]
fn selected_folders_and_their_descendants_are_not_drop_destinations() {
    let files = vec![
        PathBuf::from(r"C:\Panel\file.txt"),
        PathBuf::from(r"C:\Panel\Folder"),
    ];
    for destination in [1, 2] {
        for folder in [r"c:\panel\FOLDER", r"C:\Panel\Folder\Nested"] {
            assert_eq!(
                target(1, destination, Some(folder.into()), &files),
                Target::Ignore
            );
        }
    }
    // Similar names must not be mistaken for descendants.
    let sibling = PathBuf::from(r"C:\Panel\Folder Two");
    assert_eq!(
        target(1, 1, Some(sibling.clone()), &files),
        Target::Folder(sibling)
    );
}

// Exercise the actual native hit test with hidden controls only. In particular,
// screen coordinates must be mapped to the list, not to the panel's header.
struct PanelFixture {
    window: HWND,
    list: HWND,
}

impl PanelFixture {
    fn new(view: u32) -> Self {
        unsafe {
            InitCommonControlsEx(&INITCOMMONCONTROLSEX {
                dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
                dwICC: ICC_LISTVIEW_CLASSES,
            });
            let window = CreateWindowExW(
                0,
                wide("STATIC").as_ptr(),
                null(),
                WS_POPUP,
                120,
                80,
                500,
                400,
                null_mut(),
                null_mut(),
                null_mut(),
                null(),
            );
            assert!(!window.is_null());
            let list = CreateWindowExW(
                0,
                wide("SysListView32").as_ptr(),
                null(),
                WS_CHILD | WS_VISIBLE | LVS_AUTOARRANGE | view,
                20,
                70,
                440,
                280,
                window,
                LIST as usize as _,
                null_mut(),
                null(),
            );
            assert!(!list.is_null());
            let column = LVCOLUMNW {
                mask: LVCF_WIDTH,
                cx: 220,
                ..Default::default()
            };
            SendMessageW(list, LVM_INSERTCOLUMNW, 0, &column as *const _ as isize);
            let rows: Vec<_> = [("Folder", true), ("File.txt", false)]
                .into_iter()
                .enumerate()
                .map(|(index, (name, directory))| {
                    let mut label = wide(name);
                    let item = LVITEMW {
                        mask: LVIF_TEXT,
                        iItem: index as i32,
                        pszText: label.as_mut_ptr(),
                        ..Default::default()
                    };
                    assert_eq!(
                        SendMessageW(list, LVM_INSERTITEMW, 0, &item as *const _ as isize),
                        index as isize
                    );
                    Row {
                        path: PathBuf::from(r"C:\Panel").join(name),
                        name: name.encode_utf16().collect(),
                        kind: Vec::new(),
                        directory,
                        size: 0,
                        modified: 0,
                        created: 0,
                        hidden: false,
                        parent_navigation: false,
                    }
                })
                .collect();
            VIEWS.with(|views| {
                views.borrow_mut().insert(
                    window as usize,
                    ViewData {
                        source: None,
                        rows: Arc::new(rows),
                        main: Vec::new(),
                        is_main: false,
                        workspace_page: OVERVIEW,
                        photos: false,
                        details: view == LVS_REPORT,
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
                );
            });
            Self { window, list }
        }
    }

    fn label_point(&self, item: usize) -> POINT {
        let mut bounds = RECT {
            left: LVIR_LABEL as i32,
            ..Default::default()
        };
        unsafe {
            assert_ne!(
                SendMessageW(
                    self.list,
                    LVM_GETITEMRECT,
                    item,
                    &mut bounds as *mut _ as isize
                ),
                0
            );
        }
        let point = POINT {
            x: bounds.left + 10,
            y: (bounds.top + bounds.bottom) / 2,
        };
        let mut hit = LVHITTESTINFO {
            pt: point,
            ..Default::default()
        };
        assert_eq!(
            unsafe { SendMessageW(self.list, LVM_HITTEST, 0, &mut hit as *mut _ as isize) },
            item as isize,
            "label rect {},{}-{},{}",
            bounds.left,
            bounds.top,
            bounds.right,
            bounds.bottom
        );
        self.screen_point(point)
    }

    fn screen_point(&self, mut point: POINT) -> POINT {
        assert_ne!(unsafe { ClientToScreen(self.list, &mut point) }, 0);
        point
    }
}

impl Drop for PanelFixture {
    fn drop(&mut self) {
        VIEWS.with(|views| views.borrow_mut().remove(&(self.window as usize)));
        unsafe { DestroyWindow(self.window) };
    }
}

#[test]
fn native_hit_testing_only_accepts_folder_items_in_icons_and_details() {
    for view in [LVS_ICON, LVS_REPORT] {
        let panel = PanelFixture::new(view);
        assert_eq!(
            folder_at(panel.window, panel.label_point(0)),
            Some(PathBuf::from(r"C:\Panel\Folder"))
        );
        assert_eq!(folder_at(panel.window, panel.label_point(1)), None);
        for point in [POINT { x: 400, y: 250 }, POINT { x: 10, y: -30 }] {
            assert_eq!(folder_at(panel.window, panel.screen_point(point)), None);
        }
        VIEWS.with(|views| {
            views
                .borrow_mut()
                .get_mut(&(panel.window as usize))
                .unwrap()
                .kind = "List".into();
        });
        assert!(folder_at(panel.window, panel.label_point(0)).is_some());
        VIEWS.with(|views| {
            views
                .borrow_mut()
                .get_mut(&(panel.window as usize))
                .unwrap()
                .kind = "RecycleBin".into();
        });
        assert_eq!(folder_at(panel.window, panel.label_point(0)), None);
    }
}
