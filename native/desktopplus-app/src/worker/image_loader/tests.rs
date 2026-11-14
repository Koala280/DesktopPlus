use super::*;

fn request(name: &str, visible: bool) -> ImageRequest {
    ImageRequest {
        generation: 0,
        gate: Arc::new(AtomicU64::new(0)),
        path: name.into(),
        modified: 0,
        spec: shell::ImageSpec {
            edge: 48,
            height: 48,
            thumbnail: false,
        },
        visible,
    }
}

#[test]
fn newest_visible_images_overtake_old_scrolls_and_prefetch() {
    let mut queue = Queue::default();
    queue.push(request("prefetch", false));
    queue.push(request("old viewport", true));
    queue.push(request("new viewport", true));
    assert_eq!(queue.pop().unwrap().path, Path::new("new viewport"));
    assert_eq!(queue.pop().unwrap().path, Path::new("old viewport"));
    assert_eq!(queue.pop().unwrap().path, Path::new("prefetch"));
    assert!(queue.pop().is_none());
}

#[test]
fn scrolling_into_prefetch_promotes_it_without_duplicate_extraction() {
    let mut queue = Queue::default();
    queue.push(request("other", false));
    queue.push(request("newly visible", false));
    queue.push(request("newly visible", true));
    assert_eq!(queue.pop().unwrap().path, Path::new("newly visible"));
    // Painting again while a provider is still running must not duplicate it.
    queue.push(request("newly visible", true));
    assert_eq!(queue.pop().unwrap().path, Path::new("other"));
    assert!(queue.pop().is_none());
}

#[test]
fn a_slow_provider_does_not_block_other_visible_images() {
    let (outputs, results) = mpsc::channel();
    let (entered, started) = mpsc::channel();
    let (release, wait) = mpsc::channel();
    let wait = Mutex::new(wait);
    let loader = ImageLoader::start(0, outputs, move |path, _| {
        if path == Path::new("slow") {
            entered.send(()).unwrap();
            // The timeout also makes failure cleanup bounded if an assertion
            // fails before the test releases this simulated shell provider.
            let _ = wait.lock().unwrap().recv_timeout(Duration::from_secs(5));
        }
        None
    });
    loader.request(request("slow", true));
    started.recv_timeout(Duration::from_secs(3)).unwrap();
    loader.request(request("fast", true));
    assert!(
        matches!(results.recv_timeout(Duration::from_secs(3)).unwrap(),
        Output::Image { path, cancelled: false, .. } if path == Path::new("fast"))
    );
    release.send(()).unwrap();
    assert!(
        matches!(results.recv_timeout(Duration::from_secs(3)).unwrap(),
        Output::Image { path, .. } if path == Path::new("slow"))
    );
}

#[test]
fn cancelled_tab_requests_release_pending_slots_without_calling_the_provider() {
    let (outputs, results) = mpsc::channel();
    let calls = Arc::new(AtomicU64::new(0));
    let called = calls.clone();
    let loader = ImageLoader::start(0, outputs, move |_, _| {
        called.fetch_add(1, Ordering::Relaxed);
        None
    });
    let stale = request("old tab", true);
    stale.gate.store(1, Ordering::Relaxed);
    loader.request(stale);
    assert!(matches!(
        results.recv_timeout(Duration::from_secs(3)).unwrap(),
        Output::Image {
            cancelled: true,
            bitmap: None,
            ..
        }
    ));
    assert_eq!(calls.load(Ordering::Relaxed), 0);
}
