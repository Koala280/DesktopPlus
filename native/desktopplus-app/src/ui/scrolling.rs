//! A shared, DPI-independent wheel pace for forms and native lists.
use super::*;

pub(super) const WHEEL_STEP: i32 = 48;

#[derive(Default)]
pub(super) struct Wheel {
    remainder: i64,
    scale: (i32, i32),
}

impl Wheel {
    /// Return scroll units, retaining partial pixels/rows from precision input.
    /// Native details lists can only move by whole rows; never round every event
    /// up to a row, which makes touchpads much faster than ordinary wheels.
    pub(super) fn movement(&mut self, delta: i32, step: i32, unit: i32) -> i32 {
        if delta == 0 {
            return 0;
        }
        let unit = unit.max(1);
        let step = step.max(unit);
        if self.scale != (step, unit) || self.remainder.signum() != i64::from(delta.signum()) {
            self.remainder = 0;
        }
        self.scale = (step, unit);
        let movement = self.remainder + i64::from(delta) * i64::from(step);
        let denominator = i64::from(WHEEL_DELTA) * i64::from(unit);
        self.remainder = movement % denominator;
        (movement / denominator) as i32
    }
}

thread_local! {
    static WHEELS: RefCell<HashMap<usize, [Wheel; 2]>> = RefCell::new(HashMap::new());
}

pub(super) fn forget(hwnd: HWND) {
    WHEELS.with(|wheels| wheels.borrow_mut().remove(&(hwnd as usize)));
}

pub(super) fn list(hwnd: HWND, message: u32, w: WPARAM) {
    let vertical = message == WM_MOUSEWHEEL;
    let delta = (w >> 16) as i16 as i32 * if vertical { -1 } else { 1 };
    unsafe {
        let mut info = SCROLLINFO {
            cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
            fMask: SIF_ALL,
            ..std::mem::zeroed()
        };
        GetScrollInfo(hwnd, if vertical { SB_VERT } else { SB_HORZ }, &mut info);
        let max = (info.nMax - info.nPage.saturating_sub(1) as i32).max(info.nMin);
        let unit = if vertical && SendMessageW(hwnd, LVM_GETVIEW, 0, 0) == LV_VIEW_DETAILS as isize
        {
            let mut rect = RECT {
                left: LVIR_BOUNDS as i32,
                ..Default::default()
            };
            if SendMessageW(hwnd, LVM_GETITEMRECT, 0, &mut rect as *mut RECT as isize) != 0 {
                (rect.bottom - rect.top).max(1)
            } else {
                chrome::scale(hwnd, 30)
            }
        } else {
            1
        };
        let movement = WHEELS.with(|wheels| {
            let mut wheels = wheels.borrow_mut();
            let wheel = &mut wheels.entry(hwnd as usize).or_default()[usize::from(!vertical)];
            let movement = wheel.movement(delta, chrome::scale(hwnd, WHEEL_STEP), unit);
            let target = info.nPos.saturating_add(movement).clamp(info.nMin, max);
            // Do not carry fractions into the next gesture at either edge.
            if (delta < 0 && target == info.nMin) || (delta > 0 && target == max) {
                *wheel = Wheel::default();
            }
            target - info.nPos
        });
        if movement != 0 {
            SendMessageW(
                hwnd,
                LVM_SCROLL,
                if vertical { 0 } else { movement as usize },
                if vertical {
                    (movement * unit) as isize
                } else {
                    0
                },
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn precision_input_matches_whole_notches_at_each_dpi_and_row_size() {
        for step in [48, 60, 72, 96] {
            for unit in [1, 24, 30, 60, 120] {
                let mut coarse = Wheel::default();
                let expected = coarse.movement(1200, step, unit);
                for chunk in [1, 5, 15, 30, 60, 120] {
                    let mut fine = Wheel::default();
                    let actual: i32 = (0..1200 / chunk)
                        .map(|_| fine.movement(chunk, step, unit))
                        .sum();
                    assert_eq!(actual, expected, "step {step}, unit {unit}, chunk {chunk}");
                    assert_eq!(fine.remainder, coarse.remainder);
                }
            }
        }
    }

    #[test]
    fn reversing_or_changing_scale_does_not_keep_old_fractional_motion() {
        let mut wheel = Wheel::default();
        assert_eq!(wheel.movement(60, 48, 30), 0);
        assert_eq!(wheel.movement(-120, 48, 30), -1);
        assert_eq!(wheel.movement(-120, 96, 60), -1);
        assert_eq!(wheel.movement(-120, 48, 1), -48);
    }
}
