//! A small reusable buffer pool avoids bitmap/DC allocation on every frame.
use super::*;

const MAX_PIXELS: usize = 8 * 1024 * 1024; // 32 MiB total cached storage.
struct Buffer {
    dc: HDC,
    bitmap: HBITMAP,
    original: HGDIOBJ,
    width: i32,
    height: i32,
    data: *mut u32,
}
impl Buffer {
    fn new(dc: HDC, width: i32, height: i32) -> Option<Self> {
        // Grow in blocks so a resizing window does not allocate at every pixel.
        let width = width.checked_add(63)? / 64 * 64;
        let height = height.checked_add(63)? / 64 * 64;
        unsafe {
            let memory = CreateCompatibleDC(dc);
            if memory.is_null() {
                return None;
            }
            let info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: width,
                    biHeight: -height,
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB,
                    ..Default::default()
                },
                ..Default::default()
            };
            let mut pixels = null_mut();
            let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut pixels, null_mut(), 0);
            if bitmap.is_null() {
                DeleteDC(memory);
                return None;
            }
            let original = SelectObject(memory, bitmap);
            Some(Self {
                dc: memory,
                bitmap,
                original,
                width,
                height,
                data: pixels.cast(),
            })
        }
    }
    fn pixels(&self) -> usize {
        self.width as usize * self.height as usize
    }
}
impl Drop for Buffer {
    fn drop(&mut self) {
        unsafe {
            SelectObject(self.dc, self.original);
            DeleteObject(self.bitmap);
            DeleteDC(self.dc);
        }
    }
}
thread_local! { static POOL: RefCell<Vec<Buffer>> = const { RefCell::new(Vec::new()) }; }

pub(super) fn paint(dc: HDC, bounds: RECT, glass: Option<glass::Paint>, draw: impl FnOnce(HDC)) {
    if dc.is_null() || bounds.right <= bounds.left || bounds.bottom <= bounds.top {
        return;
    }
    let mut clip = RECT::default();
    unsafe {
        match GetClipBox(dc, &mut clip) {
            NULLREGION => return,
            ERROR => clip = bounds,
            _ => {}
        }
    }
    let area = RECT {
        left: bounds.left.max(clip.left),
        top: bounds.top.max(clip.top),
        right: bounds.right.min(clip.right),
        bottom: bounds.bottom.min(clip.bottom),
    };
    let width = area.right - area.left;
    let height = area.bottom - area.top;
    if width <= 0 || height <= 0 {
        return;
    }
    let buffer = POOL
        .with(|pool| {
            let mut pool = pool.borrow_mut();
            let index = pool
                .iter()
                .enumerate()
                .filter(|(_, b)| b.width >= width && b.height >= height)
                .min_by_key(|(_, b)| b.pixels())
                .map(|(i, _)| i);
            index.map(|i| pool.swap_remove(i))
        })
        .or_else(|| Buffer::new(dc, width, height));
    let Some(buffer) = buffer else {
        draw(dc);
        return;
    };
    unsafe {
        let saved = SaveDC(buffer.dc);
        SetWindowOrgEx(buffer.dc, area.left, area.top, null_mut());
        IntersectClipRect(buffer.dc, area.left, area.top, area.right, area.bottom);
        draw(buffer.dc);
        if let Some(glass) = glass {
            GdiFlush();
            for y in 0..height as usize {
                let row = std::slice::from_raw_parts_mut(
                    buffer.data.add(y * buffer.width as usize),
                    width as usize,
                );
                for pixel in row {
                    *pixel = glass.pixel(*pixel);
                }
            }
        }
        BitBlt(
            dc, area.left, area.top, width, height, buffer.dc, area.left, area.top, SRCCOPY,
        );
        RestoreDC(buffer.dc, saved);
    }
    POOL.with(|pool| {
        let mut pool = pool.borrow_mut();
        if buffer.pixels() <= MAX_PIXELS {
            while !pool.is_empty()
                && (pool.len() >= 4
                    || pool.iter().map(Buffer::pixels).sum::<usize>() + buffer.pixels()
                        > MAX_PIXELS)
            {
                pool.remove(0);
            }
            pool.push(buffer);
        }
    });
}
