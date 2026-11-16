use super::*;
pub use crate::shell::ImageSpec;
thread_local! { static POOLS: RefCell<HashMap<ImageSpec, Cache>> = RefCell::new(HashMap::new()); }
struct Cache {
    list: HIMAGELIST,
    entries: HashMap<(PathBuf, u64), (i32, u64)>,
    pending: HashMap<(PathBuf, u64), bool>,
    failed: std::collections::HashSet<(PathBuf, u64)>,
    clock: u64,
    capacity: usize,
}
impl Drop for Cache {
    fn drop(&mut self) {
        unsafe {
            ImageList_Destroy(self.list);
        }
    }
}
impl Cache {
    fn new(spec: ImageSpec) -> Self {
        let list =
            unsafe { ImageList_Create(spec.edge, spec.height, ILC_COLOR32 | ILC_MASK, 1, 8) };
        // GDI treats a bitmap whose alpha is entirely zero as a legacy opaque
        // image. An explicit monochrome mask keeps the unloaded slot transparent
        // while newly visible icons and thumbnails are fetched after scrolling.
        unsafe {
            let bitmap = CreateBitmap(
                spec.edge,
                spec.height,
                1,
                32,
                vec![0u8; (spec.edge * spec.height * 4) as usize]
                    .as_ptr()
                    .cast(),
            );
            // Monochrome bitmap scanlines are aligned to 16 bits; white mask
            // pixels preserve the destination, including selection backgrounds.
            let mask_bits = vec![0xffu8; (((spec.edge + 15) / 16) * 2 * spec.height) as usize];
            let mask = CreateBitmap(spec.edge, spec.height, 1, 1, mask_bits.as_ptr().cast());
            ImageList_Add(list, bitmap, mask);
            DeleteObject(mask);
            DeleteObject(bitmap);
        }
        Self {
            list,
            entries: HashMap::new(),
            pending: Default::default(),
            failed: Default::default(),
            clock: 0,
            // Small icons can retain several panels and previously visited
            // rows within the same 8 MiB budget used for larger thumbnails.
            capacity: ((8 * 1024 * 1024) / (spec.edge * spec.height * 4) as usize).clamp(8, 1024),
        }
    }
}
pub fn spec(hwnd: HWND) -> ImageSpec {
    let (details, photos) = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map(|v| (v.details, v.photos))
            .unwrap_or_default()
    });
    let edge = chrome::content_scale(
        hwnd,
        if details {
            18
        } else if photos {
            192
        } else {
            40
        },
    );
    ImageSpec {
        edge,
        height: if details {
            chrome::content_scale(hwnd, 36)
        } else {
            edge
        },
        thumbnail: photos,
    }
}
pub fn configure(hwnd: HWND) {
    let wanted = spec(hwnd);
    let list = POOLS.with(|pools| {
        pools
            .borrow_mut()
            .entry(wanted)
            .or_insert_with(|| Cache::new(wanted))
            .list
    });
    unsafe {
        let control = control(hwnd, LIST);
        SendMessageW(control, WM_SETFONT, chrome::font(hwnd, 5) as usize, 1);
        SendMessageW(control, LVM_SETIMAGELIST, LVSIL_SMALL as usize, list);
        SendMessageW(control, LVM_SETIMAGELIST, LVSIL_NORMAL as usize, list);
        InvalidateRect(control, null(), 1);
    }
    icon_grid::relayout(hwnd);
    let active = VIEWS.with(|views| {
        views
            .borrow()
            .iter()
            .filter(|(_, v)| !v.is_main)
            .map(|(h, _)| spec(*h as _))
            .collect::<std::collections::HashSet<_>>()
    });
    POOLS.with(|pools| pools.borrow_mut().retain(|key, _| active.contains(key)));
}
pub fn cell(hwnd: HWND) -> (i32, i32) {
    let list = control(hwnd, LIST);
    if !list.is_null() && unsafe { SendMessageW(list, LVM_GETVIEW, 0, 0) } == LV_VIEW_ICON as isize
    {
        let spacing = unsafe { SendMessageW(list, LVM_GETITEMSPACING, 0, 0) };
        let width = (spacing & 0xffff) as i32;
        let height = ((spacing >> 16) & 0xffff) as i32;
        if width > 0 && height > 0 {
            return (width, height);
        }
    }
    minimum_cell(hwnd)
}
pub fn minimum_cell(hwnd: HWND) -> (i32, i32) {
    let photos = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .is_some_and(|v| v.photos)
    });
    let line = with_palette(hwnd, |p| {
        (p.size * chrome::zoom(hwnd) * unsafe { GetDpiForWindow(hwnd) }.max(96) as f64 / 96.0 * 1.4)
            .ceil() as i32
    });
    (
        chrome::content_scale(hwnd, if photos { 216 } else { 100 }),
        chrome::content_scale(hwnd, if photos { 212 } else { 68 }) + line * 2,
    )
}
pub fn cached(hwnd: HWND, row: &Row) -> i32 {
    let wanted = spec(hwnd);
    POOLS.with(|pools| {
        pools
            .borrow()
            .get(&wanted)
            .and_then(|cache| cache.entries.get(&(row.path.clone(), row.modified)))
            .map_or(0, |(slot, _)| *slot)
    })
}

fn request(hwnd: HWND, row: &Row, visible: bool) -> i32 {
    let wanted = spec(hwnd);
    let key = (row.path.clone(), row.modified);
    POOLS.with(|pools| {
        let mut pools = pools.borrow_mut();
        let cache = pools.entry(wanted).or_insert_with(|| Cache::new(wanted));
        cache.clock += 1;
        if let Some((slot, used)) = cache.entries.get_mut(&key) {
            if visible {
                *used = cache.clock;
            }
            return *slot;
        }
        let enqueue = if cache.failed.contains(&key) {
            false
        } else if let Some(priority) = cache.pending.get_mut(&key) {
            let promote = visible && !*priority;
            *priority |= visible;
            promote
        } else if cache.pending.len() < if visible { 128 } else { 64 } {
            cache.pending.insert(key, visible);
            true
        } else {
            false
        };
        if enqueue {
            emit(Event::Image(
                hwnd as usize,
                wanted,
                row.path.clone(),
                row.modified,
                visible,
            ));
        }
        0
    })
}

pub fn prefetch(hwnd: HWND, rows: &[Row], visible: std::ops::Range<usize>) {
    let wanted = spec(hwnd);
    let count = POOLS.with(|pools| {
        pools.borrow().get(&wanted).map_or(0, |cache| {
            visible
                .len()
                .min(32)
                .min(cache.capacity / 4)
                .min(cache.capacity.saturating_sub(visible.len()) / 2)
        })
    });
    // Load a screen in either direction, without allowing speculative images
    // to displace the viewport or fill all pending slots during a fast scroll.
    let ahead = visible.end.min(rows.len())..visible.end.saturating_add(count).min(rows.len());
    let behind = visible.start.saturating_sub(count)..visible.start.min(rows.len());
    for index in ahead.chain(behind.rev()) {
        request(hwnd, &rows[index], false);
    }
}

pub fn prefetch_details(hwnd: HWND) {
    let list = control(hwnd, LIST);
    let start = unsafe { SendMessageW(list, LVM_GETTOPINDEX, 0, 0) }.max(0) as usize;
    let count = unsafe { SendMessageW(list, LVM_GETCOUNTPERPAGE, 0, 0) }.max(0) as usize + 1;
    let rows = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .map(|view| view.rows.clone())
    });
    if let Some(rows) = rows {
        prefetch(hwnd, &rows, start..start.saturating_add(count));
    }
}
pub fn insert(
    spec: ImageSpec,
    path: PathBuf,
    modified: u64,
    bitmap: Option<shell::Bitmap>,
    cancelled: bool,
) {
    POOLS.with(|pools| {
        let mut pools = pools.borrow_mut();
        let Some(cache) = pools.get_mut(&spec) else {
            return;
        };
        let key = (path, modified);
        cache.pending.remove(&key);
        let Some(bitmap) = bitmap else {
            if !cancelled && cache.failed.len() < 1024 {
                cache.failed.insert(key);
            }
            return;
        };
        cache.clock += 1;
        let slot = if cache.entries.len() < cache.capacity {
            unsafe { ImageList_Add(cache.list, bitmap.0 as _, null_mut()) }
        } else {
            let old = cache
                .entries
                .iter()
                .min_by_key(|(_, (_, used))| *used)
                .map(|(key, _)| key.clone())
                .unwrap();
            let (slot, _) = cache.entries.remove(&old).unwrap();
            unsafe {
                ImageList_Replace(cache.list, slot, bitmap.0 as _, null_mut());
            }
            slot
        };
        if slot >= 0 {
            cache.entries.insert(key, (slot, cache.clock));
        }
    });
}
pub fn counts() -> (usize, usize) {
    POOLS.with(|p| {
        p.borrow()
            .iter()
            .fold((0, 0), |(photos, icons), (spec, c)| {
                if spec.thumbnail {
                    (photos + c.entries.len(), icons)
                } else {
                    (photos, icons + c.entries.len())
                }
            })
    })
}
pub fn paint(hwnd: HWND, row: &Row, dc: HDC, x: i32, y: i32) {
    let index = request(hwnd, row, true);
    let spec = spec(hwnd);
    POOLS.with(|pools| {
        if let Some(cache) = pools.borrow().get(&spec) {
            unsafe {
                ImageList_Draw(cache.list, index, dc, x, y, ILD_TRANSPARENT);
            }
        }
    });
}

#[cfg(test)]
mod tests;
