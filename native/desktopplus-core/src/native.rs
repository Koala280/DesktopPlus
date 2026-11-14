//! Safe, direct Rust API used by the native application (no CLR or FFI host).
use crate::{Entry, Text, search, traversal};
use std::ffi::c_void;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CompareStringOrdinal(a: *const u16, alen: i32, b: *const u16, blen: i32, ignore: i32)
    -> i32;
    fn FindStringOrdinal(
        flags: u32,
        a: *const u16,
        alen: i32,
        b: *const u16,
        blen: i32,
        ignore: i32,
    ) -> i32;
    fn GetStringTypeW(kind: u32, text: *const u16, len: i32, types: *mut u16) -> i32;
}
unsafe extern "C" fn compare(a: Text, b: Text) -> i32 {
    unsafe { CompareStringOrdinal(a.data, a.len as i32, b.data, b.len as i32, 1) - 2 }
}
unsafe extern "C" fn find(a: Text, b: Text) -> i32 {
    unsafe { FindStringOrdinal(0x00400000, a.data, a.len as i32, b.data, b.len as i32, 1) }
}
unsafe extern "C" fn letter(ch: u16) -> i32 {
    let mut kind = 0;
    unsafe {
        GetStringTypeW(1, &ch, 1, &mut kind);
    }
    i32::from(kind & (0x0100 | 0x0004) != 0)
}

pub struct Query(*mut search::Search);
impl Query {
    pub fn new(query: &str, extensions: bool, limit: usize) -> Result<Self, i32> {
        let mut terms: Vec<Vec<u16>> = Vec::new();
        for word in query.split_whitespace() {
            let value: Vec<u16> = word.encode_utf16().collect();
            if !terms.iter().any(|term| unsafe {
                compare(Text::from_slice(term), Text::from_slice(&value)) == 0
            }) {
                terms.push(value);
            }
        }
        let texts: Vec<_> = terms.iter().map(|term| Text::from_slice(term)).collect();
        let mut handle = std::ptr::null_mut();
        let status = unsafe {
            search::dp_search_open(
                texts.as_ptr(),
                texts.len(),
                limit,
                u32::from(extensions),
                search::Unicode {
                    compare,
                    find,
                    letter_or_digit: letter,
                },
                &mut handle,
            )
        };
        if status == 0 {
            Ok(Self(handle))
        } else {
            Err(status)
        }
    }
    pub fn add(
        &mut self,
        path: &[u16],
        name: &[u16],
        relative: &[u16],
        directory: bool,
        depth: i32,
    ) -> Result<(), i32> {
        let entry = Entry {
            path: Text::from_slice(path),
            name: Text::from_slice(name),
            relative: Text::from_slice(relative),
            depth,
            directory: u32::from(directory),
        };
        let status = unsafe { search::dp_search_add(self.0, &entry, 1) };
        if status == 0 { Ok(()) } else { Err(status) }
    }
    pub fn results(&mut self) -> Result<Vec<Vec<u16>>, i32> {
        unsafe extern "C" fn collect(context: *mut c_void, text: Text) {
            let result = unsafe { &mut *(context as *mut Vec<Vec<u16>>) };
            result.push(unsafe { text.get().unwrap() }.to_vec());
        }
        let mut result = Vec::new();
        let status = unsafe {
            search::dp_search_results(self.0, collect, &mut result as *mut _ as *mut c_void)
        };
        if status == 0 { Ok(result) } else { Err(status) }
    }
}
impl Drop for Query {
    fn drop(&mut self) {
        unsafe {
            search::dp_search_close(self.0);
        }
    }
}

#[derive(Debug, Clone)]
pub struct WalkEntry {
    pub path: Vec<u16>,
    pub relative_offset: usize,
    pub name_offset: usize,
    pub directory: bool,
    pub depth: i32,
}
impl WalkEntry {
    pub fn name(&self) -> &[u16] {
        &self.path[self.name_offset..]
    }
    pub fn relative(&self) -> &[u16] {
        &self.path[self.relative_offset..]
    }
}

pub struct Walker(*mut traversal::Traversal);
impl Walker {
    pub fn new(root: &[u16]) -> Result<Self, i32> {
        let mut handle = std::ptr::null_mut();
        let status = unsafe { traversal::dp_walk_open(Text::from_slice(root), &mut handle) };
        if status == 0 {
            Ok(Self(handle))
        } else {
            Err(status)
        }
    }
    /// One bounded batch; the caller is responsible for cooperative pauses.
    pub fn next_batch(&mut self, entries: &mut Vec<WalkEntry>) -> Result<bool, i32> {
        unsafe extern "C" fn collect(context: *mut c_void, input: *const Entry) -> i32 {
            let entries = unsafe { &mut *(context as *mut Vec<WalkEntry>) };
            let input = unsafe { &*input };
            entries.push(WalkEntry {
                path: unsafe { input.path.get().unwrap() }.to_vec(),
                relative_offset: input.path.len - input.relative.len,
                name_offset: input.path.len - input.name.len,
                directory: input.directory != 0,
                depth: input.depth,
            });
            1
        }
        entries.clear();
        let mut done = 0;
        let status = unsafe {
            traversal::dp_walk_next(self.0, collect, entries as *mut _ as *mut c_void, &mut done)
        };
        if status == 0 {
            Ok(done != 0)
        } else {
            Err(status)
        }
    }
}
impl Drop for Walker {
    fn drop(&mut self) {
        unsafe {
            traversal::dp_walk_close(self.0);
        }
    }
}
