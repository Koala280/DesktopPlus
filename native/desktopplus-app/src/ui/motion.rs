//! Shared, display-paced transitions. No animation work is scheduled at rest.
use super::*;
use std::{
    cell::Cell,
    time::{Duration, Instant},
};

mod frame_clock;
#[cfg(test)]
mod reveal_tests;
#[cfg(test)]
mod tests;

const COLOR_MS: u64 = 160;
const CONTROL_DURATION: Duration = Duration::from_millis(220);
const PANEL_DURATION: Duration = Duration::from_millis(300);

/// Finite Hermite motion: soft acceleration and deceleration with an exact end
/// time. Preserve velocity on interruption without an endless spring tail.
#[derive(Clone, Copy)]
struct Scalar {
    from: f64,
    to: f64,
    velocity: f64,
    started: Instant,
    duration: Duration,
}
impl Scalar {
    fn new(value: f64, now: Instant) -> Self {
        Self {
            from: value,
            to: value,
            velocity: 0.0,
            started: now,
            duration: CONTROL_DURATION,
        }
    }
    fn sample(self, now: Instant) -> (f64, f64) {
        let seconds = self.duration.as_secs_f64();
        let t = (now.saturating_duration_since(self.started).as_secs_f64() / seconds).min(1.0);
        if t >= 1.0 {
            return (self.to, 0.0);
        }
        let distance = self.to - self.from;
        let tangent = self.velocity * seconds;
        (
            self.from + distance * t * t * (3.0 - 2.0 * t) + tangent * t * (1.0 - t).powi(2),
            (distance * 6.0 * t * (1.0 - t) + tangent * (1.0 - 4.0 * t + 3.0 * t * t)) / seconds,
        )
    }
    fn retarget(&mut self, target: f64, now: Instant, animate: bool) {
        self.retarget_for(target, now, animate, CONTROL_DURATION);
    }
    fn retarget_for(&mut self, target: f64, now: Instant, animate: bool, duration: Duration) {
        if self.to == target {
            return;
        }
        let (position, velocity) = self.sample(now);
        *self = Self {
            from: if animate { position } else { target },
            to: target,
            velocity: if animate { velocity } else { 0.0 },
            started: now,
            duration,
        };
    }
    fn active(self) -> bool {
        self.from != self.to || self.velocity != 0.0
    }
    fn advance(&mut self, now: Instant, animate: bool) {
        if !animate || now.saturating_duration_since(self.started) >= self.duration {
            self.from = self.to;
            self.velocity = 0.0;
        }
    }
}
#[derive(Clone, Copy)]
enum Damage {
    Client,
    Area(RECT),
    Scrollbars,
}
#[derive(Default)]
struct Dirty {
    client: bool,
    area: Option<RECT>,
    scrollbars: bool,
}
impl Dirty {
    fn add(&mut self, damage: Damage) {
        match damage {
            Damage::Client => self.client = true,
            Damage::Scrollbars => self.scrollbars = true,
            Damage::Area(rect) => {
                self.area = Some(self.area.map_or(rect, |old| RECT {
                    left: old.left.min(rect.left),
                    top: old.top.min(rect.top),
                    right: old.right.max(rect.right),
                    bottom: old.bottom.max(rect.bottom),
                }))
            }
        }
    }
}
#[derive(Clone, Copy)]
struct Color {
    from: u32,
    to: u32,
    started: Instant,
    damage: Damage,
}
#[derive(Clone, Copy)]
struct Geometry {
    edges: [Scalar; 4],
    to: RECT,
    content_height: i32,
    collapsed: bool,
}
impl Geometry {
    fn rect(self, now: Instant) -> RECT {
        let [left, top, right, bottom] = self.edges.map(|edge| edge.sample(now).0.round() as i32);
        RECT {
            left,
            top,
            right,
            bottom,
        }
    }
}
#[derive(Default)]
struct State {
    colors: HashMap<u32, Color>,
    scalars: HashMap<u32, Scalar>,
    layout_scalars: HashMap<u32, Scalar>,
    geometry: Option<Geometry>,
    buffered_resize: bool,
}
impl State {
    fn active(&self) -> bool {
        self.geometry.is_some()
            || self.colors.values().any(|c| c.from != c.to)
            || self.scalars.values().any(|s| s.active())
            || self.layout_scalars.values().any(|s| s.active())
    }
}
thread_local! {
    static STATES: RefCell<HashMap<usize, State>> = RefCell::new(HashMap::new());
    static FRAME_TIME: Cell<Option<Instant>> = const { Cell::new(None) };
}
fn now() -> Instant {
    FRAME_TIME.with(|time| time.get().unwrap_or_else(Instant::now))
}
pub fn enabled() -> bool {
    let mut enabled = 1i32;
    unsafe {
        SystemParametersInfoW(
            SPI_GETCLIENTAREAANIMATION,
            0,
            (&mut enabled as *mut i32).cast(),
            0,
        );
    }
    enabled != 0
}
fn progress(started: Instant, now: Instant) -> f64 {
    let t =
        (now.saturating_duration_since(started).as_secs_f64() * 1000.0 / COLOR_MS as f64).min(1.0);
    t * t * (3.0 - 2.0 * t)
}
fn mix(from: u32, to: u32, t: f64) -> u32 {
    [0, 8, 16].into_iter().fold(0, |result, shift| {
        let a = ((from >> shift) & 255) as f64;
        let b = ((to >> shift) & 255) as f64;
        result | (((a + (b - a) * t).round() as u32) << shift)
    })
}
fn attach(hwnd: HWND) -> State {
    unsafe {
        SetWindowSubclass(hwnd, Some(proc), 16, 0);
    }
    State::default()
}
fn schedule(hwnd: HWND) {
    if !frame_clock::request(hwnd) {
        // Allocation/thread creation failure must still apply the final state.
        tick(false);
    }
}
pub fn color(hwnd: HWND, slot: u32, target: u32) -> u32 {
    color_with_damage(hwnd, slot, target, Damage::Client)
}
pub fn color_in(hwnd: HWND, slot: u32, target: u32, area: RECT) -> u32 {
    color_with_damage(hwnd, slot, target, Damage::Area(area))
}
pub fn scrollbar_color(hwnd: HWND, slot: u32, target: u32) -> u32 {
    color_with_damage(hwnd, slot, target, Damage::Scrollbars)
}
fn color_with_damage(hwnd: HWND, slot: u32, target: u32, damage: Damage) -> u32 {
    let now = now();
    let mut start = false;
    let result = STATES.with(|states| {
        let mut states = states.borrow_mut();
        let state = states.entry(hwnd as usize).or_insert_with(|| attach(hwnd));
        if state.colors.len() >= 512 && !state.colors.contains_key(&slot) {
            state.colors.retain(|_, c| c.from != c.to);
        }
        let color = state.colors.entry(slot).or_insert(Color {
            from: target,
            to: target,
            started: now,
            damage,
        });
        color.damage = damage;
        if color.to != target {
            let current = mix(color.from, color.to, progress(color.started, now));
            *color = Color {
                from: if enabled() { current } else { target },
                to: target,
                started: now,
                damage,
            };
            start = color.from != color.to;
        }
        mix(color.from, color.to, progress(color.started, now))
    });
    if start {
        schedule(hwnd);
    }
    result
}
pub fn scalar(hwnd: HWND, slot: u32, target: f64) -> f64 {
    scalar_with_layout(hwnd, slot, target, false)
}
pub fn layout_scalar(hwnd: HWND, slot: u32, target: f64) -> f64 {
    scalar_with_layout(hwnd, slot, target, true)
}
fn scalar_with_layout(hwnd: HWND, slot: u32, target: f64, layout: bool) -> f64 {
    let now = now();
    let mut start = false;
    let result = STATES.with(|states| {
        let mut states = states.borrow_mut();
        let state = states.entry(hwnd as usize).or_insert_with(|| attach(hwnd));
        let scalars = if layout {
            &mut state.layout_scalars
        } else {
            &mut state.scalars
        };
        let value = scalars
            .entry(slot)
            .or_insert_with(|| Scalar::new(target, now));
        if value.to != target {
            value.retarget(target, now, enabled());
            start = value.active();
        }
        value.sample(now).0
    });
    if start {
        schedule(hwnd);
    }
    result
}
pub fn destination(hwnd: HWND) -> Option<RECT> {
    STATES.with(|s| {
        s.borrow()
            .get(&(hwnd as usize))
            .and_then(|s| s.geometry.map(|g| g.to))
    })
}
pub fn content_height(hwnd: HWND) -> Option<i32> {
    STATES.with(|s| {
        s.borrow()
            .get(&(hwnd as usize))
            .and_then(|s| s.geometry.map(|g| g.content_height))
    })
}
pub fn resizing(hwnd: HWND) -> bool {
    destination(hwnd).is_some()
}
pub fn collapse_target(hwnd: HWND) -> Option<bool> {
    STATES.with(|s| {
        s.borrow()
            .get(&(hwnd as usize))
            .and_then(|s| s.geometry.map(|g| g.collapsed))
    })
}
pub fn active_count() -> usize {
    STATES.with(|s| s.borrow().values().filter(|s| s.active()).count())
}
fn begin_resize_buffer(hwnd: HWND) {
    // A redirected opaque surface keeps the client pixels and window bounds in
    // the same compositor update. Otherwise DWM can expose the next size while
    // the GDI client still has the previous height. Glass owns its own alpha.
    if glass::for_window(hwnd).is_some() {
        return;
    }
    unsafe {
        let style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        if style & WS_EX_LAYERED as isize != 0 {
            return;
        }
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED as isize);
        if SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA) == 0 {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style);
            return;
        }
    }
    STATES.with(|states| {
        if let Some(state) = states.borrow_mut().get_mut(&(hwnd as usize)) {
            state.buffered_resize = true;
        }
    });
}
fn end_resize_buffer(hwnd: HWND) {
    let owned = STATES.with(|states| {
        states
            .borrow_mut()
            .get_mut(&(hwnd as usize))
            .is_some_and(|state| std::mem::take(&mut state.buffered_resize))
    });
    if owned {
        unsafe {
            let style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style & !(WS_EX_LAYERED as isize));
        }
        paint_resize(hwnd);
    }
}
fn paint_resize(hwnd: HWND) {
    unsafe {
        // Children keep their final layout throughout the reveal. Explicitly
        // invalidate them too, including portions clipped on the last frame.
        RedrawWindow(
            hwnd,
            null(),
            null_mut(),
            RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOERASE | RDW_ALLCHILDREN,
        );
        GdiFlush();
    }
}
fn set_bounds(hwnd: HWND, rect: RECT) {
    unsafe {
        let mut current = RECT::default();
        GetWindowRect(hwnd, &mut current);
        if [current.left, current.top, current.right, current.bottom]
            == [rect.left, rect.top, rect.right, rect.bottom]
        {
            return;
        }
        SetWindowPos(
            hwnd,
            null_mut(),
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS,
        );
    }
}
fn finish(hwnd: HWND, collapsed: bool) {
    VIEWS.with(|views| {
        if let Some(view) = views.borrow_mut().get_mut(&(hwnd as usize)) {
            view.collapsed = collapsed;
        }
    });
    layout(hwnd);
    unsafe {
        RedrawWindow(hwnd, null(), null_mut(), RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
}
pub fn cancel_resize(hwnd: HWND) {
    let geometry = STATES.with(|s| {
        s.borrow_mut()
            .get_mut(&(hwnd as usize))
            .and_then(|s| s.geometry.take())
    });
    if let Some(g) = geometry {
        finish(hwnd, g.collapsed);
        set_bounds(hwnd, g.to);
        paint_resize(hwnd);
        end_resize_buffer(hwnd);
    }
}
pub fn resize(hwnd: HWND, to: RECT, collapsed: bool) {
    let mut from = RECT::default();
    unsafe {
        GetWindowRect(hwnd, &mut from);
    }
    if !enabled()
        || [from.left, from.top, from.right, from.bottom] == [to.left, to.top, to.right, to.bottom]
    {
        STATES.with(|s| {
            if let Some(s) = s.borrow_mut().get_mut(&(hwnd as usize)) {
                s.geometry = None;
            }
        });
        finish(hwnd, collapsed);
        set_bounds(hwnd, to);
        paint_resize(hwnd);
        end_resize_buffer(hwnd);
        return;
    }
    let now = now();
    STATES.with(|s| {
        let mut states = s.borrow_mut();
        let state = states.entry(hwnd as usize).or_insert_with(|| attach(hwnd));
        let mut edges = state.geometry.map_or_else(
            || [from.left, from.top, from.right, from.bottom].map(|v| Scalar::new(v as f64, now)),
            |g| g.edges,
        );
        for (edge, target) in edges.iter_mut().zip([to.left, to.top, to.right, to.bottom]) {
            edge.retarget_for(target as f64, now, true, PANEL_DURATION);
        }
        state.geometry = Some(Geometry {
            edges,
            to,
            collapsed,
            content_height: state
                .geometry
                .map_or(0, |g| g.content_height)
                .max(from.bottom - from.top)
                .max(to.bottom - to.top),
        });
    });
    begin_resize_buffer(hwnd);
    VIEWS.with(|views| {
        if let Some(view) = views.borrow_mut().get_mut(&(hwnd as usize)) {
            view.collapsed = false;
        }
    });
    layout(hwnd);
    paint_resize(hwnd);
    schedule(hwnd);
}
/// Clear row-indexed transitions when a virtual list changes data or view mode.
pub fn reset_colors(hwnd: HWND) {
    STATES.with(|s| {
        if let Some(state) = s.borrow_mut().get_mut(&(hwnd as usize)) {
            state.colors.clear();
        }
    });
}
fn tick(animate: bool) {
    let now = Instant::now();
    FRAME_TIME.with(|time| time.set(Some(now)));
    let updates = STATES.with(|states| {
        let mut states = states.borrow_mut();
        let mut updates = Vec::new();
        for (&hwnd, state) in states.iter_mut().filter(|(_, state)| state.active()) {
            let mut dirty = Dirty::default();
            for color in state.colors.values_mut().filter(|c| c.from != c.to) {
                dirty.add(color.damage);
                if !animate
                    || now.saturating_duration_since(color.started)
                        >= Duration::from_millis(COLOR_MS)
                {
                    color.from = color.to;
                }
            }
            for value in state.scalars.values_mut().filter(|s| s.active()) {
                dirty.add(Damage::Client);
                value.advance(now, animate);
            }
            let mut relayout = false;
            for value in state.layout_scalars.values_mut().filter(|s| s.active()) {
                relayout = true;
                value.advance(now, animate);
            }
            let geometry = state.geometry.as_mut().map(|g| {
                for edge in &mut g.edges {
                    edge.advance(now, animate);
                }
                let done = !g.edges.iter().any(|s| s.active());
                (g.rect(now), done.then_some(g.collapsed))
            });
            if geometry.is_some_and(|(_, done)| done.is_some()) {
                state.geometry = None;
            }
            updates.push((hwnd, dirty, geometry, relayout));
        }
        updates
    });
    // No RefCell borrows across window messages: painting can retarget motion.
    for (hwnd, dirty, geometry, relayout) in updates {
        let hwnd = hwnd as HWND;
        if unsafe { IsWindow(hwnd) } == 0 {
            continue;
        }
        if let Some((rect, done)) = geometry {
            if let Some(collapsed) = done {
                finish(hwnd, collapsed);
            }
            set_bounds(hwnd, rect);
        }
        if unsafe { IsWindowVisible(hwnd) } == 0 {
            if geometry.is_some_and(|(_, done)| done.is_some()) {
                end_resize_buffer(hwnd);
            }
            continue;
        }
        if relayout {
            layout_panel_header(hwnd);
            unsafe {
                let mut area = RECT::default();
                GetClientRect(hwnd, &mut area);
                area.bottom = chrome::scale(hwnd, chrome::header_height(hwnd));
                RedrawWindow(
                    hwnd,
                    &area,
                    null_mut(),
                    RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOERASE | RDW_ALLCHILDREN,
                );
            }
        }
        unsafe {
            if dirty.client || dirty.area.is_some() {
                let area = if dirty.client {
                    null()
                } else {
                    dirty.area.as_ref().unwrap() as *const RECT
                };
                RedrawWindow(
                    hwnd,
                    area,
                    null_mut(),
                    RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOERASE | RDW_NOCHILDREN,
                );
            }
            if geometry.is_some() {
                paint_resize(hwnd);
            }
        }
        if dirty.scrollbars {
            scrollbars::paint(hwnd);
        }
        if geometry.is_some_and(|(_, done)| done.is_some()) {
            end_resize_buffer(hwnd);
        }
    }
    unsafe {
        GdiFlush();
    }
    FRAME_TIME.with(|time| time.set(None));
}
pub fn shutdown() {
    frame_clock::shutdown();
}
unsafe extern "system" fn proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _: usize,
    _: usize,
) -> LRESULT {
    if message == WM_NCDESTROY {
        STATES.with(|s| {
            s.borrow_mut().remove(&(hwnd as usize));
        });
    }
    unsafe { DefSubclassProc(hwnd, message, w, l) }
}
