//! Pull-based Win32 enumeration. Each call has a strict operation/time budget;
//! the host performs asynchronous pauses, so native calls never sleep.
use crate::{Entry, INVALID_ARGUMENT, OK, Text, boundary};
use std::collections::VecDeque;
use std::ffi::c_void;
use std::time::{Duration, Instant};

const SYSTEM: u32 = 4;
const DIRECTORY: u32 = 0x10;
const REPARSE_POINT: u32 = 0x400;
const INVALID_ATTRIBUTES: u32 = u32::MAX;
const INVALID_HANDLE: *mut c_void = -1isize as *mut c_void;

#[repr(C)]
#[derive(Default)]
struct FileTime {
    low: u32,
    high: u32,
}

#[repr(C)]
struct FindData {
    attributes: u32,
    creation: FileTime,
    access: FileTime,
    write: FileTime,
    size_high: u32,
    size_low: u32,
    reserved0: u32,
    reserved1: u32,
    name: [u16; 260],
    alternate: [u16; 14],
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn FindFirstFileExW(
        name: *const u16,
        level: i32,
        data: *mut FindData,
        search: i32,
        filter: *mut c_void,
        flags: u32,
    ) -> *mut c_void;
    fn FindNextFileW(handle: *mut c_void, data: *mut FindData) -> i32;
    fn FindClose(handle: *mut c_void) -> i32;
    fn GetFileAttributesW(name: *const u16) -> u32;
}

struct Directory {
    path: Vec<u16>,
    depth: i32,
}
struct OpenDirectory {
    handle: *mut c_void,
    directory: Directory,
}
impl Drop for OpenDirectory {
    fn drop(&mut self) {
        unsafe {
            FindClose(self.handle);
        }
    }
}

pub struct Traversal {
    pending: VecDeque<Directory>,
    current: Option<OpenDirectory>,
    parents: Vec<OpenDirectory>,
    root_prefix_len: usize,
    path: Vec<u16>,
}

// Always use the long-path form internally, including UNC roots. Output paths
// retain the caller's original form to keep cache keys and UI paths compatible.
fn native_path(path: &[u16], wildcard: bool) -> Vec<u16> {
    let extended: Vec<u16> = "\\\\?\\".encode_utf16().collect();
    let mut native = Vec::with_capacity(path.len() + 10);
    if path.starts_with(&extended) {
        native.extend_from_slice(path);
    } else if path.starts_with(&[92, 92]) {
        native.extend("\\\\?\\UNC\\".encode_utf16());
        native.extend_from_slice(&path[2..]);
    } else {
        native.extend_from_slice(&extended);
        native.extend_from_slice(path);
    }
    if wildcard {
        if native.last() != Some(&92) {
            native.push(92);
        }
        native.push(42);
    }
    native.push(0);
    native
}

impl Traversal {
    fn new(root: &[u16]) -> Self {
        let mut pending = VecDeque::new();
        let path = native_path(root, false);
        let attributes = unsafe { GetFileAttributesW(path.as_ptr()) };
        if attributes != INVALID_ATTRIBUTES
            && attributes & (SYSTEM | REPARSE_POINT) == 0
            && attributes & DIRECTORY != 0
        {
            pending.push_back(Directory {
                path: root.to_vec(),
                depth: 0,
            });
        }
        Self {
            pending,
            current: None,
            parents: Vec::new(),
            root_prefix_len: root.len() + usize::from(root.last() != Some(&92)),
            path: Vec::new(),
        }
    }

    fn step(
        &mut self,
        callback: unsafe extern "C" fn(*mut c_void, *const Entry) -> i32,
        context: *mut c_void,
    ) -> bool {
        let mut data: FindData = unsafe { std::mem::zeroed() };
        if self.current.is_none() && self.pending.is_empty() {
            self.current = self.parents.pop();
        }
        if self.current.is_none() {
            let Some(directory) = self.pending.pop_front() else {
                return false;
            };
            // Recheck queued directories: a link/system attribute may have
            // appeared since its parent was enumerated.
            let path = native_path(&directory.path, false);
            let attributes = unsafe { GetFileAttributesW(path.as_ptr()) };
            if attributes == INVALID_ATTRIBUTES || attributes & (SYSTEM | REPARSE_POINT) != 0 {
                return true;
            }
            let pattern = native_path(&directory.path, true);
            // FindExInfoBasic skips obsolete DOS short names. Large fetch is
            // optional (older/network filesystems can reject that flag).
            let mut handle = unsafe {
                FindFirstFileExW(pattern.as_ptr(), 1, &mut data, 0, std::ptr::null_mut(), 2)
            };
            if handle == INVALID_HANDLE {
                handle = unsafe {
                    FindFirstFileExW(pattern.as_ptr(), 1, &mut data, 0, std::ptr::null_mut(), 0)
                };
            }
            if handle == INVALID_HANDLE {
                return true;
            }
            self.current = Some(OpenDirectory { handle, directory });
        } else if unsafe { FindNextFileW(self.current.as_ref().unwrap().handle, &mut data) } == 0 {
            self.current = None;
            return true;
        }
        if data.attributes & (SYSTEM | REPARSE_POINT) != 0 {
            return true;
        }
        let name_len = data
            .name
            .iter()
            .position(|&ch| ch == 0)
            .unwrap_or(data.name.len());
        let name = &data.name[..name_len];
        if name == [46] || name == [46, 46] || name.is_empty() {
            return true;
        }
        let parent = &self.current.as_ref().unwrap().directory;
        self.path.clear();
        self.path.extend_from_slice(&parent.path);
        if self.path.last() != Some(&92) {
            self.path.push(92);
        }
        self.path.extend_from_slice(name);
        let depth = parent.depth.saturating_add(1);
        let is_directory = data.attributes & DIRECTORY != 0;
        if is_directory {
            // Descend immediately instead of retaining a path for every sibling
            // directory. Enumeration state grows with tree depth, not width.
            self.parents.push(self.current.take().unwrap());
            self.pending.push_back(Directory {
                path: self.path.clone(),
                depth,
            });
        }
        let entry = Entry {
            path: Text::from_slice(&self.path),
            name: Text::from_slice(name),
            relative: Text::from_slice(&self.path[self.root_prefix_len..]),
            depth,
            directory: u32::from(is_directory),
        };
        unsafe { callback(context, &entry) != 0 }
    }
}

/// # Safety
/// Root is an absolute, normalized Windows path in a pinned UTF-16 buffer;
/// output is writable. The returned handle is exclusive and closed once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_walk_open(root: Text, output: *mut *mut Traversal) -> i32 {
    boundary(|| {
        if output.is_null() {
            return INVALID_ARGUMENT;
        }
        unsafe {
            *output = std::ptr::null_mut();
        }
        let Some(root) = (unsafe { root.get() }) else {
            return INVALID_ARGUMENT;
        };
        if root.is_empty()
            || root.contains(&0)
            || root.contains(&47)
            || !(root.starts_with(&[92, 92]) || (root.len() >= 3 && root[1] == 58 && root[2] == 92))
        {
            return INVALID_ARGUMENT;
        }
        unsafe {
            *output = Box::into_raw(Box::new(Traversal::new(root)));
        }
        OK
    })
}

/// # Safety
/// The handle is live and exclusive. The callback copies entry fields before
/// returning and must not throw/reenter. Return zero from callback to stop this
/// batch. `done` points to writable memory. At most 64 operations or 8 ms/call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_walk_next(
    handle: *mut Traversal,
    callback: unsafe extern "C" fn(*mut c_void, *const Entry) -> i32,
    context: *mut c_void,
    done: *mut u32,
) -> i32 {
    boundary(|| {
        if handle.is_null() || done.is_null() {
            return INVALID_ARGUMENT;
        }
        let walk = unsafe { &mut *handle };
        let started = Instant::now();
        for _ in 0..64 {
            if !walk.step(callback, context) || started.elapsed() >= Duration::from_millis(8) {
                break;
            }
        }
        unsafe {
            *done = u32::from(
                walk.pending.is_empty() && walk.current.is_none() && walk.parents.is_empty(),
            );
        }
        OK
    })
}

/// # Safety
/// Handle is null or a live exclusive traversal handle, released exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_walk_close(handle: *mut Traversal) {
    if !handle.is_null() {
        let _ = boundary(|| {
            drop(unsafe { Box::from_raw(handle) });
            OK
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn extends_local_and_unc_paths_without_changing_existing_extended_paths() {
        for (input, expected) in [
            ("C:\\data", "\\\\?\\C:\\data\\*\0"),
            ("\\\\host\\share", "\\\\?\\UNC\\host\\share\\*\0"),
            ("\\\\?\\C:\\data", "\\\\?\\C:\\data\\*\0"),
        ] {
            assert_eq!(
                String::from_utf16(&native_path(
                    &input.encode_utf16().collect::<Vec<_>>(),
                    true
                ))
                .unwrap(),
                expected
            );
        }
    }
}
