//! Bounded search and directory traversal for the Rust Windows application.
//! Paths retain UTF-16, including unpaired Windows surrogates.

#[cfg(windows)]
pub mod native;
mod search;
#[cfg(windows)]
mod traversal;

use std::panic::{AssertUnwindSafe, catch_unwind};

pub(crate) const OK: i32 = 0;
pub(crate) const INVALID_ARGUMENT: i32 = -1;
pub(crate) const PANIC: i32 = -2;

pub(crate) fn boundary(operation: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(operation)).unwrap_or(PANIC)
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Text {
    pub data: *const u16,
    pub len: usize,
}

impl Text {
    // The host pins each buffer for the duration of a call. Null is legal only
    // for empty text; lengths are checked before constructing any Rust slice.
    pub(crate) unsafe fn get<'a>(self) -> Option<&'a [u16]> {
        if self.len == 0 {
            return Some(&[]);
        }
        if self.data.is_null()
            || (self.data as usize) % align_of::<u16>() != 0
            || self.len > isize::MAX as usize / size_of::<u16>()
        {
            return None;
        }
        Some(unsafe { std::slice::from_raw_parts(self.data, self.len) })
    }

    pub(crate) fn from_slice(value: &[u16]) -> Self {
        Self {
            data: value.as_ptr(),
            len: value.len(),
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Entry {
    pub path: Text,
    pub name: Text,
    pub relative: Text,
    pub depth: i32,
    pub directory: u32,
}
