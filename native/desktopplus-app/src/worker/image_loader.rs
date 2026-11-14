//! Shell image providers can be slow. Keep them off the file/search worker and
//! serve visible items before speculative requests from neighboring rows.
use super::*;
use std::sync::{Condvar, Mutex};

type Key = (PathBuf, u64, shell::ImageSpec);

pub struct ImageRequest {
    pub generation: u64,
    pub gate: Arc<AtomicU64>,
    pub path: PathBuf,
    pub modified: u64,
    pub spec: shell::ImageSpec,
    pub visible: bool,
}

impl ImageRequest {
    fn key(&self) -> Key {
        (self.path.clone(), self.modified, self.spec)
    }

    fn cancelled(&self) -> bool {
        self.gate.load(Ordering::Relaxed) != self.generation
    }
}

#[derive(Default)]
struct Queue {
    visible: VecDeque<ImageRequest>,
    prefetch: VecDeque<ImageRequest>,
    active: HashSet<Key>,
    stopped: bool,
}

impl Queue {
    fn push(&mut self, request: ImageRequest) {
        let key = request.key();
        if self.stopped || self.active.contains(&key) {
            return;
        }
        for queue in [&mut self.visible, &mut self.prefetch] {
            if let Some(index) = queue.iter().position(|item| item.key() == key) {
                queue.remove(index);
            }
        }
        if request.visible {
            // A fast scroll must not wait behind an older viewport's requests.
            self.visible.push_front(request);
        } else {
            self.prefetch.push_back(request);
        }
    }

    fn pop(&mut self) -> Option<ImageRequest> {
        let request = self
            .visible
            .pop_front()
            .or_else(|| self.prefetch.pop_front())?;
        self.active.insert(request.key());
        Some(request)
    }
}

pub struct ImageLoader {
    queue: Arc<(Mutex<Queue>, Condvar)>,
    threads: Vec<std::thread::JoinHandle<()>>,
}

impl ImageLoader {
    pub fn new(main: usize, outputs: Sender<Output>) -> Self {
        Self::start(main, outputs, shell::image)
    }

    fn start(
        main: usize,
        outputs: Sender<Output>,
        load: impl Fn(&Path, shell::ImageSpec) -> Option<shell::Bitmap> + Send + Sync + 'static,
    ) -> Self {
        let queue = Arc::new((Mutex::new(Queue::default()), Condvar::new()));
        let load = Arc::new(load);
        let threads = (0..2)
            .map(|index| {
                let queue = queue.clone();
                let outputs = outputs.clone();
                let load = load.clone();
                std::thread::Builder::new()
                    .name(format!("DesktopPlus images {index}"))
                    .spawn(move || {
                        use windows_sys::Win32::System::Com::*;
                        let initialized = unsafe {
                            CoInitializeEx(std::ptr::null(), COINIT_APARTMENTTHREADED as u32) >= 0
                        };
                        loop {
                            let request = {
                                let (lock, ready) = &*queue;
                                let mut state = lock.lock().unwrap();
                                loop {
                                    if state.stopped {
                                        break None;
                                    }
                                    if let Some(request) = state.pop() {
                                        break Some(request);
                                    }
                                    state = ready.wait(state).unwrap();
                                }
                            };
                            let Some(request) = request else { break };
                            let bitmap = if initialized && !request.cancelled() {
                                load(&request.path, request.spec)
                            } else {
                                None
                            };
                            let cancelled = request.cancelled();
                            queue.0.lock().unwrap().active.remove(&request.key());
                            if outputs
                                .send(Output::Image {
                                    path: request.path,
                                    modified: request.modified,
                                    spec: request.spec,
                                    bitmap: if cancelled { None } else { bitmap },
                                    cancelled,
                                })
                                .is_ok()
                            {
                                unsafe {
                                    PostMessageW(main as _, WAKE, 0, 0);
                                }
                            }
                        }
                        if initialized {
                            unsafe {
                                CoUninitialize();
                            }
                        }
                    })
                    .expect("Cannot create image worker")
            })
            .collect();
        Self { queue, threads }
    }

    pub fn request(&self, request: ImageRequest) {
        self.queue.0.lock().unwrap().push(request);
        self.queue.1.notify_one();
    }
}

impl Drop for ImageLoader {
    fn drop(&mut self) {
        {
            let mut state = self.queue.0.lock().unwrap();
            state.stopped = true;
            state.visible.clear();
            state.prefetch.clear();
        }
        self.queue.1.notify_all();
        for thread in self.threads.drain(..) {
            let _ = thread.join();
        }
    }
}

#[cfg(test)]
mod tests;
