use super::*;

pub enum Field {
    Text(String, String),
    Check(String, bool),
    Choice(String, Vec<String>, usize),
}

/// Keep painting and Windows shell messages responsive during a transactional
/// backup/restore. Disable application windows until the result is available.
pub fn work<T: Send + 'static>(
    owner: HWND,
    title: &str,
    operation: impl FnOnce() -> Result<T, String> + Send + 'static,
) -> Result<T, String> {
    unsafe {
        let instance = GetModuleHandleW(null());
        let class = wide("DesktopPlus.Rust.Progress");
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(work_proc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_WAIT),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        });
        let hwnd = CreateWindowExW(
            WS_EX_DLGMODALFRAME,
            class.as_ptr(),
            wide(crate::locale::text(title)).as_ptr(),
            WS_OVERLAPPED | WS_CAPTION,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            500,
            130,
            owner,
            null_mut(),
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(shell::error_text());
        }
        theme_frame(hwnd);
        let label = child(hwnd, "STATIC", "Please wait…", 0, 0);
        MoveWindow(label, 24, 24, 440, 32, 1);
        let mut disabled = Vec::<HWND>::new();
        unsafe extern "system" fn collect(hwnd: HWND, data: LPARAM) -> i32 {
            if unsafe { IsWindowEnabled(hwnd) } != 0 {
                unsafe { &mut *(data as *mut Vec<HWND>) }.push(hwnd);
            }
            1
        }
        EnumThreadWindows(
            windows_sys::Win32::System::Threading::GetCurrentThreadId(),
            Some(collect),
            &mut disabled as *mut _ as isize,
        );
        disabled.retain(|window| *window != hwnd);
        for window in &disabled {
            EnableWindow(*window, 0);
        }
        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let notify = hwnd as usize;
        let thread = std::thread::Builder::new()
            .name("DesktopPlus recovery".into())
            .spawn(move || {
                let initialized = windows_sys::Win32::System::Com::CoInitializeEx(
                    null(),
                    windows_sys::Win32::System::Com::COINIT_APARTMENTTHREADED as u32,
                ) >= 0;
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(operation))
                    .unwrap_or_else(|_| Err("Recovery operation failed.".into()));
                if initialized {
                    windows_sys::Win32::System::Com::CoUninitialize();
                }
                let _ = sender.send(result);
                PostMessageW(notify as _, WM_APP + 66, 0, 0);
            });
        let result = match thread {
            Ok(thread) => {
                let result = loop {
                    match receiver.try_recv() {
                        Ok(result) => break result,
                        Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                            break Err("Recovery worker disconnected.".into());
                        }
                        _ => {}
                    }
                    let mut message = MSG::default();
                    if GetMessageW(&mut message, null_mut(), 0, 0) <= 0 {
                        PostQuitMessage(message.wParam as i32);
                        break receiver
                            .recv()
                            .unwrap_or_else(|_| Err("Recovery worker disconnected.".into()));
                    }
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                };
                let _ = thread.join();
                result
            }
            Err(error) => Err(error.to_string()),
        };
        DestroyWindow(hwnd);
        for window in disabled {
            if IsWindow(window) != 0 {
                EnableWindow(window, 1);
            }
        }
        SetForegroundWindow(owner);
        result
    }
}
unsafe extern "system" fn work_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_NCDESTROY {
        chrome::release(hwnd);
    }
    if message == WM_CLOSE {
        return 0;
    }
    if matches!(message, WM_ERASEBKGND | WM_CTLCOLORSTATIC) {
        return unsafe { window_proc(hwnd, message, wparam, lparam) };
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}
pub fn edit(owner: HWND, title: &str, fields: Vec<Field>) -> Option<Vec<String>> {
    let fields = fields
        .into_iter()
        .map(|field| match field {
            Field::Text(label, value) => form::Input::Text(label, value),
            Field::Check(label, value) => form::Input::Check(label, value),
            Field::Choice(label, values, selected) => form::Input::Choice(label, values, selected),
        })
        .collect();
    form::show(
        owner,
        title,
        &crate::locale::text("Make it work your way."),
        vec![form::Page {
            name: "General",
            fields,
        }],
        &|_| Ok(()),
    )
    .map(|answer| answer.values)
}
