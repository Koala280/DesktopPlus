use crate::{index::Index, model::Tab, shell};
use notify::{RecommendedWatcher, RecursiveMode, Watcher};
use std::collections::{HashMap, HashSet, VecDeque};
use std::fs;
use std::os::windows::{ffi::OsStrExt, fs::MetadataExt};
use std::path::{Path, PathBuf};
use std::sync::{
    Arc,
    atomic::{AtomicBool, AtomicU64, Ordering},
    mpsc::{self, Receiver, Sender},
};
use std::time::{Duration, Instant};
use windows_sys::Win32::System::Threading::{
    GetCurrentThread, SetThreadPriority, THREAD_MODE_BACKGROUND_BEGIN, THREAD_MODE_BACKGROUND_END,
};
use windows_sys::Win32::UI::WindowsAndMessaging::PostMessageW;

mod image_loader;
pub use image_loader::{ImageLoader, ImageRequest};

pub const WAKE: u32 = 0x8000 + 20;

#[derive(Clone, Debug)]
pub struct Row {
    pub path: PathBuf,
    pub name: Vec<u16>,
    pub kind: Vec<u16>,
    pub directory: bool,
    pub size: u64,
    pub modified: u64,
    pub created: u64,
    pub hidden: bool,
    pub parent_navigation: bool,
}
impl Row {
    pub fn read(path: PathBuf, extensions: bool) -> Option<Self> {
        let metadata = path.symlink_metadata().ok()?;
        let name = if extensions || metadata.is_dir() {
            path.file_name().unwrap_or(path.as_os_str())
        } else {
            path.file_stem()
                .filter(|name| !name.is_empty())
                .unwrap_or(path.as_os_str())
        };
        let kind = if metadata.is_dir() {
            "Folder".into()
        } else {
            path.extension()
                .map(|value| value.to_string_lossy().to_uppercase())
                .filter(|ext| !ext.is_empty())
                .unwrap_or_else(|| "File".into())
        };
        Some(Self {
            name: name.encode_wide().collect(),
            kind: kind.encode_utf16().collect(),
            directory: metadata.is_dir(),
            size: metadata.len(),
            modified: metadata.last_write_time(),
            created: metadata.creation_time(),
            hidden: metadata.file_attributes() & 2 != 0,
            parent_navigation: false,
            path,
        })
    }
    pub fn photo(&self) -> bool {
        self.path.extension().is_some_and(|extension| {
            matches!(
                extension.to_string_lossy().to_ascii_lowercase().as_str(),
                "jpg" | "jpeg" | "png" | "gif" | "bmp" | "webp" | "tif" | "tiff" | "heic"
            )
        })
    }
}

pub enum Job {
    Configure(Vec<PathBuf>),
    List {
        window: usize,
        generation: u64,
        gate: Arc<AtomicU64>,
        tab: Box<Tab>,
        query: String,
        sort: usize,
        descending: bool,
    },
    Transfer {
        owner: usize,
        action: shell::FileAction,
        sources: Vec<PathBuf>,
        destination: Option<PathBuf>,
    },
    Sort {
        settings: Box<crate::model::Settings>,
        roots: Vec<PathBuf>,
        storage: PathBuf,
        backups: PathBuf,
    },
    Metadata {
        generation: u64,
        gate: Arc<AtomicU64>,
        path: PathBuf,
        modified: u64,
        column: usize,
    },

    Fs(notify::Result<notify::Event>),
    Stop,
}
pub enum Output {
    Rows {
        window: usize,
        generation: u64,
        rows: Arc<Vec<Row>>,
        preparing: bool,
        error: Option<String>,
    },
    Changed(Vec<(PathBuf, bool)>),
    Indexed(PathBuf),
    SearchStatus {
        count: u64,
        preparing: bool,
        error: Option<String>,
    },
    Done(Result<(), String>),
    Sorted(Result<crate::sort::Report, String>),
    Image {
        path: PathBuf,
        modified: u64,
        spec: shell::ImageSpec,
        bitmap: Option<shell::Bitmap>,
        cancelled: bool,
    },
    Error(String),

    Metadata {
        path: PathBuf,
        modified: u64,
        column: usize,
        value: Option<Vec<u16>>,
    },
}

pub struct Worker {
    pub sender: Sender<Job>,
    pub receiver: Receiver<Output>,
    pub images: ImageLoader,
    thread: Option<std::thread::JoinHandle<()>>,
    stopping: Arc<AtomicBool>,
}
impl Worker {
    pub fn start(main: usize, directory: PathBuf) -> Self {
        let (sender, receiver) = mpsc::channel();
        let (outputs, results) = mpsc::channel();
        let images = ImageLoader::new(main, outputs.clone());
        let changes = sender.clone();
        let stopping = Arc::new(AtomicBool::new(false));
        let stop = stopping.clone();
        let thread = std::thread::Builder::new()
            .name("DesktopPlus I/O".into())
            .spawn(move || {
                unsafe {
                    windows_sys::Win32::System::Com::CoInitializeEx(
                        std::ptr::null(),
                        windows_sys::Win32::System::Com::COINIT_APARTMENTTHREADED as u32,
                    );
                }
                let mut worker = Engine {
                    main,
                    outputs,
                    directory,
                    indexes: HashMap::new(),
                    order: Vec::new(),
                    cursor: 0,
                    last_status: None,
                };
                let mut watcher: Option<RecommendedWatcher> =
                    notify::recommended_watcher(move |event| {
                        let _ = changes.send(Job::Fs(event));
                    })
                    .ok();
                let mut pending = HashMap::<PathBuf, bool>::new();
                let mut pending_since = Instant::now();
                let mut next_step = Instant::now();
                let mut metadata = VecDeque::new();
                while !stop.load(Ordering::Relaxed) {
                    let active = worker.indexes.values().any(Index::has_work);
                    let now = Instant::now();
                    let mut timeout = if !metadata.is_empty() {
                        Duration::ZERO
                    } else if active {
                        next_step.saturating_duration_since(now)
                    } else {
                        worker
                            .indexes
                            .values()
                            .map(|index| {
                                index
                                    .wake_at()
                                    .max(next_step)
                                    .saturating_duration_since(now)
                            })
                            .min()
                            .unwrap_or(Duration::from_secs(3600))
                    };
                    if !pending.is_empty() {
                        timeout = timeout.min(
                            (pending_since + Duration::from_millis(200))
                                .saturating_duration_since(now),
                        );
                    }
                    match receiver.recv_timeout(timeout) {
                        Ok(Job::Stop) | Err(mpsc::RecvTimeoutError::Disconnected) => break,
                        Ok(Job::Configure(paths)) => {
                            let keep: HashSet<_> = paths.iter().cloned().collect();
                            for root in worker.order.iter().filter(|root| !keep.contains(*root)) {
                                if let Some(watcher) = &mut watcher {
                                    let _ = watcher.unwatch(root);
                                }
                                worker.indexes.remove(root);
                            }
                            worker.order = paths;
                            worker.order.dedup();
                            for root in &worker.order {
                                if !worker.indexes.contains_key(root) {
                                    if let Some(watcher) = &mut watcher {
                                        if let Err(error) =
                                            watcher.watch(root, RecursiveMode::Recursive)
                                        {
                                            worker.send(Output::Error(format!(
                                                "Cannot watch {}: {error}",
                                                root.display()
                                            )));
                                        }
                                    }
                                    worker.indexes.insert(
                                        root.clone(),
                                        Index::open(root.clone(), &worker.directory),
                                    );
                                }
                            }
                            worker.publish_status();
                        }
                        Ok(Job::List {
                            window,
                            generation,
                            gate,
                            tab,
                            query,
                            sort,
                            descending,
                        }) => {
                            if gate.load(Ordering::Relaxed) != generation {
                                continue;
                            }
                            let cancelled = || gate.load(Ordering::Relaxed) != generation;
                            let (mut rows, preparing, error) =
                                worker.list(&tab, &query, sort, descending, cancelled);
                            if query.is_empty()
                                && tab.panel_type == "Folder"
                                && tab
                                    .extra
                                    .get("IconViewParentNavigationMode")
                                    .and_then(serde_json::Value::as_str)
                                    == Some("item")
                            {
                                if let Some(parent) = Path::new(&tab.folder_path).parent() {
                                    rows.insert(
                                        0,
                                        Row {
                                            path: parent.into(),
                                            name: "..".encode_utf16().collect(),
                                            kind: Vec::new(),
                                            directory: true,
                                            size: 0,
                                            modified: 0,
                                            created: 0,
                                            hidden: false,
                                            parent_navigation: true,
                                        },
                                    );
                                }
                            }
                            if !cancelled() {
                                worker.send(Output::Rows {
                                    window,
                                    generation,
                                    rows: Arc::new(rows),
                                    preparing,
                                    error,
                                });
                            }
                        }
                        Ok(Job::Transfer {
                            owner,
                            action,
                            sources,
                            destination,
                        }) => {
                            worker.send(Output::Done(shell::transfer(
                                owner as _,
                                action,
                                &sources,
                                destination.as_deref(),
                            )));
                        }
                        Ok(Job::Sort {
                            settings,
                            roots,
                            storage,
                            backups,
                        }) => {
                            worker.send(Output::Sorted(crate::sort::run(
                                &settings, &roots, &storage, &backups,
                            )));
                        }
                        Ok(Job::Metadata {
                            generation,
                            gate,
                            path,
                            modified,
                            column,
                        }) => {
                            metadata.push_back((generation, gate, path, modified, column));
                        }
                        Ok(Job::Fs(Ok(event))) => {
                            if matches!(event.kind, notify::EventKind::Access(_)) {
                                continue;
                            }
                            if event.need_rescan() || pending.len() > 4096 {
                                for index in worker.indexes.values_mut() {
                                    index.refresh();
                                }
                                pending.clear();
                            } else {
                                if pending.is_empty() {
                                    pending_since = Instant::now();
                                }
                                let structural = changes_names(event.kind);
                                for path in event.paths {
                                    pending
                                        .entry(path)
                                        .and_modify(|value| *value |= structural)
                                        .or_insert(structural);
                                }
                            }
                        }
                        Ok(Job::Fs(Err(error))) => {
                            for index in worker.indexes.values_mut() {
                                index.refresh();
                            }
                            worker.send(Output::Error(format!("Folder watcher: {error}")));
                        }
                        Err(mpsc::RecvTimeoutError::Timeout) => {
                            if let Some((generation, gate, path, modified, column)) =
                                metadata.pop_front()
                            {
                                let cancelled = gate.load(Ordering::Relaxed) != generation;
                                let value =
                                    (!cancelled).then(|| crate::metadata::read(&path, column));
                                worker.send(Output::Metadata {
                                    path,
                                    modified,
                                    column,
                                    value,
                                });
                            }
                        }
                    }
                    if !pending.is_empty() && pending_since.elapsed() >= Duration::from_millis(200)
                    {
                        let changes: Vec<_> = pending.drain().collect();
                        for index in worker.indexes.values_mut() {
                            for (path, structural) in &changes {
                                if *structural && path.starts_with(&index.root) {
                                    index.change(path);
                                }
                            }
                        }
                        worker.send(Output::Changed(changes));
                        worker.publish_status();
                    }
                    if Instant::now() >= next_step {
                        let started = Instant::now();
                        worker.step();
                        // Target about 5% of one CPU core; foreground jobs can interrupt the wait.
                        next_step = Instant::now()
                            + (started.elapsed() * 19)
                                .clamp(Duration::from_millis(40), Duration::from_millis(500));
                    }
                }
                drop(watcher);
                unsafe {
                    windows_sys::Win32::System::Com::CoUninitialize();
                }
            })
            .expect("Cannot create I/O worker");
        Self {
            sender,
            receiver: results,
            images,
            thread: Some(thread),
            stopping,
        }
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        self.stopping.store(true, Ordering::Relaxed);
        let _ = self.sender.send(Job::Stop);
        // Requests are bounded/cooperative. Do not leave background writers
        // running after the application has released its single-instance lock.
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

struct Engine {
    main: usize,
    outputs: Sender<Output>,
    directory: PathBuf,
    indexes: HashMap<PathBuf, Index>,
    order: Vec<PathBuf>,
    cursor: usize,
    last_status: Option<(u64, bool, Option<String>)>,
}
fn changes_names(kind: notify::EventKind) -> bool {
    use notify::{EventKind, event::ModifyKind};
    // Win32 reports writes, size and timestamp changes as Modify(Any).
    matches!(
        kind,
        EventKind::Any
            | EventKind::Other
            | EventKind::Create(_)
            | EventKind::Remove(_)
            | EventKind::Modify(ModifyKind::Name(_))
    )
}
struct Background(bool);
impl Background {
    fn enter() -> Self {
        Self(unsafe { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) } != 0)
    }
}
impl Drop for Background {
    fn drop(&mut self) {
        if self.0 {
            unsafe {
                SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
            }
        }
    }
}
impl Engine {
    fn publish_status(&mut self) {
        let status = (
            self.indexes.values().map(Index::published_count).sum(),
            self.indexes.values().any(Index::preparing),
            self.order
                .iter()
                .filter_map(|root| self.indexes.get(root))
                .find_map(|index| {
                    index
                        .error
                        .as_ref()
                        .map(|error| format!("{}: {error}", index.root.display()))
                }),
        );
        if self.last_status.as_ref() != Some(&status) {
            self.send(Output::SearchStatus {
                count: status.0,
                preparing: status.1,
                error: status.2.clone(),
            });
            self.last_status = Some(status);
        }
    }
    fn send(&self, output: Output) {
        if self.outputs.send(output).is_ok() {
            unsafe {
                PostMessageW(self.main as _, WAKE, 0, 0);
            }
        }
    }
    fn list(
        &mut self,
        tab: &Tab,
        query: &str,
        sort: usize,
        descending: bool,
        cancelled: impl Fn() -> bool,
    ) -> (Vec<Row>, bool, Option<String>) {
        if tab.panel_type.eq_ignore_ascii_case("RecycleBin") {
            return match shell::recycle_rows(tab.show_file_extensions) {
                Ok(mut rows) => {
                    if !query.is_empty() {
                        rows.retain(|row| {
                            String::from_utf16_lossy(&row.name)
                                .to_lowercase()
                                .contains(&query.to_lowercase())
                        });
                    }
                    rows.sort_by(|a, b| ordinal(&a.name, &b.name));
                    (rows, false, None)
                }
                Err(error) => (Vec::new(), false, Some(error)),
            };
        }
        let root = Path::new(&tab.folder_path);
        let mut preparing = false;
        let paths: Result<Vec<PathBuf>, String> = if tab.pinned() {
            Ok(tab.pinned_items.iter().map(PathBuf::from).collect())
        } else if query.chars().count() >= 2 {
            if let Some(index) = self.indexes.get_mut(root) {
                preparing = index.preparing();
                index
                    .prepare_search()
                    .and_then(|()| index.search(query, tab.show_file_extensions, &cancelled))
            } else {
                preparing = true;
                Ok(Vec::new())
            }
        } else {
            fs::read_dir(root)
                .map_err(|e| format!("Cannot open {}: {e}", root.display()))
                .map(|entries| {
                    entries
                        .filter_map(Result::ok)
                        .map(|entry| entry.path())
                        .collect()
                })
        };
        let paths = match paths {
            Ok(paths) => paths,
            Err(error) => return (Vec::new(), preparing, Some(error)),
        };
        let needle = query.to_lowercase();
        let mut rows = Vec::with_capacity(paths.len());
        for path in paths {
            if cancelled() {
                return (Vec::new(), preparing, None);
            }
            if let Some(row) = Row::read(path, tab.show_file_extensions) {
                if !tab.show_hidden && row.hidden {
                    continue;
                }
                if (tab.pinned() || query.chars().count() < 2)
                    && !needle.is_empty()
                    && !String::from_utf16_lossy(&row.name)
                        .to_lowercase()
                        .contains(&needle)
                {
                    continue;
                }
                if tab.view_mode == "photos" && !row.photo() {
                    continue;
                }
                rows.push(row);
            }
        }
        if query.chars().count() < 2 || tab.pinned() {
            if sort >= 5 {
                let mut keyed = Vec::with_capacity(rows.len());
                for row in rows {
                    if cancelled() {
                        return (Vec::new(), preparing, None);
                    }
                    keyed.push((crate::metadata::read(&row.path, sort), row));
                }
                keyed.sort_by(|(a_key, a), (b_key, b)| {
                    b.directory
                        .cmp(&a.directory)
                        .then_with(|| {
                            let order = ordinal(a_key, b_key);
                            if descending { order.reverse() } else { order }
                        })
                        .then_with(|| ordinal(&a.name, &b.name))
                });
                return (
                    keyed.into_iter().map(|(_, row)| row).collect(),
                    preparing,
                    None,
                );
            }
            rows.sort_by(|a, b| {
                b.directory
                    .cmp(&a.directory)
                    .then_with(|| {
                        let order = match sort {
                            1 => a.kind.cmp(&b.kind),
                            2 => a.size.cmp(&b.size),
                            3 => a.modified.cmp(&b.modified),
                            4 => a.created.cmp(&b.created),
                            _ => ordinal(&a.name, &b.name),
                        };
                        if descending { order.reverse() } else { order }
                    })
                    .then_with(|| ordinal(&a.name, &b.name))
            });
        }
        (rows, preparing, None)
    }
    fn step(&mut self) {
        let _background = Background::enter();
        let mut publish = false;
        let length = self.order.len();
        for _ in 0..length {
            self.cursor %= length;
            let root = self.order[self.cursor].clone();
            self.cursor += 1;
            let Some(index) = self.indexes.get_mut(&root) else {
                continue;
            };
            if !index.has_work() {
                if let Err(error) = index.persist_if_due() {
                    self.send(Output::Error(format!("Search index: {error}")));
                }
                continue;
            }
            let started = Instant::now();
            let mut result = Ok(false);
            for _ in 0..16 {
                result = index.step();
                if result != Ok(false) || started.elapsed() >= Duration::from_millis(4) {
                    break;
                }
            }
            let notify = index.last_notification.elapsed() >= Duration::from_millis(800);
            if notify {
                index.last_notification = Instant::now();
            }
            publish = notify || result != Ok(false);
            if let Err(error) = &result {
                index.failed(error.clone());
            }
            match result {
                Ok(true) => self.send(Output::Indexed(root)),
                Ok(false) if notify && !index.complete => self.send(Output::Indexed(root)),
                Err(error) => self.send(Output::Error(format!("Search index: {error}"))),
                _ => {}
            }
            break;
        }
        let mut total: usize = self.indexes.values().map(Index::memory_bytes).sum();
        if total > 64 * 1024 * 1024 {
            for root in self.order.iter().rev() {
                if let Some(index) = self.indexes.get_mut(root) {
                    total -= index.memory_bytes();
                    index.trim_memory();
                    total += index.memory_bytes();
                }
                if total <= 64 * 1024 * 1024 {
                    break;
                }
            }
        }
        if publish {
            self.publish_status();
        }
    }
}
pub fn ordinal(left: &[u16], right: &[u16]) -> std::cmp::Ordering {
    unsafe {
        windows_sys::Win32::Globalization::CompareStringOrdinal(
            left.as_ptr(),
            left.len() as i32,
            right.as_ptr(),
            right.len() as i32,
            1,
        )
    }
    .cmp(&2)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn writes_do_not_reindex_names_but_creates_deletes_and_renames_do() {
        use notify::{EventKind, event::*};
        for kind in [
            EventKind::Modify(ModifyKind::Any),
            EventKind::Modify(ModifyKind::Data(DataChange::Any)),
            EventKind::Modify(ModifyKind::Metadata(MetadataKind::WriteTime)),
            EventKind::Access(AccessKind::Any),
        ] {
            assert!(!changes_names(kind), "{kind:?}");
        }
        for kind in [
            EventKind::Create(CreateKind::Any),
            EventKind::Remove(RemoveKind::Any),
            EventKind::Modify(ModifyKind::Name(RenameMode::From)),
            EventKind::Modify(ModifyKind::Name(RenameMode::To)),
            EventKind::Any,
        ] {
            assert!(changes_names(kind), "{kind:?}");
        }
    }
    #[test]
    fn status_aggregates_roots_and_does_not_repeat_unchanged_ready_notifications() {
        struct Fixture(PathBuf);
        impl Drop for Fixture {
            fn drop(&mut self) {
                let _ = fs::remove_dir_all(&self.0);
            }
        }
        let fixture = Fixture(
            std::env::temp_dir().join(format!("desktopplus-worker-{}", crate::model::new_id())),
        );
        let roots = vec![fixture.0.join("one"), fixture.0.join("two")];
        let (outputs, results) = mpsc::channel();
        let mut engine = Engine {
            main: 0,
            outputs,
            directory: fixture.0.join("cache"),
            indexes: HashMap::new(),
            order: roots.clone(),
            cursor: 0,
            last_status: None,
        };
        for root in &roots {
            fs::create_dir_all(root).unwrap();
            fs::write(root.join("report.txt"), "test").unwrap();
            engine
                .indexes
                .insert(root.clone(), Index::open(root.clone(), &engine.directory));
        }
        assert!(engine.indexes.get_mut(&roots[0]).unwrap().step().unwrap());
        engine.publish_status();
        assert!(matches!(
            results.try_recv().unwrap(),
            Output::SearchStatus {
                count: 1,
                preparing: true,
                error: None
            }
        ));
        assert!(engine.indexes.get_mut(&roots[1]).unwrap().step().unwrap());
        engine.publish_status();
        assert!(matches!(
            results.try_recv().unwrap(),
            Output::SearchStatus {
                count: 2,
                preparing: false,
                error: None
            }
        ));
        engine.indexes.get_mut(&roots[0]).unwrap().refresh();
        engine.publish_status();
        assert!(results.try_recv().is_err());
        engine
            .indexes
            .get_mut(&roots[1])
            .unwrap()
            .failed("test failure".into());
        engine.publish_status();
        assert!(matches!(
            results.try_recv().unwrap(),
            Output::SearchStatus {
                preparing: false,
                error: Some(_),
                ..
            }
        ));
        // Drop open files before removing the fixture on Windows.
        drop(engine);
    }
}
