use crate::{Entry, INVALID_ARGUMENT, OK, Text, boundary};
use std::cmp::Ordering;

// The host supplies its Unicode operations. ASCII stays entirely in Rust;
// non-ASCII uses Windows ordinal comparison rules.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct Unicode {
    pub compare: unsafe extern "C" fn(Text, Text) -> i32,
    pub find: unsafe extern "C" fn(Text, Text) -> i32,
    pub letter_or_digit: unsafe extern "C" fn(u16) -> i32,
}

impl Unicode {
    fn compare(&self, left: &[u16], right: &[u16]) -> Ordering {
        // Even an ASCII prefix must defer to the host when the other side is
        // Unicode (e.g. long s): mixed strings use ordinal Unicode rules.
        if left.iter().chain(right).all(|&ch| ch < 128) {
            return left
                .iter()
                .map(|&ch| upper(ch))
                .cmp(right.iter().map(|&ch| upper(ch)));
        }
        unsafe { (self.compare)(Text::from_slice(left), Text::from_slice(right)).cmp(&0) }
    }

    fn find(&self, candidate: &[u16], needle: &[u16]) -> Option<usize> {
        if needle.is_empty() {
            return Some(0);
        }
        if candidate.len() < needle.len() {
            return None;
        }
        if candidate.iter().chain(needle).all(|&ch| ch < 128) {
            let first = upper(needle[0]);
            return candidate.windows(needle.len()).position(|window| {
                upper(window[0]) == first
                    && window
                        .iter()
                        .zip(needle)
                        .all(|(&a, &b)| upper(a) == upper(b))
            });
        }
        let index = unsafe { (self.find)(Text::from_slice(candidate), Text::from_slice(needle)) };
        (index >= 0).then_some(index as usize)
    }

    fn letter_or_digit(&self, ch: u16) -> bool {
        if ch < 128 {
            return (ch as u8).is_ascii_alphanumeric();
        }
        unsafe { (self.letter_or_digit)(ch) != 0 }
    }
}

fn upper(ch: u16) -> u16 {
    if (b'a' as u16..=b'z' as u16).contains(&ch) {
        ch - 32
    } else {
        ch
    }
}

struct Term {
    original: Vec<u16>,
    normalized: Vec<u16>,
}

#[derive(Clone)]
struct Match {
    path: Vec<u16>,
    name: Vec<u16>,
    depth: i32,
    score: i64,
    sequence: u64,
}

struct Candidate<'a> {
    path: &'a [u16],
    name: &'a [u16],
    depth: i32,
    score: i64,
    sequence: u64,
}

impl Match {
    fn candidate(&self) -> Candidate<'_> {
        Candidate {
            path: &self.path,
            name: &self.name,
            depth: self.depth,
            score: self.score,
            sequence: self.sequence,
        }
    }
}

pub struct Search {
    terms: Vec<Term>,
    unicode: Unicode,
    show_extensions: bool,
    limit: usize,
    // Max-heap: the worst retained match is at index zero. Only the best K
    // paths are copied; rejected candidates borrow the caller's batch buffers.
    heap: Vec<Match>,
    sequence: u64,
    scratch: Vec<u16>,
}

impl Search {
    fn compare(&self, left: &Candidate<'_>, right: &Candidate<'_>) -> Ordering {
        left.score
            .cmp(&right.score)
            .then_with(|| left.depth.cmp(&right.depth))
            .then_with(|| self.unicode.compare(left.name, right.name))
            .then_with(|| left.sequence.cmp(&right.sequence))
    }

    fn sift_down(&mut self, mut index: usize) {
        loop {
            let left = index * 2 + 1;
            if left >= self.heap.len() {
                break;
            }
            let right = left + 1;
            let worst = if right < self.heap.len()
                && self
                    .compare(&self.heap[right].candidate(), &self.heap[left].candidate())
                    .is_gt()
            {
                right
            } else {
                left
            };
            if !self
                .compare(&self.heap[worst].candidate(), &self.heap[index].candidate())
                .is_gt()
            {
                break;
            }
            self.heap.swap(index, worst);
            index = worst;
        }
    }

    fn keep(&mut self, candidate: Candidate<'_>) {
        if self.heap.len() == self.limit
            && !self.compare(&candidate, &self.heap[0].candidate()).is_lt()
        {
            return;
        }
        // At most K comparisons, only for candidates that can enter the heap.
        // Deduplicating before truncation prevents duplicate snapshots from
        // hiding lower-ranked unique results.
        let duplicate = self
            .heap
            .iter()
            .position(|entry| self.unicode.compare(&entry.path, candidate.path).is_eq());
        if let Some(index) = duplicate {
            if !self
                .compare(&candidate, &self.heap[index].candidate())
                .is_lt()
            {
                return;
            }
            self.heap[index] = Match {
                path: candidate.path.to_vec(),
                name: candidate.name.to_vec(),
                depth: candidate.depth,
                score: candidate.score,
                sequence: candidate.sequence,
            };
            self.sift_down(index);
            return;
        }
        let entry = Match {
            path: candidate.path.to_vec(),
            name: candidate.name.to_vec(),
            depth: candidate.depth,
            score: candidate.score,
            sequence: candidate.sequence,
        };
        if self.heap.len() == self.limit {
            self.heap[0] = entry;
            self.sift_down(0);
        } else {
            self.heap.push(entry);
            let mut index = self.heap.len() - 1;
            while index > 0 {
                let parent = (index - 1) / 2;
                if !self
                    .compare(
                        &self.heap[index].candidate(),
                        &self.heap[parent].candidate(),
                    )
                    .is_gt()
                {
                    break;
                }
                self.heap.swap(index, parent);
                index = parent;
            }
        }
    }

    fn match_index(&mut self, candidate: &[u16], term_index: usize) -> Option<usize> {
        let term = &self.terms[term_index];
        if let Some(index) = self.unicode.find(candidate, &term.original) {
            return Some(index);
        }
        if !candidate.contains(&(b'/' as u16)) && term.original == term.normalized {
            return None;
        }
        self.scratch.clear();
        self.scratch.extend(
            candidate
                .iter()
                .map(|&ch| if ch == b'/' as u16 { b'\\' as u16 } else { ch }),
        );
        self.unicode.find(&self.scratch, &term.normalized)
    }

    fn name_score(&self, name: &[u16], term: &[u16], index: usize, depth: i32) -> i64 {
        let exact = self.unicode.compare(name, term).is_eq();
        let prefix =
            name.len() >= term.len() && self.unicode.compare(&name[..term.len()], term).is_eq();
        let word_prefix = index == 0 || !self.unicode.letter_or_digit(name[index - 1]);
        i64::from(!exact) * 100
            + i64::from(!prefix) * 28
            + i64::from(!word_prefix) * 12
            + (index as i64 * 4).min(64)
            + ((i64::from(depth) - 1).max(0) * 3).min(30)
            + (name.len().abs_diff(term.len()) as i64).min(22)
    }

    fn path_score(&self, path: &[u16], term: &[u16], index: usize, depth: i32) -> i64 {
        let segment = index == 0 || !self.unicode.letter_or_digit(path[index - 1]);
        240 + i64::from(index != 0) * 48
            + i64::from(!segment) * 20
            + (index as i64 * 2).min(120)
            + ((i64::from(depth) - 1).max(0) * 4).min(36)
            + (path.len().abs_diff(term.len()) as i64).min(36)
    }

    fn add(&mut self, path: &[u16], name: &[u16], relative: &[u16], depth: i32, directory: bool) {
        let mut display = name;
        if !directory && !self.show_extensions {
            if let Some(dot) = name.iter().rposition(|&ch| ch == b'.' as u16) {
                // Path.GetFileNameWithoutExtension may yield an empty/blank
                // stem. The UI deliberately falls back to the original name.
                if name[..dot].iter().any(|&ch| !is_whitespace(ch)) {
                    display = &name[..dot];
                }
            }
        }
        let sequence = self.sequence;
        self.sequence += 1;
        let mut score = ((self.terms.len().saturating_sub(1) * 6) as i64).min(24);
        for term_index in 0..self.terms.len() {
            if let Some(index) = self.match_index(display, term_index) {
                score += self.name_score(display, &self.terms[term_index].original, index, depth);
            } else if display.len() != name.len() {
                if let Some(index) = self.match_index(name, term_index) {
                    score +=
                        self.name_score(name, &self.terms[term_index].original, index, depth) + 10;
                } else if let Some(index) = self.match_index(relative, term_index) {
                    score +=
                        self.path_score(relative, &self.terms[term_index].original, index, depth);
                } else {
                    return;
                }
            } else if let Some(index) = self.match_index(relative, term_index) {
                score += self.path_score(relative, &self.terms[term_index].original, index, depth);
            } else {
                return;
            }
        }
        if !self.terms.is_empty() {
            self.keep(Candidate {
                path,
                name: display,
                depth,
                score,
                sequence,
            });
        }
    }
}

fn is_whitespace(ch: u16) -> bool {
    matches!(ch, 0x09..=0x0d | 0x20 | 0x85 | 0xa0 | 0x1680 | 0x2000..=0x200a | 0x2028..=0x2029 | 0x202f | 0x205f | 0x3000)
}

/// # Safety
/// `terms` references `count` live Text structs and pinned UTF-16 slices for this
/// call. Callbacks must remain callable until close and must never unwind.
/// `output` is writable. The returned handle is single-owner and not concurrent.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_search_open(
    terms: *const Text,
    count: usize,
    limit: usize,
    show_extensions: u32,
    unicode: Unicode,
    output: *mut *mut Search,
) -> i32 {
    boundary(|| {
        if output.is_null() {
            return INVALID_ARGUMENT;
        }
        unsafe {
            *output = std::ptr::null_mut();
        }
        if limit == 0 || limit > 4096 || count > 4096 || (count > 0 && terms.is_null()) {
            return INVALID_ARGUMENT;
        }
        let input = if count == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(terms, count) }
        };
        let mut owned = Vec::with_capacity(count);
        for &text in input {
            let Some(value) = (unsafe { text.get() }) else {
                return INVALID_ARGUMENT;
            };
            if value.is_empty() {
                return INVALID_ARGUMENT;
            }
            owned.push(Term {
                original: value.to_vec(),
                normalized: value
                    .iter()
                    .map(|&ch| if ch == b'/' as u16 { b'\\' as u16 } else { ch })
                    .collect(),
            });
        }
        let search = Search {
            terms: owned,
            unicode,
            show_extensions: show_extensions != 0,
            limit,
            heap: Vec::with_capacity(limit),
            sequence: 0,
            scratch: Vec::new(),
        };
        unsafe {
            *output = Box::into_raw(Box::new(search));
        }
        OK
    })
}

/// # Safety
/// Handle is live, exclusive, and from open. Entries and their text remain pinned
/// for the call. The native side retains only copies of at most `limit` matches.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_search_add(
    handle: *mut Search,
    entries: *const Entry,
    count: usize,
) -> i32 {
    boundary(|| {
        if handle.is_null() || count > 4096 || (count > 0 && entries.is_null()) {
            return INVALID_ARGUMENT;
        }
        let search = unsafe { &mut *handle };
        let entries = if count == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(entries, count) }
        };
        for entry in entries {
            let (Some(path), Some(name), Some(relative)) = (
                unsafe { entry.path.get() },
                unsafe { entry.name.get() },
                unsafe { entry.relative.get() },
            ) else {
                return INVALID_ARGUMENT;
            };
            search.add(path, name, relative, entry.depth, entry.directory != 0);
        }
        OK
    })
}

/// # Safety
/// Handle is live and exclusive. Callback copies borrowed result text before
/// returning and must not throw or reenter this handle. Can be called repeatedly.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_search_results(
    handle: *mut Search,
    callback: unsafe extern "C" fn(*mut std::ffi::c_void, Text),
    context: *mut std::ffi::c_void,
) -> i32 {
    boundary(|| {
        if handle.is_null() {
            return INVALID_ARGUMENT;
        }
        let search = unsafe { &mut *handle };
        let mut ordered: Vec<_> = search.heap.iter().collect();
        ordered.sort_unstable_by(|a, b| search.compare(&a.candidate(), &b.candidate()));
        for entry in ordered {
            unsafe {
                callback(context, Text::from_slice(&entry.path));
            }
        }
        OK
    })
}

/// # Safety
/// Handle is null or from open, unused concurrently, and closed exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_search_close(handle: *mut Search) {
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
    unsafe extern "C" fn compare(a: Text, b: Text) -> i32 {
        let a = String::from_utf16_lossy(unsafe { a.get().unwrap() }).to_uppercase();
        let b = String::from_utf16_lossy(unsafe { b.get().unwrap() }).to_uppercase();
        match a.cmp(&b) {
            Ordering::Less => -1,
            Ordering::Equal => 0,
            Ordering::Greater => 1,
        }
    }
    unsafe extern "C" fn find(a: Text, b: Text) -> i32 {
        let a = String::from_utf16_lossy(unsafe { a.get().unwrap() }).to_uppercase();
        let b = String::from_utf16_lossy(unsafe { b.get().unwrap() }).to_uppercase();
        a.find(&b)
            .map(|i| a[..i].encode_utf16().count() as i32)
            .unwrap_or(-1)
    }
    unsafe extern "C" fn letter(ch: u16) -> i32 {
        i32::from(char::from_u32(ch as u32).is_some_and(char::is_alphanumeric))
    }
    fn search(query: &str, limit: usize) -> Search {
        Search {
            terms: query
                .split_whitespace()
                .map(|term| Term {
                    original: term.encode_utf16().collect(),
                    normalized: term.replace('/', "\\").encode_utf16().collect(),
                })
                .collect(),
            unicode: Unicode {
                compare,
                find,
                letter_or_digit: letter,
            },
            show_extensions: false,
            limit,
            heap: Vec::new(),
            sequence: 0,
            scratch: Vec::new(),
        }
    }
    fn add(search: &mut Search, path: &str, depth: i32) {
        let name = path.rsplit('\\').next().unwrap();
        search.add(
            &path.encode_utf16().collect::<Vec<_>>(),
            &name.encode_utf16().collect::<Vec<_>>(),
            &path.encode_utf16().collect::<Vec<_>>(),
            depth,
            false,
        );
    }
    #[test]
    fn bounded_heap_keeps_unique_best_matches_after_duplicate_batches() {
        let mut s = search("report", 3);
        for _ in 0..500 {
            add(&mut s, "report.txt", 1);
        }
        for i in (0..1000).rev() {
            add(&mut s, &format!("report-{i:04}.txt"), 1);
        }
        assert_eq!(s.heap.len(), 3);
        let mut names: Vec<_> = s
            .heap
            .iter()
            .map(|e| String::from_utf16_lossy(&e.path))
            .collect();
        names.sort();
        assert_eq!(names, ["report-0000.txt", "report-0001.txt", "report.txt"]);
    }
    #[test]
    fn terms_can_match_different_path_segments_with_mixed_separators() {
        let mut s = search("work/reports final", 80);
        add(&mut s, "work\\reports\\final.pdf", 3);
        add(&mut s, "personal\\final.pdf", 2);
        assert_eq!(s.heap.len(), 1);
    }
    #[test]
    fn hidden_extensions_remain_searchable_and_empty_stems_survive() {
        let mut s = search(".txt", 80);
        add(&mut s, "report.txt", 1);
        add(&mut s, ".txt", 1);
        assert_eq!(s.heap.len(), 2);
        assert!(
            s.heap
                .iter()
                .any(|m| String::from_utf16_lossy(&m.name) == ".txt")
        );
    }
    #[test]
    fn invalid_arguments_are_reported_without_unwinding() {
        assert_eq!(
            unsafe { dp_search_add(std::ptr::null_mut(), std::ptr::null(), 0) },
            INVALID_ARGUMENT
        );
        assert_eq!(boundary(|| panic!("contained")), crate::PANIC);
    }
}
