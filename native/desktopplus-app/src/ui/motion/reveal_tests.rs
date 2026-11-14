use super::*;

struct Window(HWND);
impl Window {
    fn new(layered: bool) -> Self {
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | if layered { WS_EX_LAYERED } else { 0 },
                wide("STATIC").as_ptr(),
                null(),
                WS_POPUP,
                100,
                100,
                440,
                48,
                null_mut(),
                null_mut(),
                GetModuleHandleW(null()),
                null(),
            )
        };
        assert!(!hwnd.is_null());
        Self(hwnd)
    }
    fn style(&self) -> isize {
        unsafe { GetWindowLongPtrW(self.0, GWL_EXSTYLE) }
    }
    fn bounds(&self) -> RECT {
        let mut rect = RECT::default();
        unsafe { GetWindowRect(self.0, &mut rect) };
        rect
    }
    fn expand(&self) {
        let mut rect = self.bounds();
        rect.bottom = rect.top + 460;
        resize(self.0, rect, false);
    }
}
impl Drop for Window {
    fn drop(&mut self) {
        glass::release(self.0);
        unsafe { DestroyWindow(self.0) };
        shutdown();
    }
}

#[test]
fn resize_buffer_is_released_after_completion_cancellation_and_immediate_retarget() {
    for ending in 0..3 {
        let window = Window::new(false);
        let original_style = window.style();
        window.expand();
        if enabled() {
            assert_ne!(window.style() & WS_EX_LAYERED as isize, 0);
            let mut reversed = window.bounds();
            reversed.bottom = reversed.top + 300;
            resize(window.0, reversed, false);
            assert_ne!(window.style() & WS_EX_LAYERED as isize, 0);
        }
        match ending {
            0 => tick(false),
            1 => cancel_resize(window.0),
            _ => resize(window.0, window.bounds(), false),
        }
        assert!(!resizing(window.0));
        assert_eq!(window.style(), original_style);
        assert!(!STATES.with(|states| {
            states
                .borrow()
                .get(&(window.0 as usize))
                .is_some_and(|state| state.buffered_resize)
        }));
    }
}

#[test]
fn resize_preserves_an_existing_layered_surface_and_its_opacity() {
    let window = Window::new(true);
    unsafe { assert_ne!(SetLayeredWindowAttributes(window.0, 0, 173, LWA_ALPHA), 0) };
    let original_style = window.style();
    window.expand();
    cancel_resize(window.0);
    let mut alpha = 0;
    let mut flags = 0;
    unsafe {
        assert_ne!(
            GetLayeredWindowAttributes(window.0, null_mut(), &mut alpha, &mut flags),
            0
        );
    }
    assert_eq!(window.style(), original_style);
    assert_eq!((alpha, flags), (173, LWA_ALPHA));
}

#[test]
fn glass_keeps_its_per_pixel_alpha_during_resize() {
    let window = Window::new(false);
    glass::apply(
        window.0,
        &Theme {
            glass_mode: crate::model::GlassMode::Clear,
            ..Default::default()
        },
    );
    assert!(glass::for_window(window.0).is_some());
    let original_style = window.style();
    window.expand();
    assert_eq!(window.style(), original_style);
    tick(false);
    assert!(glass::for_window(window.0).is_some());
    assert_eq!(window.style(), original_style);
}
