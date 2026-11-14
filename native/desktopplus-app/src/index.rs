use crate::{
    config::atomic_write,
    shell::{from_wide, path_wide},
};
use desktopplus_core::native::{Query, WalkEntry, Walker};
use std::collections::BTreeMap;
use std::fs::{self, File};
use std::io::{self, BufReader, BufWriter, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const MAGIC: &[u8; 8] = b"DPRSI001";
pub const ROOT_BUDGET: usize = 24 * 1024 * 1024;
const CHANGE_BUDGET: usize = 1024 * 1024;
const RESCAN_INTERVAL: Duration = Duration::from_secs(5 * 60);

// Small name changes stay searchable without rereading/writing a large snapshot.
// A missing entry also hides descendants (deleted or renamed directories).
struct Change {
    entry: Option<Entry>,
    revision: u64,
}

#[derive(Clone)]
struct Entry {
    relative: Vec<u16>,
    name: usize,
    directory: bool,
    depth: i32,
}
impl Entry {
    fn bytes(&self) -> usize {
        std::mem::size_of::<Self>() + self.relative.capacity() * 2
    }
    fn from_walk(entry: &WalkEntry) -> Self {
        Self {
            relative: entry.relative().to_vec(),
            name: entry.name_offset - entry.relative_offset,
            directory: entry.directory,
            depth: entry.depth,
        }
    }
    fn write(&self, writer: &mut impl Write) -> io::Result<()> {
        writer.write_all(&(self.relative.len() as u32).to_le_bytes())?;
        writer.write_all(&[u8::from(self.directory)])?;
        writer.write_all(&self.depth.to_le_bytes())?;
        for ch in &self.relative {
            writer.write_all(&ch.to_le_bytes())?;
        }
        Ok(())
    }
    fn read(reader: &mut impl Read) -> io::Result<Self> {
        let length = read_u32(reader)? as usize;
        if length == 0 || length > 32768 {
            return Err(invalid());
        }
        let mut directory = [0];
        reader.read_exact(&mut directory)?;
        if directory[0] > 1 {
            return Err(invalid());
        }
        let depth = read_u32(reader)? as i32;
        let relative = read_wide(reader, length)?;
        // A cache is never allowed to point outside its configured root.
        if depth <= 0
            || relative.contains(&0)
            || relative.first() == Some(&92)
            || relative.contains(&58)
            || relative
                .split(|ch| *ch == 92 || *ch == 47)
                .any(|part| part == [46, 46] || part == [46] || part.is_empty())
        {
            return Err(invalid());
        }
        let name = relative
            .iter()
            .rposition(|ch| *ch == 92 || *ch == 47)
            .map_or(0, |i| i + 1);
        Ok(Self {
            relative,
            name,
            directory: directory[0] != 0,
            depth,
        })
    }
}
fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid search index")
}
fn read_u32(reader: &mut impl Read) -> io::Result<u32> {
    let mut data = [0; 4];
    reader.read_exact(&mut data)?;
    Ok(u32::from_le_bytes(data))
}
fn read_wide(reader: &mut impl Read, length: usize) -> io::Result<Vec<u16>> {
    let mut bytes = vec![0; length * 2];
    reader.read_exact(&mut bytes)?;
    Ok(bytes
        .chunks_exact(2)
        .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
        .collect())
}
fn header(reader: &mut impl Read, root: &[u16]) -> io::Result<u64> {
    let mut magic = [0; 8];
    reader.read_exact(&mut magic)?;
    if &magic != MAGIC {
        return Err(invalid());
    }
    let length = read_u32(reader)? as usize;
    if length > 32768 || read_wide(reader, length)? != root {
        return Err(invalid());
    }
    let mut count = [0; 8];
    reader.read_exact(&mut count)?;
    let count = u64::from_le_bytes(count);
    if count > 100_000_000 {
        return Err(invalid());
    }
    Ok(count)
}

struct Build {
    walker: Walker,
    writer: BufWriter<File>,
    path: PathBuf,
    count_offset: u64,
    batch: Vec<WalkEntry>,
    entries: Vec<Entry>,
    bytes: usize,
    count: u64,
    memory_complete: bool,
    revision: u64,
}

pub struct Index {
    pub root: PathBuf,
    root_wide: Vec<u16>,
    path: PathBuf,
    entries: Vec<Entry>,
    pub bytes: usize,
    pub count: u64,
    pub complete: bool,
    memory_complete: bool,
    build: Option<Build>,
    pub due: Instant,
    dirty: bool,
    last_save: Instant,
    rescan_after_build: bool,
    pub last_notification: Instant,
    changes: BTreeMap<Vec<u16>, Change>,
    change_bytes: usize,
    revision: u64,
    last_build: Option<Instant>,
    pub error: Option<String>,
}

impl Index {
    pub fn open(root: PathBuf, directory: &Path) -> Self {
        let root_wide = path_wide(&root);
        let hash = root_wide
            .iter()
            .flat_map(|ch| ch.to_le_bytes())
            .fold(0xcbf29ce484222325u64, |hash, byte| {
                (hash ^ byte as u64).wrapping_mul(0x100000001b3)
            });
        let path = directory.join(format!("{hash:016x}.bin"));
        let mut index = Self {
            root,
            root_wide,
            path,
            entries: Vec::new(),
            bytes: 0,
            count: 0,
            complete: false,
            memory_complete: true,
            build: None,
            due: Instant::now() + Duration::from_secs(5),
            dirty: false,
            last_save: Instant::now(),
            rescan_after_build: false,
            last_notification: Instant::now(),
            changes: BTreeMap::new(),
            change_bytes: 0,
            revision: 0,
            last_build: None,
            error: None,
        };
        let loaded = (|| -> io::Result<()> {
            let mut reader = BufReader::with_capacity(65536, File::open(&index.path)?);
            index.count = header(&mut reader, &index.root_wide)?;
            if reader.get_ref().metadata()?.len() > ROOT_BUDGET as u64 {
                index.memory_complete = false;
                return Ok(());
            }
            for _ in 0..index.count {
                let entry = Entry::read(&mut reader)?;
                if index.bytes + entry.bytes() > ROOT_BUDGET {
                    index.memory_complete = false;
                    break;
                }
                index.bytes += entry.bytes();
                index.entries.push(entry);
            }
            Ok(())
        })();
        if loaded.is_ok() {
            index.complete = true;
            if !index.memory_complete {
                index.entries = Vec::new();
                index.bytes = 0;
            }
        } else {
            index.count = 0;
            index.entries.clear();
            index.bytes = 0;
            index.memory_complete = true;
        }
        index
    }
    fn start(&mut self) -> Result<(), String> {
        self.rescan_after_build = false;
        self.error = None;
        if !self.root.is_dir() {
            return Err(format!("Cannot open folder: {}", self.root.display()));
        }
        if self.dirty && self.memory_complete {
            self.last_save = Instant::now() - Duration::from_secs(30);
            self.persist_if_due()?;
        }
        // The previous snapshot stays searchable on disk while rebuilding.
        // Do not retain two full in-memory snapshots of the same root.
        if self.complete {
            self.entries.clear();
            self.entries.shrink_to_fit();
            self.bytes = 0;
            self.memory_complete = false;
        }
        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let path = self
            .path
            .with_extension(format!("{}.tmp", crate::model::new_id()));
        let file = File::options()
            .write(true)
            .create_new(true)
            .open(&path)
            .map_err(|e| e.to_string())?;
        let mut writer = BufWriter::with_capacity(65536, file);
        writer.write_all(MAGIC).map_err(|e| e.to_string())?;
        writer
            .write_all(&(self.root_wide.len() as u32).to_le_bytes())
            .map_err(|e| e.to_string())?;
        for ch in &self.root_wide {
            writer
                .write_all(&ch.to_le_bytes())
                .map_err(|e| e.to_string())?;
        }
        let count_offset = writer.stream_position().map_err(|e| e.to_string())?;
        writer
            .write_all(&0u64.to_le_bytes())
            .map_err(|e| e.to_string())?;
        let walker =
            Walker::new(&self.root_wide).map_err(|e| format!("Cannot index folder ({e})"))?;
        self.build = Some(Build {
            walker,
            writer,
            path,
            count_offset,
            batch: Vec::with_capacity(64),
            entries: Vec::new(),
            bytes: 0,
            count: 0,
            memory_complete: true,
            revision: self.revision,
        });
        Ok(())
    }
    pub fn preparing(&self) -> bool {
        !self.complete && self.error.is_none()
    }
    pub fn has_work(&self) -> bool {
        self.build.is_some() || Instant::now() >= self.due
    }
    pub fn step(&mut self) -> Result<bool, String> {
        if self.build.is_none() {
            self.start()?;
        }
        let build = self.build.as_mut().unwrap();
        let done = build
            .walker
            .next_batch(&mut build.batch)
            .map_err(|e| format!("Directory scan failed ({e})"))?;
        for entry in &build.batch {
            let entry = Entry::from_walk(entry);
            entry.write(&mut build.writer).map_err(|e| e.to_string())?;
            build.count += 1;
            if build.memory_complete && build.bytes + entry.bytes() <= ROOT_BUDGET {
                build.bytes += entry.bytes();
                build.entries.push(entry);
            } else if build.memory_complete {
                build.memory_complete = false;
                build.entries = Vec::new();
                build.bytes = 0;
            }
        }
        if !done {
            return Ok(false);
        }
        let mut build = self.build.take().unwrap();
        build
            .writer
            .seek(SeekFrom::Start(build.count_offset))
            .map_err(|e| e.to_string())?;
        build
            .writer
            .write_all(&build.count.to_le_bytes())
            .map_err(|e| e.to_string())?;
        build.writer.flush().map_err(|e| e.to_string())?;
        drop(build.writer);
        replace(&build.path, &self.path).map_err(|e| e.to_string())?;
        self.entries = build.entries;
        self.bytes = build.bytes;
        self.count = build.count;
        self.memory_complete = build.memory_complete;
        self.complete = true;
        self.changes
            .retain(|_, change| change.revision > build.revision);
        self.recount_changes();
        self.last_build = Some(Instant::now());
        let changed = !self.changes.is_empty();
        if self.memory_complete && changed {
            let mut entries = std::mem::take(&mut self.entries);
            entries.retain(|entry| !self.shadowed(&entry.relative));
            entries.extend(self.changes.values_mut().filter_map(|c| c.entry.take()));
            self.entries = entries;
            self.bytes = self.entries.iter().map(Entry::bytes).sum();
            self.count = self.entries.len() as u64;
            self.changes.clear();
            self.change_bytes = 0;
        }
        self.due = if self.rescan_after_build {
            Instant::now() + RESCAN_INTERVAL
        } else {
            far_future()
        };
        self.last_save = Instant::now();
        self.dirty = changed && self.memory_complete;
        Ok(true)
    }
    pub fn memory_bytes(&self) -> usize {
        self.bytes + self.build.as_ref().map_or(0, |build| build.bytes) + self.change_bytes
    }
    pub fn published_count(&self) -> u64 {
        if self.complete {
            self.count
        } else {
            self.build.as_ref().map_or(0, |build| build.count)
        }
    }
    pub fn trim_memory(&mut self) {
        // Do not discard watcher edits before the snapshot has been saved.
        if self.dirty && self.memory_complete {
            self.last_save = Instant::now() - Duration::from_secs(30);
            if self.persist_if_due().is_err() {
                self.refresh();
                return;
            }
        }
        self.entries.clear();
        self.entries.shrink_to_fit();
        self.bytes = 0;
        self.memory_complete = false;
        if let Some(build) = &mut self.build {
            build.entries.clear();
            build.entries.shrink_to_fit();
            build.bytes = 0;
            build.memory_complete = false;
        }
    }
    pub fn refresh(&mut self) {
        self.rescan_after_build |= self.build.is_some();
        let due = self.last_build.map_or_else(
            || Instant::now() + Duration::from_millis(1500),
            |last| (last + RESCAN_INTERVAL).max(Instant::now() + Duration::from_millis(1500)),
        );
        // Continuous notifications must neither restart nor indefinitely defer a scan.
        self.due = self.due.min(due);
    }
    pub fn failed(&mut self, error: String) {
        if let Some(build) = self.build.take() {
            let path = build.path.clone();
            drop(build);
            let _ = fs::remove_file(path);
        }
        self.due = Instant::now() + Duration::from_secs(60);
        self.error = Some(error);
    }
    pub fn wake_at(&self) -> Instant {
        if self.dirty && self.memory_complete && self.build.is_none() && self.changes.is_empty() {
            self.due.min(self.last_save + Duration::from_secs(30))
        } else {
            self.due
        }
    }
    // Only flush a partial on-disk snapshot when someone actually searches it.
    pub fn prepare_search(&mut self) -> Result<(), String> {
        if !self.complete {
            if let Some(build) = &mut self.build {
                if !build.memory_complete {
                    build.writer.flush().map_err(|e| e.to_string())?;
                }
            }
        }
        Ok(())
    }
    pub fn search(
        &self,
        query: &str,
        extensions: bool,
        cancelled: impl Fn() -> bool,
    ) -> Result<Vec<PathBuf>, String> {
        if cancelled() {
            return Ok(Vec::new());
        }
        let mut search =
            Query::new(query, extensions, 80).map_err(|e| format!("Invalid query ({e})"))?;
        let mut full = self.root_wide.clone();
        if full.last() != Some(&92) {
            full.push(92);
        }
        let prefix = full.len();
        let mut feed = |entry: &Entry| -> Result<bool, String> {
            if cancelled() {
                return Ok(false);
            }
            full.truncate(prefix);
            full.extend_from_slice(&entry.relative);
            search
                .add(
                    &full,
                    &entry.relative[entry.name..],
                    &entry.relative,
                    entry.directory,
                    entry.depth,
                )
                .map_err(|e| format!("Search failed ({e})"))?;
            Ok(true)
        };
        let (entries, memory, disk, count) = if !self.complete {
            if let Some(build) = &self.build {
                (
                    &build.entries,
                    build.memory_complete,
                    &build.path,
                    build.count,
                )
            } else {
                (&self.entries, true, &self.path, 0)
            }
        } else {
            (&self.entries, self.memory_complete, &self.path, self.count)
        };
        if memory {
            for entry in entries {
                if !self.shadowed(&entry.relative) && !feed(entry)? {
                    return Ok(Vec::new());
                }
            }
        } else {
            let mut reader =
                BufReader::with_capacity(65536, File::open(disk).map_err(|e| e.to_string())?);
            header(&mut reader, &self.root_wide).map_err(|e| e.to_string())?;
            for _ in 0..count {
                if cancelled() {
                    return Ok(Vec::new());
                }
                let entry = Entry::read(&mut reader).map_err(|e| e.to_string())?;
                if !self.shadowed(&entry.relative) && !feed(&entry)? {
                    return Ok(Vec::new());
                }
            }
        }
        for change in self.changes.values() {
            if let Some(entry) = &change.entry {
                if !feed(entry)? {
                    return Ok(Vec::new());
                }
            }
        }
        search
            .results()
            .map(|paths| paths.iter().map(|path| from_wide(path)).collect())
            .map_err(|e| format!("Search failed ({e})"))
    }
    fn shadowed(&self, relative: &[u16]) -> bool {
        !self.changes.is_empty()
            && (self.changes.contains_key(relative)
                || relative
                    .iter()
                    .enumerate()
                    .any(|(i, ch)| *ch == 92 && self.changes.contains_key(&relative[..i])))
    }
    fn recount_changes(&mut self) {
        self.change_bytes = self
            .changes
            .iter()
            .map(|(path, change)| {
                path.capacity() * 2
                    + std::mem::size_of::<Change>()
                    + 64
                    + change.entry.as_ref().map_or(0, Entry::bytes)
            })
            .sum();
    }
    // Contents/size/timestamps are not part of the name index. The worker sends
    // only structural changes here; content changes still refresh visible rows.
    pub fn change(&mut self, path: &Path) {
        let Ok(relative) = path.strip_prefix(&self.root) else {
            return;
        };
        let relative: Vec<_> = path_wide(relative)
            .into_iter()
            .map(|ch| if ch == 47 { 92 } else { ch })
            .collect();
        if relative.is_empty() {
            self.refresh();
            return;
        }
        if self.build.is_some() || !self.memory_complete {
            let metadata = path.symlink_metadata().ok();
            let directory = metadata.as_ref().is_some_and(|m| m.is_dir());
            use std::os::windows::fs::MetadataExt;
            let entry = metadata
                .filter(|m| m.file_attributes() & (4 | 0x400) == 0)
                .map(|m| Entry {
                    name: relative
                        .iter()
                        .rposition(|ch| *ch == 92)
                        .map_or(0, |i| i + 1),
                    depth: relative.iter().filter(|ch| **ch == 92).count() as i32 + 1,
                    relative: relative.clone(),
                    directory: m.is_dir(),
                });
            if self.change_bytes + relative.len() * 4 + 256 > CHANGE_BUDGET {
                self.refresh();
                return;
            }
            // A removed directory invalidates any earlier updates below it too.
            if entry.is_none() {
                self.changes.retain(|p, _| {
                    !(p.starts_with(&relative) && p.get(relative.len()) == Some(&92))
                });
            }
            self.revision += 1;
            self.changes.insert(
                relative,
                Change {
                    entry,
                    revision: self.revision,
                },
            );
            self.recount_changes();
            if directory {
                self.refresh();
            }
            return;
        }
        let is_subtree = path.is_dir();
        self.entries.retain(|entry| {
            !(entry.relative == relative
                || (entry.relative.starts_with(&relative)
                    && entry.relative.get(relative.len()) == Some(&92)))
        });
        if let Ok(metadata) = path.symlink_metadata() {
            use std::os::windows::fs::MetadataExt;
            if metadata.file_attributes() & (4 | 0x400) == 0 {
                let name = relative
                    .iter()
                    .rposition(|ch| *ch == 92)
                    .map_or(0, |i| i + 1);
                let depth = relative.iter().filter(|ch| **ch == 92).count() as i32 + 1;
                self.entries.push(Entry {
                    relative,
                    name,
                    directory: metadata.is_dir(),
                    depth,
                });
            }
        }
        self.bytes = self.entries.iter().map(Entry::bytes).sum();
        self.count = self.entries.len() as u64;
        self.dirty = true;
        if is_subtree {
            self.refresh();
        }
    }
    pub fn persist_if_due(&mut self) -> Result<(), String> {
        if !self.dirty
            || self.build.is_some()
            || !self.memory_complete
            || !self.changes.is_empty()
            || self.last_save.elapsed() < Duration::from_secs(30)
        {
            return Ok(());
        }
        // Also rate-limit failed writes, otherwise the due timer would busy-loop.
        self.last_save = Instant::now();
        let mut bytes = Vec::with_capacity(self.bytes);
        bytes.extend_from_slice(MAGIC);
        bytes.extend_from_slice(&(self.root_wide.len() as u32).to_le_bytes());
        for ch in &self.root_wide {
            bytes.extend_from_slice(&ch.to_le_bytes());
        }
        bytes.extend_from_slice(&self.count.to_le_bytes());
        for entry in &self.entries {
            entry.write(&mut bytes).map_err(|e| e.to_string())?;
        }
        atomic_write(&self.path, &bytes).map_err(|e| e.to_string())?;
        self.last_save = Instant::now();
        self.dirty = false;
        Ok(())
    }
}
impl Drop for Index {
    fn drop(&mut self) {
        if let Some(build) = self.build.take() {
            let path = build.path.clone();
            drop(build);
            let _ = fs::remove_file(path);
        }
    }
}
fn far_future() -> Instant {
    Instant::now() + Duration::from_secs(365 * 86400)
}
fn replace(from: &Path, to: &Path) -> io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::*;
    if unsafe {
        MoveFileExW(
            crate::shell::wide(from).as_ptr(),
            crate::shell::wide(to).as_ptr(),
            MOVEFILE_REPLACE_EXISTING,
        )
    } == 0
    {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let root =
                std::env::temp_dir().join(format!("desktopplus-index-{}", crate::model::new_id()));
            fs::create_dir_all(root.join("files")).unwrap();
            Self(root)
        }
        fn index(&self) -> Index {
            Index::open(self.0.join("files"), &self.0.join("cache"))
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    fn finish(index: &mut Index) {
        for _ in 0..10000 {
            if index.step().unwrap() {
                return;
            }
        }
        panic!("Directory traversal did not terminate");
    }
    #[test]
    fn traverses_siblings_and_deep_folders_and_reuses_disk_cache() {
        let fixture = Fixture::new();
        let root = fixture.0.join("files");
        for number in 0..130 {
            let folder = root.join(format!("branch-{number}/deep/empty"));
            fs::create_dir_all(&folder).unwrap();
            fs::write(
                folder
                    .parent()
                    .unwrap()
                    .join(format!("report-{number}.txt")),
                "test",
            )
            .unwrap();
        }
        let mut index = fixture.index();
        assert!(!index.step().unwrap());
        assert!(index.preparing());
        finish(&mut index);
        assert_eq!(index.count, 520);
        let before = index.search("report", true, || false).unwrap();
        assert_eq!(before.len(), 80);
        let exact = index.search("report-129", true, || false).unwrap();
        assert_eq!(exact, vec![root.join("branch-129/deep/report-129.txt")]);
        index.trim_memory();
        assert_eq!(index.memory_bytes(), 0);
        assert_eq!(index.search("report", true, || false).unwrap(), before);
        drop(index);
        let loaded = fixture.index();
        assert!(loaded.complete);
        assert!(!loaded.has_work());
        assert_eq!(loaded.search("report", true, || false).unwrap(), before);
        assert!(loaded.search("report", true, || true).unwrap().is_empty());
    }
    #[test]
    fn updates_files_without_rescan_and_reconciles_changes_during_build() {
        let fixture = Fixture::new();
        let root = fixture.0.join("files");
        let first = root.join("first-report.txt");
        fs::write(&first, "one").unwrap();
        let mut index = fixture.index();
        finish(&mut index);
        let renamed = root.join("renamed-report.txt");
        fs::rename(&first, &renamed).unwrap();
        index.change(&first);
        index.change(&renamed);
        assert_eq!(
            index.search("report", true, || false).unwrap(),
            vec![renamed]
        );
        assert!(!index.has_work());
        for number in 0..200 {
            fs::write(root.join(format!("file-{number}.txt")), "test").unwrap();
        }
        index.refresh();
        assert!(!index.step().unwrap());
        let late = root.join("late-report.txt");
        fs::write(&late, "late").unwrap();
        index.change(&late);
        finish(&mut index);
        // The late file is reconciled without scheduling another whole-root scan.
        assert!(!index.has_work());
        assert_eq!(
            index.search("late-report", true, || false).unwrap(),
            vec![late]
        );
    }
    #[test]
    fn corrupt_cache_rebuilds_and_cancelled_build_removes_temporary_file() {
        let fixture = Fixture::new();
        fs::write(fixture.0.join("files/visible.txt"), "test").unwrap();
        let mut index = fixture.index();
        finish(&mut index);
        let cache = index.path.clone();
        drop(index);
        fs::write(&cache, "corrupt").unwrap();
        let mut index = fixture.index();
        assert!(!index.complete);
        index.start().unwrap();
        let temporary = index.build.as_ref().unwrap().path.clone();
        assert!(temporary.exists());
        drop(index);
        assert!(!temporary.exists());
        let mut index = fixture.index();
        finish(&mut index);
        assert_eq!(index.search("visible", true, || false).unwrap().len(), 1);
    }
    #[test]
    fn disk_snapshot_updates_names_and_removes_subtrees_without_rebuilding() {
        let fixture = Fixture::new();
        let root = fixture.0.join("files");
        fs::create_dir(root.join("folder")).unwrap();
        fs::write(root.join("folder/old-report.txt"), "old").unwrap();
        fs::write(root.join("rename-report.txt"), "old").unwrap();
        let mut index = fixture.index();
        finish(&mut index);
        index.trim_memory();
        let snapshot = fs::read(&index.path).unwrap();
        let moved = root.join("moved-report.txt");
        fs::rename(root.join("rename-report.txt"), &moved).unwrap();
        index.change(&root.join("rename-report.txt"));
        index.change(&moved);
        fs::write(root.join("folder/new-report.txt"), "new").unwrap();
        index.change(&root.join("folder/new-report.txt"));
        fs::remove_dir_all(root.join("folder")).unwrap();
        index.change(&root.join("folder"));
        assert_eq!(index.search("report", true, || false).unwrap(), vec![moved]);
        assert!(!index.has_work());
        assert!(!index.preparing());
        assert_eq!(fs::read(&index.path).unwrap(), snapshot);
        assert!(index.memory_bytes() < CHANGE_BUDGET);
    }
    #[test]
    fn warm_cache_stays_ready_and_structural_rescans_are_coalesced() {
        let fixture = Fixture::new();
        fs::write(fixture.0.join("files/report.txt"), "old").unwrap();
        let mut index = fixture.index();
        finish(&mut index);
        let mut loaded = fixture.index();
        assert!(!loaded.preparing());
        loaded.start().unwrap();
        assert!(!loaded.preparing());
        finish(&mut loaded);
        let folder = fixture.0.join("files/new-folder");
        fs::create_dir(&folder).unwrap();
        loaded.change(&folder);
        let due = loaded.due;
        for _ in 0..100 {
            loaded.refresh();
        }
        assert_eq!(loaded.due, due);
        assert!(due.saturating_duration_since(Instant::now()) > Duration::from_secs(290));
        assert!(!loaded.has_work());
        assert!(!loaded.preparing());
    }
    #[test]
    fn partial_disk_snapshot_is_flushed_only_for_search() {
        let fixture = Fixture::new();
        let root = fixture.0.join("files");
        for i in 0..200 {
            fs::write(root.join(format!("report-{i}.txt")), "test").unwrap();
        }
        let mut index = fixture.index();
        assert!(!index.step().unwrap());
        index.trim_memory();
        let build = index.build.as_ref().unwrap();
        let path = build.path.clone();
        let count = build.count;
        let before = fs::metadata(&path).unwrap().len();
        assert!(count > 0);
        index.prepare_search().unwrap();
        assert!(fs::metadata(&path).unwrap().len() > before);
        assert_eq!(
            index.search("report", true, || false).unwrap().len(),
            count.min(80) as usize
        );
        finish(&mut index);
        assert!(!index.preparing());
    }
    #[test]
    fn empty_partial_disk_snapshot_can_be_searched_before_first_entry() {
        let fixture = Fixture::new();
        let mut index = fixture.index();
        index.start().unwrap();
        index.trim_memory();
        index.prepare_search().unwrap();
        assert!(index.search("report", true, || false).unwrap().is_empty());
        finish(&mut index);
        assert!(!index.preparing());
    }
    #[test]
    fn disk_updates_during_build_survive_publication_and_next_rebuild() {
        let fixture = Fixture::new();
        let root = fixture.0.join("files");
        for i in 0..200 {
            fs::write(root.join(format!("report-{i}.txt")), "test").unwrap();
        }
        let mut index = fixture.index();
        assert!(!index.step().unwrap());
        index.trim_memory();
        let late = root.join("late-report.txt");
        fs::write(&late, "late").unwrap();
        index.change(&late);
        finish(&mut index);
        assert_eq!(
            index.search("late-report", true, || false).unwrap(),
            vec![late.clone()]
        );
        assert!(!index.has_work());
        finish(&mut index);
        assert_eq!(
            index.search("late-report", true, || false).unwrap(),
            vec![late]
        );
        assert!(index.changes.is_empty());
    }
    #[test]
    fn trimming_memory_preserves_unsaved_name_changes() {
        let fixture = Fixture::new();
        let mut index = fixture.index();
        finish(&mut index);
        let late = fixture.0.join("files/late-report.txt");
        fs::write(&late, "late").unwrap();
        index.change(&late);
        index.trim_memory();
        assert_eq!(
            index.search("late-report", true, || false).unwrap(),
            vec![late.clone()]
        );
        assert_eq!(
            fixture
                .index()
                .search("late-report", true, || false)
                .unwrap(),
            vec![late]
        );
    }
    #[test]
    fn failed_initial_build_stops_preparing_and_retries_with_backoff() {
        let fixture = Fixture::new();
        let mut index = fixture.index();
        fs::remove_dir(&index.root).unwrap();
        let error = index.step().unwrap_err();
        index.failed(error);
        assert!(!index.preparing());
        assert!(!index.has_work());
        assert!(index.error.is_some());
        assert!(
            index.wake_at().saturating_duration_since(Instant::now()) > Duration::from_secs(50)
        );
    }
    #[test]
    fn rejects_path_escape_in_cache() {
        let entry = Entry {
            relative: "..\\secret".encode_utf16().collect(),
            name: 3,
            directory: false,
            depth: 1,
        };
        let mut bytes = Vec::new();
        entry.write(&mut bytes).unwrap();
        assert!(Entry::read(&mut bytes.as_slice()).is_err());
    }
    #[test]
    fn cache_roundtrips_unpaired_windows_surrogates() {
        let entry = Entry {
            relative: vec![0xd800, 65],
            name: 0,
            directory: false,
            depth: 1,
        };
        let mut bytes = Vec::new();
        entry.write(&mut bytes).unwrap();
        assert_eq!(
            Entry::read(&mut bytes.as_slice()).unwrap().relative,
            entry.relative
        );
    }
}
