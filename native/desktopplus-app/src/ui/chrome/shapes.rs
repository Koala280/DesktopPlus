//! Antialiased rounded controls on the existing, opaque GDI back buffer.
use super::*;
use std::sync::OnceLock;
use windows_sys::Win32::Graphics::GdiPlus::{self as gp, *};

// Keep GDI+ alive for the process. Shutting its worker down in a Windows TLS
// destructor can deadlock under the loader lock while other UI threads exit.
static RUNTIME: OnceLock<Option<usize>> = OnceLock::new();

struct Shapes {
    path: *mut GpPath,
    brush: *mut GpSolidFill,
    pen: *mut GpPen,
}
impl Shapes {
    fn new() -> Option<Self> {
        RUNTIME
            .get_or_init(|| unsafe {
                let mut token = 0;
                let input = GdiplusStartupInput {
                    GdiplusVersion: 1,
                    ..Default::default()
                };
                (GdiplusStartup(&mut token, &input, null_mut()) == gp::Ok).then_some(token)
            })
            .as_ref()?;
        let mut shapes = Self {
            path: null_mut(),
            brush: null_mut(),
            pen: null_mut(),
        };
        unsafe {
            if GdipCreatePath(FillModeWinding, &mut shapes.path) != gp::Ok
                || GdipCreateSolidFill(0, &mut shapes.brush) != gp::Ok
                || GdipCreatePen1(0, 1.0, UnitPixel, &mut shapes.pen) != gp::Ok
            {
                return None;
            }
        }
        Some(shapes)
    }
    fn fill(&self, graphics: *mut GpGraphics, rect: &RECT, diameter: f32, color: u32) -> bool {
        let x = rect.left as f32;
        let y = rect.top as f32;
        let width = (rect.right - rect.left) as f32;
        let height = (rect.bottom - rect.top) as f32;
        if width <= 0.0 || height <= 0.0 {
            return true;
        }
        let d = diameter.clamp(0.0, width.min(height));
        // COLORREF is BGR; GDI+ expects opaque ARGB.
        let argb = 0xff000000 | ((color & 0xff) << 16) | (color & 0xff00) | ((color >> 16) & 0xff);
        unsafe {
            if GdipResetPath(self.path) != gp::Ok
                || GdipSetSolidFillColor(self.brush, argb) != gp::Ok
            {
                return false;
            }
            if d <= 0.0 {
                if GdipAddPathRectangle(self.path, x, y, width, height) != gp::Ok {
                    return false;
                }
            } else {
                for (left, top, angle) in [
                    (x, y, 180.0),
                    (x + width - d, y, 270.0),
                    (x + width - d, y + height - d, 0.0),
                    (x, y + height - d, 90.0),
                ] {
                    if GdipAddPathArc(self.path, left, top, d, d, angle, 90.0) != gp::Ok {
                        return false;
                    }
                }
            }
            GdipClosePathFigure(self.path) == gp::Ok
                && GdipFillPath(graphics, self.brush.cast(), self.path) == gp::Ok
        }
    }
}
impl Drop for Shapes {
    fn drop(&mut self) {
        unsafe {
            if !self.brush.is_null() {
                GdipDeleteBrush(self.brush.cast());
            }
            if !self.path.is_null() {
                GdipDeletePath(self.path);
            }
            if !self.pen.is_null() {
                GdipDeletePen(self.pen);
            }
        }
    }
}

/// One fractional-position tab surface moves as a unit, including its outline.
pub(super) fn tab(dc: HDC, area: [f32; 4], radius: f32, background: u32, border: u32) -> bool {
    let [left, top, right, bottom] = area;
    if right - left < radius * 2.0 || bottom <= top {
        return true;
    }
    SHAPES.with(|shapes| {
        let Some(s) = shapes else {
            return false;
        };
        let mut graphics = null_mut();
        let argb = |color: u32| {
            0xff000000 | ((color & 0xff) << 16) | (color & 0xff00) | ((color >> 16) & 0xff)
        };
        unsafe {
            if GdipCreateFromHDC(dc, &mut graphics) != gp::Ok {
                return false;
            }
            let x = left + radius * 0.5;
            let r = right - radius * 0.5;
            let t = top + 0.5;
            let b = bottom + 1.0;
            let k = radius * 0.5522848;
            let ok = GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias8x8) == gp::Ok
                && GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf) == gp::Ok
                && GdipResetPath(s.path) == gp::Ok
                && GdipAddPathBezier(
                    s.path,
                    x - radius,
                    b,
                    x - radius + k,
                    b,
                    x,
                    b - radius + k,
                    x,
                    b - radius,
                ) == gp::Ok
                && GdipAddPathLine(s.path, x, b - radius, x, t + radius) == gp::Ok
                && GdipAddPathBezier(
                    s.path,
                    x,
                    t + radius,
                    x,
                    t + radius - k,
                    x + radius - k,
                    t,
                    x + radius,
                    t,
                ) == gp::Ok
                && GdipAddPathLine(s.path, x + radius, t, r - radius, t) == gp::Ok
                && GdipAddPathBezier(
                    s.path,
                    r - radius,
                    t,
                    r - radius + k,
                    t,
                    r,
                    t + radius - k,
                    r,
                    t + radius,
                ) == gp::Ok
                && GdipAddPathLine(s.path, r, t + radius, r, b - radius) == gp::Ok
                && GdipAddPathBezier(
                    s.path,
                    r,
                    b - radius,
                    r,
                    b - radius + k,
                    r + radius - k,
                    b,
                    r + radius,
                    b,
                ) == gp::Ok
                && GdipClosePathFigure(s.path) == gp::Ok
                && GdipSetSolidFillColor(s.brush, argb(background)) == gp::Ok
                && GdipSetPenColor(s.pen, argb(border)) == gp::Ok
                && GdipFillPath(graphics, s.brush.cast(), s.path) == gp::Ok
                && GdipDrawPath(graphics, s.pen, s.path) == gp::Ok;
            GdipDeleteGraphics(graphics);
            ok
        }
    })
}
thread_local! {
    static SHAPES: Option<Shapes> = Shapes::new();
}

pub(super) fn marquee(dc: HDC, area: &RECT, color: u32) -> bool {
    SHAPES.with(|shapes| {
        let Some(s) = shapes else { return false };
        let mut graphics = null_mut();
        let rgb = ((color & 0xff) << 16) | (color & 0xff00) | ((color >> 16) & 0xff);
        let width = (area.right - area.left) as f32;
        let height = (area.bottom - area.top) as f32;
        if width <= 0.0 || height <= 0.0 {
            return true;
        }
        unsafe {
            if GdipCreateFromHDC(dc, &mut graphics) != gp::Ok {
                return false;
            }
            let ok = GdipSetSolidFillColor(s.brush, 0x30000000 | rgb) == gp::Ok
                && GdipSetPenColor(s.pen, 0xff000000 | rgb) == gp::Ok
                && GdipFillRectangle(
                    graphics,
                    s.brush.cast(),
                    area.left as f32,
                    area.top as f32,
                    width,
                    height,
                ) == gp::Ok
                && GdipDrawRectangle(
                    graphics,
                    s.pen,
                    area.left as f32 + 0.5,
                    area.top as f32 + 0.5,
                    width - 1.0,
                    height - 1.0,
                ) == gp::Ok;
            GdipDeleteGraphics(graphics);
            ok
        }
    })
}

pub(super) fn rounded(dc: HDC, rect: &RECT, diameter: i32, background: u32, border: u32) -> bool {
    rounded_offset(dc, rect, diameter, background, border, (0.0, 0.0))
}
pub(super) fn rounded_offset(
    dc: HDC,
    rect: &RECT,
    diameter: i32,
    background: u32,
    border: u32,
    offset: (f32, f32),
) -> bool {
    SHAPES.with(|shapes| {
        let Some(shapes) = shapes else { return false };
        let mut graphics = null_mut();
        unsafe {
            if GdipCreateFromHDC(dc, &mut graphics) != gp::Ok {
                return false;
            }
            // Half-pixel sampling keeps straight edges on pixel boundaries,
            // while the 8x8 coverage smooths small switch thumbs and corners.
            let configured = GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias8x8) == gp::Ok
                && GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf) == gp::Ok
                && GdipTranslateWorldTransform(graphics, offset.0, offset.1, MatrixOrderPrepend)
                    == gp::Ok;
            let d = diameter
                .max(0)
                .min(rect.right - rect.left)
                .min(rect.bottom - rect.top) as f32;
            let mut painted = configured && shapes.fill(graphics, rect, d, border);
            if painted && background != border {
                // Two nested fills keep the entire one-pixel border inside the
                // control, with the same thickness on straight and curved edges.
                let inner = RECT {
                    left: rect.left + 1,
                    top: rect.top + 1,
                    right: rect.right - 1,
                    bottom: rect.bottom - 1,
                };
                painted = shapes.fill(graphics, &inner, (d - 2.0).max(0.0), background);
            }
            GdipDeleteGraphics(graphics);
            painted
        }
    })
}
