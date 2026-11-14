//! Keep foreground pixels opaque while the DWM supplies the window backdrop.
use super::*;
use crate::model::GlassMode;
use windows_sys::Win32::Graphics::Dwm::*;

#[derive(Clone, Copy)]
pub struct Paint {
    pub alpha: u8,
    pub backgrounds: [u32; 4],
}
thread_local! { static WINDOWS: RefCell<HashMap<usize, Paint>> = RefCell::new(HashMap::new()); }

/// Accent blur keeps the soft material visible on inactive desktop panels.
/// This optional Windows export is resolved dynamically; its accent policy is
/// undocumented, so a failed call falls back to the supported system backdrop.
fn accent_blur(hwnd: HWND, enabled: bool) -> bool {
    #[repr(C)]
    struct Accent {
        state: i32,
        flags: u32,
        color: u32,
        animation: u32,
    }
    #[repr(C)]
    struct Attribute {
        attribute: i32,
        data: *const Accent,
        size: usize,
    }
    type SetAttribute = unsafe extern "system" fn(HWND, *const Attribute) -> i32;
    unsafe {
        let user32 = GetModuleHandleW(wide("user32.dll").as_ptr());
        let Some(function) =
            GetProcAddress(user32, c"SetWindowCompositionAttribute".as_ptr().cast())
        else {
            return false;
        };
        let set: SetAttribute = std::mem::transmute(function);
        let accent = Accent {
            state: if enabled { 3 } else { 0 },
            flags: 0,
            color: 0,
            animation: 0,
        };
        set(
            hwnd,
            &Attribute {
                attribute: 19,
                data: &accent,
                size: std::mem::size_of::<Accent>(),
            },
        ) != 0
    }
}

pub fn apply(hwnd: HWND, theme: &Theme) {
    unsafe {
        let corners = if theme.rounded_corners {
            DWMWCP_ROUND
        } else {
            DWMWCP_DONOTROUND
        };
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE as _,
            (&corners as *const i32).cast(),
            4,
        );
        let accent_supported = accent_blur(hwnd, theme.glass_mode == GlassMode::Frosted);
        let mut soft = theme.glass_mode == GlassMode::Frosted && accent_supported;
        let backdrop = if theme.glass_mode == GlassMode::Acrylic
            || (theme.glass_mode == GlassMode::Frosted && !soft)
        {
            DWMSBT_TRANSIENTWINDOW
        } else {
            DWMSBT_NONE
        };
        let supported = DwmSetWindowAttribute(
            hwnd,
            DWMWA_SYSTEMBACKDROP_TYPE as _,
            (&backdrop as *const i32).cast(),
            4,
        ) >= 0;
        if !supported && theme.glass_mode == GlassMode::Acrylic {
            soft = accent_blur(hwnd, true);
        }
        let enabled = theme.glass_mode != GlassMode::Solid
            && (theme.glass_mode == GlassMode::Clear || supported || soft);
        let region = CreateRectRgn(0, 0, -1, -1);
        let blur = DWM_BLURBEHIND {
            dwFlags: DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable: i32::from(enabled),
            hRgnBlur: region,
            fTransitionOnMaximized: 0,
        };
        DwmEnableBlurBehindWindow(hwnd, &blur);
        DeleteObject(region);
        let margin = if enabled { -1 } else { 0 };
        DwmExtendFrameIntoClientArea(
            hwnd,
            &MARGINS {
                cxLeftWidth: margin,
                cxRightWidth: margin,
                cyTopHeight: margin,
                cyBottomHeight: margin,
            },
        );
        WINDOWS.with(|windows| {
            let mut windows = windows.borrow_mut();
            if enabled {
                let backgrounds =
                    with_palette(hwnd, |p| [p.background, p.surface, p.hover, p.selected]).map(
                        |color| ((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255),
                    );
                windows.insert(
                    hwnd as usize,
                    Paint {
                        alpha: ((theme.glass_opacity.clamp(10, 100) as u32 * 255 + 50) / 100) as u8,
                        backgrounds,
                    },
                );
            } else {
                windows.remove(&(hwnd as usize));
            }
        });
        RedrawWindow(
            hwnd,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME,
        );
    }
}
pub fn release(hwnd: HWND) {
    WINDOWS.with(|windows| windows.borrow_mut().remove(&(hwnd as usize)));
}
pub fn for_window(hwnd: HWND) -> Option<Paint> {
    let root = unsafe { GetAncestor(hwnd, GA_ROOT) };
    WINDOWS.with(|windows| windows.borrow().get(&(root as usize)).copied())
}
impl Paint {
    /// GDI clears alpha. Restore premultiplied alpha only near surface colors;
    /// text, icons, focus rings and selections retain their full contrast.
    pub fn pixel(self, pixel: u32) -> u32 {
        let rgb = pixel & 0x00ff_ffff;
        if self.alpha == 255 {
            return rgb | 0xff00_0000;
        }
        let distance = if self.backgrounds.contains(&rgb) {
            0
        } else {
            self.backgrounds
                .iter()
                .map(|color| {
                    let bgr = *color;
                    [0, 8, 16]
                        .into_iter()
                        .map(|shift| {
                            (((rgb >> shift) & 255) as i32 - ((bgr >> shift) & 255) as i32)
                                .unsigned_abs()
                        })
                        .max()
                        .unwrap_or(255)
                })
                .min()
                .unwrap_or(255)
        };
        let alpha = u32::from(self.alpha) + (255 - u32::from(self.alpha)) * distance.min(40) / 40;
        let channel = |shift: u32| (((rgb >> shift) & 255) * alpha + 127) / 255;
        (alpha << 24) | (channel(16) << 16) | (channel(8) << 8) | channel(0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn glass_preserves_foreground_and_premultiplies_surface() {
        let paint = Paint {
            alpha: 128,
            backgrounds: [0x101820; 4],
        };
        assert_eq!(paint.pixel(0x101820), 0x80080c10);
        assert_eq!(paint.pixel(0xeeeeee), 0xffeeeeee);
        assert_eq!(
            Paint {
                alpha: 255,
                ..paint
            }
            .pixel(0x101820),
            0xff101820
        );
    }
}
