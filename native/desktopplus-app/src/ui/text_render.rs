//! DirectWrite layouts rendered into a reusable GDI-compatible surface.
//! The existing background is copied, so rounded tabs and selections survive.
use super::*;
use std::ffi::c_void;
use windows::{
    Win32::{Foundation::COLORREF, Graphics::DirectWrite::*},
    core::{BOOL, IUnknown, IUnknownImpl, Interface, PCWSTR, Ref, Result, implement},
};

#[implement(IDWriteTextRenderer)]
struct Renderer {
    target: IDWriteBitmapRenderTarget1,
    params: IDWriteRenderingParams,
}
impl IDWritePixelSnapping_Impl for Renderer_Impl {
    fn IsPixelSnappingDisabled(&self, _: *const c_void) -> Result<BOOL> {
        Ok(false.into())
    }
    fn GetCurrentTransform(&self, _: *const c_void, transform: *mut DWRITE_MATRIX) -> Result<()> {
        unsafe {
            *transform = DWRITE_MATRIX {
                m11: 1.0,
                m22: 1.0,
                ..Default::default()
            };
        }
        Ok(())
    }
    fn GetPixelsPerDip(&self, _: *const c_void) -> Result<f32> {
        Ok(1.0)
    }
}
impl Renderer_Impl {
    fn line(&self, color: *const c_void, x: f32, y: f32, width: f32, offset: f32, thickness: f32) {
        let rect = RECT {
            left: x.round() as i32,
            top: (y + offset).round() as i32,
            right: (x + width).round() as i32,
            bottom: (y + offset + thickness.max(1.0)).round() as i32,
        };
        chrome::fill(
            unsafe { self.target.GetMemoryDC().0 },
            &rect,
            color as usize as u32,
        );
    }
}
impl IDWriteTextRenderer_Impl for Renderer_Impl {
    fn DrawGlyphRun(
        &self,
        context: *const c_void,
        x: f32,
        y: f32,
        mode: DWRITE_MEASURING_MODE,
        glyphs: *const DWRITE_GLYPH_RUN,
        _: *const DWRITE_GLYPH_RUN_DESCRIPTION,
        _: Ref<IUnknown>,
    ) -> Result<()> {
        unsafe {
            self.target.DrawGlyphRun(
                x,
                y,
                mode,
                glyphs,
                &self.params,
                COLORREF(context as usize as u32),
                None,
            )
        }
    }
    fn DrawUnderline(
        &self,
        context: *const c_void,
        x: f32,
        y: f32,
        line: *const DWRITE_UNDERLINE,
        _: Ref<IUnknown>,
    ) -> Result<()> {
        let line = unsafe { &*line };
        self.line(context, x, y, line.width, line.offset, line.thickness);
        Ok(())
    }
    fn DrawStrikethrough(
        &self,
        context: *const c_void,
        x: f32,
        y: f32,
        line: *const DWRITE_STRIKETHROUGH,
        _: Ref<IUnknown>,
    ) -> Result<()> {
        let line = unsafe { &*line };
        self.line(context, x, y, line.width, line.offset, line.thickness);
        Ok(())
    }
    fn DrawInlineObject(
        &self,
        context: *const c_void,
        x: f32,
        y: f32,
        object: Ref<IDWriteInlineObject>,
        sideways: BOOL,
        rtl: BOOL,
        effect: Ref<IUnknown>,
    ) -> Result<()> {
        unsafe {
            object.ok()?.Draw(
                Some(context),
                &self.to_interface::<IDWriteTextRenderer>(),
                x,
                y,
                sideways.as_bool(),
                rtl.as_bool(),
                effect.as_ref(),
            )
        }
    }
}
#[derive(Clone, Hash, Eq, PartialEq)]
struct Key {
    family: Vec<u16>,
    pixels: i32,
    weight: i32,
    italic: u8,
    flags: u32,
    text: String,
}
struct TextEngine {
    factory: IDWriteFactory,
    target: IDWriteBitmapRenderTarget1,
    renderer: IDWriteTextRenderer,
    layouts: HashMap<Key, (IDWriteTextLayout, u64)>,
    sequence: u64,
}
impl TextEngine {
    fn new() -> Result<Self> {
        unsafe {
            let factory: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;
            let target: IDWriteBitmapRenderTarget1 = factory
                .GetGdiInterop()?
                .CreateBitmapRenderTarget(None, 1, 1)?
                .cast()?;
            // These are opaque, pixel-aligned surfaces. Keep Windows' ClearType
            // tuning instead of blurring small text in both axes with grayscale.
            target.SetTextAntialiasMode(DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE)?;
            target.SetPixelsPerDip(1.0)?;
            let params = factory.CreateRenderingParams()?;
            let renderer = Renderer {
                target: target.clone(),
                params,
            }
            .into();
            Ok(Self {
                factory,
                target,
                renderer,
                layouts: HashMap::new(),
                sequence: 0,
            })
        }
    }
    fn draw(
        &mut self,
        dc: HDC,
        value: &str,
        rect: RECT,
        font: HFONT,
        color: u32,
        flags: u32,
    ) -> Result<()> {
        unsafe {
            let mut lf = LOGFONTW::default();
            if GetObjectW(
                font,
                std::mem::size_of_val(&lf) as i32,
                (&mut lf as *mut LOGFONTW).cast(),
            ) == 0
            {
                return Err(windows::core::Error::from_thread());
            }
            let width = rect.right - rect.left;
            let height = rect.bottom - rect.top;
            let key = Key {
                family: lf.lfFaceName.to_vec(),
                pixels: lf.lfHeight.abs(),
                weight: lf.lfWeight,
                italic: lf.lfItalic,
                flags,
                text: value.into(),
            };
            self.sequence += 1;
            let layout = if let Some((layout, used)) = self.layouts.get_mut(&key) {
                *used = self.sequence;
                // Reflow the existing text as a panel grows. Width/height in
                // the cache key recreated formats, trimming signs and layouts
                // for every pixel, evicting other visible labels while dragging.
                if layout.GetMaxWidth() != width as f32 {
                    layout.SetMaxWidth(width as f32)?;
                }
                if layout.GetMaxHeight() != height as f32 {
                    layout.SetMaxHeight(height as f32)?;
                }
                layout.clone()
            } else {
                let format = self.factory.CreateTextFormat(
                    PCWSTR(key.family.as_ptr()),
                    None,
                    DWRITE_FONT_WEIGHT(key.weight),
                    if key.italic != 0 {
                        DWRITE_FONT_STYLE_ITALIC
                    } else {
                        DWRITE_FONT_STYLE_NORMAL
                    },
                    DWRITE_FONT_STRETCH_NORMAL,
                    key.pixels as f32,
                    windows::core::w!(""),
                )?;
                format.SetTextAlignment(if flags & DT_CENTER != 0 {
                    DWRITE_TEXT_ALIGNMENT_CENTER
                } else if flags & DT_RIGHT != 0 {
                    DWRITE_TEXT_ALIGNMENT_TRAILING
                } else {
                    DWRITE_TEXT_ALIGNMENT_LEADING
                })?;
                format.SetParagraphAlignment(if flags & DT_VCENTER != 0 {
                    DWRITE_PARAGRAPH_ALIGNMENT_CENTER
                } else {
                    DWRITE_PARAGRAPH_ALIGNMENT_NEAR
                })?;
                format.SetWordWrapping(if flags & DT_SINGLELINE != 0 {
                    DWRITE_WORD_WRAPPING_NO_WRAP
                } else {
                    DWRITE_WORD_WRAPPING_WRAP
                })?;
                if flags & DT_END_ELLIPSIS != 0 {
                    let sign = self.factory.CreateEllipsisTrimmingSign(&format)?;
                    format.SetTrimming(
                        &DWRITE_TRIMMING {
                            granularity: DWRITE_TRIMMING_GRANULARITY_CHARACTER,
                            ..Default::default()
                        },
                        &sign,
                    )?;
                }
                let layout = self.factory.CreateTextLayout(
                    &value.encode_utf16().collect::<Vec<_>>(),
                    &format,
                    width as f32,
                    height as f32,
                )?;
                if self.layouts.len() >= 256 {
                    if let Some(old) = self
                        .layouts
                        .iter()
                        .min_by_key(|(_, (_, used))| *used)
                        .map(|(key, _)| key.clone())
                    {
                        self.layouts.remove(&old);
                    }
                }
                self.layouts.insert(key, (layout.clone(), self.sequence));
                layout
            };
            let previous = self.target.GetSize()?;
            if previous.cx < width || previous.cy < height {
                self.target.Resize(
                    width.max(previous.cx) as u32,
                    height.max(previous.cy) as u32,
                )?;
            }
            let memory = self.target.GetMemoryDC().0;
            BitBlt(
                memory, 0, 0, width, height, dc, rect.left, rect.top, SRCCOPY,
            );
            layout.Draw(
                Some(color as usize as *const c_void),
                &self.renderer,
                0.0,
                0.0,
            )?;
            BitBlt(
                dc, rect.left, rect.top, width, height, memory, 0, 0, SRCCOPY,
            );
            Ok(())
        }
    }
}
thread_local! {static ENGINE: RefCell<Option<TextEngine>> = RefCell::new(TextEngine::new().ok());}

// Resolve the same installed face for DirectWrite and native edit controls.
// Windows 10 can fall back to Segoe UI without substituting an unrelated font.
pub(super) fn ui_family(family: &str, size: f64) -> &str {
    if !family.eq_ignore_ascii_case("Segoe UI")
        && !family.eq_ignore_ascii_case("Segoe UI Variable Text")
    {
        return family;
    }
    static FAMILIES: std::sync::OnceLock<(&str, &str)> = std::sync::OnceLock::new();
    let (text, display) = FAMILIES.get_or_init(|| {
        let available = || -> Result<(&'static str, &'static str)> {
            unsafe {
                let factory: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;
                let mut collection = None;
                factory.GetSystemFontCollection(&mut collection, false)?;
                let collection = collection.ok_or_else(windows::core::Error::from_thread)?;
                let find = |name: &'static str| -> Result<&'static str> {
                    let mut index = 0;
                    let mut exists = BOOL::default();
                    collection.FindFamilyName(
                        PCWSTR(wide(name).as_ptr()),
                        &mut index,
                        &mut exists,
                    )?;
                    Ok(if exists.as_bool() { name } else { "Segoe UI" })
                };
                Ok((
                    find("Segoe UI Variable Text")?,
                    find("Segoe UI Variable Display")?,
                ))
            }
        };
        available().unwrap_or(("Segoe UI", "Segoe UI"))
    });
    if size >= 24.0 { display } else { text }
}

pub fn draw(dc: HDC, value: &str, rect: RECT, font: HFONT, color: u32, flags: u32) -> bool {
    if value.is_empty() || rect.right <= rect.left || rect.bottom <= rect.top {
        return true;
    }
    ENGINE.with(|engine| {
        engine
            .borrow_mut()
            .as_mut()
            .is_some_and(|engine| engine.draw(dc, value, rect, font, color, flags).is_ok())
    })
}
pub fn available() -> bool {
    ENGINE.with(|engine| engine.borrow().is_some())
}

#[cfg(test)]
mod tests;
