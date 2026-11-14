use super::*;

fn row(index: usize) -> Row {
    Row {
        path: PathBuf::from(format!("prefetch-{index}")),
        name: Vec::new(),
        kind: Vec::new(),
        directory: false,
        size: 0,
        modified: 0,
        created: 0,
        hidden: false,
        parent_navigation: false,
    }
}

#[test]
fn layout_does_not_enqueue_images_and_prefetch_stays_near_the_viewport() {
    let hwnd = null_mut();
    POOLS.with(|pools| pools.borrow_mut().clear());
    EVENTS.with(|events| events.borrow_mut().clear());
    let rows: Vec<_> = (0..1000).map(row).collect();
    for row in &rows {
        assert_eq!(cached(hwnd, row), 0);
    }
    assert!(EVENTS.with(|events| events.borrow().is_empty()));
    for row in &rows[100..118] {
        request(hwnd, row, true);
    }
    prefetch(hwnd, &rows, 100..118);
    EVENTS.with(|events| {
        let events = events.borrow();
        let visible = events
            .iter()
            .filter(|event| matches!(event, Event::Image(_, _, _, _, true)))
            .count();
        assert_eq!(visible, 18);
        assert_eq!(events.len(), 54);
        for event in events.iter() {
            let Event::Image(_, _, path, _, _) = event else {
                panic!("Unexpected event")
            };
            assert!(rows[82..136].iter().any(|row| &row.path == path));
        }
    });
    EVENTS.with(|events| events.borrow_mut().clear());
    // The next scroll promotes its outstanding preload once. Repaints neither
    // duplicate the request nor let it stay behind the speculative queue.
    request(hwnd, &rows[118], true);
    request(hwnd, &rows[118], true);
    EVENTS.with(|events| {
        let events = events.borrow();
        assert_eq!(events.len(), 1);
        assert!(matches!(
            events.front(),
            Some(Event::Image(_, _, _, _, true))
        ));
    });
}

#[test]
fn icon_cache_retains_long_scrolled_lists_without_expanding_the_pixel_budget() {
    let hwnd = null_mut();
    let wanted = spec(hwnd);
    POOLS.with(|pools| {
        let mut pools = pools.borrow_mut();
        pools.clear();
        pools.insert(wanted, Cache::new(wanted));
        let capacity = pools[&wanted].capacity;
        assert!(capacity >= 256);
        assert!(capacity * (wanted.edge * wanted.height * 4) as usize <= 8 * 1024 * 1024);
    });
    for index in 0..256 {
        let pixels = vec![0xff00ff00u32; (wanted.edge * wanted.height) as usize];
        let bitmap =
            unsafe { CreateBitmap(wanted.edge, wanted.height, 1, 32, pixels.as_ptr().cast()) };
        insert(
            wanted,
            row(index).path,
            0,
            Some(shell::Bitmap(bitmap as isize)),
            false,
        );
    }
    for index in 0..256 {
        assert_ne!(
            cached(hwnd, &row(index)),
            0,
            "scrolling back must reuse icon {index}"
        );
    }
    let photo = ImageSpec {
        edge: 192,
        height: 192,
        thumbnail: true,
    };
    assert!(
        Cache::new(photo).capacity * (photo.edge * photo.height * 4) as usize <= 8 * 1024 * 1024
    );
}

#[test]
fn prefetch_does_not_evict_a_full_thumbnail_viewport() {
    let hwnd = null_mut();
    let wanted = spec(hwnd);
    POOLS.with(|pools| {
        let mut cache = Cache::new(wanted);
        cache.capacity = 8;
        pools.borrow_mut().insert(wanted, cache);
    });
    EVENTS.with(|events| events.borrow_mut().clear());
    let rows: Vec<_> = (0..100).map(row).collect();
    prefetch(hwnd, &rows, 20..28);
    assert!(EVENTS.with(|events| events.borrow().is_empty()));
}

struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    previous: HGDIOBJ,
    spec: ImageSpec,
}

impl Surface {
    fn new(spec: ImageSpec) -> Self {
        unsafe {
            let dc = CreateCompatibleDC(null_mut());
            assert!(!dc.is_null());
            let info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: spec.edge,
                    biHeight: -spec.height,
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB,
                    ..Default::default()
                },
                ..Default::default()
            };
            let mut pixels = null_mut();
            let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut pixels, null_mut(), 0);
            assert!(!bitmap.is_null());
            let previous = SelectObject(dc, bitmap);
            Self {
                dc,
                bitmap,
                previous,
                spec,
            }
        }
    }

    fn fill(&self, color: u32) {
        chrome::fill(
            self.dc,
            &RECT {
                left: 0,
                top: 0,
                right: self.spec.edge,
                bottom: self.spec.height,
            },
            color,
        );
    }

    fn draw(&self, list: HIMAGELIST, index: i32) {
        unsafe {
            assert_ne!(
                ImageList_Draw(list, index, self.dc, 0, 0, ILD_TRANSPARENT),
                0
            );
            GdiFlush();
        }
    }
}

impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            SelectObject(self.dc, self.previous);
            DeleteObject(self.bitmap);
            DeleteDC(self.dc);
        }
    }
}

#[test]
fn unloaded_images_preserve_the_background_in_every_view() {
    // Exercise the actual common-control rendering, without opening windows.
    // All-zero alpha alone is treated as an opaque legacy bitmap by GDI.
    for (edge, height, thumbnail) in [
        (18, 30, false),
        (40, 40, false),
        (48, 48, false),
        (192, 192, true),
    ] {
        let spec = ImageSpec {
            edge,
            height,
            thumbnail,
        };
        let cache = Cache::new(spec);
        let surface = Surface::new(spec);
        for background in [0x202020, 0xfafafa, 0x123456] {
            surface.fill(background);
            surface.draw(cache.list, 0);
            for y in 0..height {
                for x in 0..edge {
                    assert_eq!(
                        unsafe { GetPixel(surface.dc, x, y) },
                        background,
                        "unloaded {edge}x{height} image overwrote the background at ({x}, {y})"
                    );
                }
            }
        }
    }
}

#[test]
fn loaded_and_replaced_images_keep_their_alpha_and_placeholder() {
    let spec = ImageSpec {
        edge: 48,
        height: 48,
        thumbnail: false,
    };
    let cache = Cache::new(spec);
    let icon = Surface::new(spec);
    let surface = Surface::new(spec);
    unsafe {
        let mut bits = BITMAP::default();
        assert_ne!(
            GetObjectW(
                icon.bitmap,
                std::mem::size_of::<BITMAP>() as i32,
                (&mut bits as *mut BITMAP).cast(),
            ),
            0
        );
        // Transparent padding and an opaque center match shell icon bitmaps.
        let pixels = std::slice::from_raw_parts_mut(bits.bmBits.cast::<u32>(), 48 * 48);
        pixels.fill(0);
        pixels[24 * 48 + 24] = 0xff00ff00;
        SelectObject(icon.dc, icon.previous);
        let index = ImageList_Add(cache.list, icon.bitmap, null_mut());
        assert_eq!(index, 1);
        for replace in [false, true] {
            if replace {
                assert_ne!(
                    ImageList_Replace(cache.list, index, icon.bitmap, null_mut()),
                    0
                );
            }
            surface.fill(0);
            surface.draw(cache.list, index);
            assert_eq!(GetPixel(surface.dc, 24, 24), 0x00ff00);
            surface.fill(0x123456);
            surface.draw(cache.list, index);
            assert_eq!(GetPixel(surface.dc, 0, 0), 0x123456);
            assert_eq!(GetPixel(surface.dc, 47, 47), 0x123456);
            surface.fill(0x123456);
            surface.draw(cache.list, 0);
            assert_eq!(GetPixel(surface.dc, 24, 24), 0x123456);
        }
    }
}

#[test]
fn cached_icons_paint_in_the_partial_buffer_exposed_by_scrolling() {
    let spec = ImageSpec {
        edge: 48,
        height: 48,
        thumbnail: false,
    };
    let cache = Cache::new(spec);
    let pixels = vec![0xff00ff00u32; 48 * 48];
    let bitmap =
        shell::Bitmap(unsafe { CreateBitmap(48, 48, 1, 32, pixels.as_ptr().cast()) } as isize);
    let index = unsafe { ImageList_Add(cache.list, bitmap.0 as _, null_mut()) };
    let surface = Surface::new(ImageSpec {
        height: 144,
        ..spec
    });
    surface.fill(0x123456);
    unsafe {
        IntersectClipRect(surface.dc, 0, 96, 48, 144);
    }
    chrome::buffered(
        surface.dc,
        RECT {
            left: 0,
            top: 0,
            right: 48,
            bottom: 144,
        },
        |dc| unsafe {
            assert_ne!(
                ImageList_Draw(cache.list, index, dc, 0, 96, ILD_TRANSPARENT),
                0
            );
        },
    );
    unsafe {
        GdiFlush();
        assert_eq!(GetPixel(surface.dc, 24, 120), 0x00ff00);
    }
}
