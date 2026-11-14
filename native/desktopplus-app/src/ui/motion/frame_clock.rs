//! Precise display-rate deadlines off the UI thread, with one outstanding frame.
use super::*;
use std::sync::{Condvar, Mutex};
use windows_sys::Win32::System::Threading::*;

const FRAME: u32 = WM_APP + 81;
const FALLBACK_TIMER: usize = 1;
struct Gate {
    requested: bool,
    pending: bool,
    stop: bool,
    interval: Duration,
}
impl Default for Gate {
    fn default() -> Self {
        Self {
            requested: false,
            pending: false,
            stop: false,
            interval: frame_interval(60),
        }
    }
}
struct Driver {
    hwnd: HWND,
    shared: Arc<(Mutex<Gate>, Condvar)>,
    worker: Option<std::thread::JoinHandle<()>>,
}
thread_local! { static DRIVER: RefCell<Option<Driver>> = const { RefCell::new(None) }; }
thread_local! {
    static RATES: RefCell<HashMap<usize, (Instant, Duration)>> = RefCell::new(HashMap::new());
}
fn frame_interval(hz: u32) -> Duration {
    Duration::from_secs_f64(1.0 / if (30..=360).contains(&hz) { hz } else { 60 } as f64)
}
fn display_interval(hwnd: HWND) -> Duration {
    let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY) };
    RATES.with(|rates| {
        let mut rates = rates.borrow_mut();
        let now = Instant::now();
        if let Some((checked, rate)) = rates.get(&(monitor as usize)) {
            if now.duration_since(*checked) < Duration::from_secs(2) {
                return *rate;
            }
        }
        let mut info = MONITORINFOEXW {
            monitorInfo: MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFOEXW>() as u32,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut mode = DEVMODEW {
            dmSize: std::mem::size_of::<DEVMODEW>() as u16,
            ..Default::default()
        };
        let rate = unsafe {
            if GetMonitorInfoW(monitor, &mut info.monitorInfo) != 0
                && EnumDisplaySettingsW(info.szDevice.as_ptr(), ENUM_CURRENT_SETTINGS, &mut mode)
                    != 0
            {
                frame_interval(mode.dmDisplayFrequency)
            } else {
                frame_interval(60)
            }
        };
        rates.insert(monitor as usize, (now, rate));
        rate
    })
}
/// Missed frames are skipped; rendering time never gets added to every period.
fn next_deadline(previous: Instant, now: Instant, interval: Duration) -> Instant {
    if previous > now {
        return previous;
    }
    let skipped = (now.duration_since(previous).as_nanos() / interval.as_nanos()) + 1;
    previous + interval.saturating_mul(skipped.min(u32::MAX as u128) as u32)
}
struct FrameTimer(HANDLE);
impl FrameTimer {
    fn new() -> Self {
        unsafe {
            let timer = CreateWaitableTimerExW(
                null(),
                null(),
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_ALL_ACCESS,
            );
            Self(if timer.is_null() {
                CreateWaitableTimerExW(null(), null(), 0, TIMER_ALL_ACCESS)
            } else {
                timer
            })
        }
    }
    fn wait(&self, deadline: Instant) {
        let delay = deadline.saturating_duration_since(Instant::now());
        if delay.is_zero() {
            return;
        }
        let due = -((delay.as_nanos() / 100).max(1).min(i64::MAX as u128) as i64);
        if !self.0.is_null() && unsafe { SetWaitableTimer(self.0, &due, 0, None, null(), 0) } != 0 {
            unsafe {
                WaitForSingleObject(self.0, 100);
            }
        } else {
            std::thread::sleep(delay);
        }
    }
}
impl Drop for FrameTimer {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}

impl Driver {
    fn new() -> Option<Self> {
        let hwnd = unsafe {
            let class = wide("DesktopPlus.Rust.AnimationClock");
            let instance = GetModuleHandleW(null());
            RegisterClassW(&WNDCLASSW {
                lpfnWndProc: Some(proc),
                hInstance: instance,
                lpszClassName: class.as_ptr(),
                ..Default::default()
            });
            CreateWindowExW(
                0,
                class.as_ptr(),
                null(),
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                null_mut(),
                instance,
                null(),
            )
        };
        if hwnd.is_null() {
            return None;
        }
        let shared = Arc::new((Mutex::new(Gate::default()), Condvar::new()));
        let gate = shared.clone();
        let target = hwnd as usize;
        let worker = std::thread::Builder::new()
            .name("animation-clock".into())
            .spawn(move || {
                let (lock, wake) = &*gate;
                let timer = FrameTimer::new();
                let mut deadline = Instant::now();
                loop {
                    let state = lock.lock().unwrap();
                    let state = wake
                        .wait_while(state, |s| !s.stop && (!s.requested || s.pending))
                        .unwrap();
                    if state.stop {
                        break;
                    }
                    let interval = state.interval;
                    drop(state);
                    deadline = next_deadline(deadline, Instant::now(), interval);
                    timer.wait(deadline);
                    deadline += interval;
                    let mut state = lock.lock().unwrap();
                    if state.stop {
                        break;
                    }
                    if state.requested && !state.pending {
                        state.pending = true;
                        state.requested = false;
                        if unsafe { PostMessageW(target as HWND, FRAME, 0, 0) } == 0 {
                            break;
                        }
                    }
                }
            })
            .ok();
        Some(Self {
            hwnd,
            shared,
            worker,
        })
    }
    fn request(&self, interval: Duration) {
        if self.worker.is_none() {
            unsafe {
                SetTimer(self.hwnd, FALLBACK_TIMER, 10, None);
            }
            return;
        }
        let (lock, wake) = &*self.shared;
        let mut state = lock.lock().unwrap();
        state.interval = if state.requested || state.pending {
            state.interval.min(interval)
        } else {
            interval
        };
        state.requested = true;
        wake.notify_one();
    }
}
pub(super) fn request(hwnd: HWND) -> bool {
    let interval = display_interval(hwnd);
    DRIVER.with(|driver| {
        let mut driver = driver.borrow_mut();
        if driver.is_none() {
            *driver = Driver::new();
        }
        if let Some(driver) = driver.as_ref() {
            driver.request(interval);
            true
        } else {
            false
        }
    })
}
pub(super) fn shutdown() {
    let driver = DRIVER.with(|d| d.borrow_mut().take());
    if let Some(mut driver) = driver {
        let (lock, wake) = &*driver.shared;
        lock.lock().unwrap().stop = true;
        wake.notify_one();
        if let Some(worker) = driver.worker.take() {
            let _ = worker.join();
        }
        unsafe {
            DestroyWindow(driver.hwnd);
        }
    }
}
unsafe extern "system" fn proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    if message == FRAME || (message == WM_TIMER && w == FALLBACK_TIMER) {
        super::tick(enabled());
        let active = active_count() != 0;
        DRIVER.with(|driver| {
            if let Some(driver) = driver.borrow().as_ref() {
                let (lock, wake) = &*driver.shared;
                let mut state = lock.lock().unwrap();
                state.pending = false;
                state.requested = active;
                if active {
                    wake.notify_one();
                } else {
                    unsafe {
                        KillTimer(hwnd, FALLBACK_TIMER);
                    }
                }
            }
        });
        return 0;
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clock_coalesces_requests_and_sleeps_after_the_last_frame() {
        assert!(request(null_mut()));
        let (hwnd, gate) = DRIVER.with(|d| {
            let d = d.borrow();
            let d = d.as_ref().unwrap();
            assert!(d.worker.is_some());
            (d.hwnd, d.shared.clone())
        });
        for _ in 0..100 {
            assert!(request(null_mut()));
        }
        let deadline = Instant::now() + Duration::from_secs(2);
        while !gate.0.lock().unwrap().pending && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(
            gate.0.lock().unwrap().pending,
            "compositor did not schedule a frame"
        );
        std::thread::sleep(Duration::from_millis(35));
        unsafe {
            let mut message = MSG::default();
            assert_ne!(PeekMessageW(&mut message, hwnd, FRAME, FRAME, PM_REMOVE), 0);
            let mut extra = MSG::default();
            assert_eq!(
                PeekMessageW(&mut extra, hwnd, FRAME, FRAME, PM_REMOVE),
                0,
                "frame messages accumulated while UI was busy"
            );
            DispatchMessageW(&message);
        }
        {
            let state = gate.0.lock().unwrap();
            assert!(
                !state.requested && !state.pending,
                "idle clock still requests frames"
            );
        }
        assert!(request(null_mut())); // It must also wake again after going idle.
        shutdown();
        assert!(gate.0.lock().unwrap().stop);
        assert_eq!(unsafe { IsWindow(hwnd) }, 0);
    }

    #[test]
    fn cadence_is_preserved_and_late_frames_are_skipped() {
        let start = Instant::now();
        let period = frame_interval(120);
        let next = start + period;
        assert_eq!(
            next_deadline(next, start + Duration::from_millis(3), period),
            next
        );
        assert_eq!(
            next_deadline(next, start + period * 3 + Duration::from_millis(1), period),
            start + period * 4
        );
        assert!(frame_interval(144) < frame_interval(60));
        assert_eq!(frame_interval(0), frame_interval(60));
    }
}
