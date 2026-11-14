//! Synchronous WinHTTP runs only on the update worker. TLS and proxy settings
//! come from Windows; redirects may not downgrade HTTPS to HTTP.
use crate::shell::wide;
use std::{
    ffi::c_void,
    ptr::{null, null_mut},
    time::{Duration, Instant},
};
use windows_sys::Win32::Networking::WinHttp::*;

struct Handle(*mut c_void);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            WinHttpCloseHandle(self.0);
        }
    }
}
fn handle(value: *mut c_void) -> Result<Handle, String> {
    if value.is_null() {
        Err(crate::shell::error_text())
    } else {
        Ok(Handle(value))
    }
}
fn check(value: i32) -> Result<(), String> {
    if value == 0 {
        Err(crate::shell::error_text())
    } else {
        Ok(())
    }
}

pub(super) fn get(url: &str, limit: usize) -> Result<Vec<u8>, String> {
    let (host, path) = url
        .strip_prefix("https://")
        .and_then(|url| url.split_once('/'))
        .ok_or("Update downloads require HTTPS.")?;
    if !matches!(host, "api.github.com" | "github.com") {
        return Err("Unexpected update download host.".into());
    }
    unsafe {
        let session = handle(WinHttpOpen(
            wide(concat!("DesktopPlus/", env!("DESKTOPPLUS_VERSION"))).as_ptr(),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            null(),
            null(),
            0,
        ))?;
        check(WinHttpSetTimeouts(
            session.0, 10_000, 15_000, 30_000, 30_000,
        ))?;
        let connection = handle(WinHttpConnect(session.0, wide(host).as_ptr(), 443, 0))?;
        let request = handle(WinHttpOpenRequest(
            connection.0,
            wide("GET").as_ptr(),
            wide(format!("/{path}")).as_ptr(),
            null(),
            null(),
            null(),
            WINHTTP_FLAG_SECURE,
        ))?;
        let policy = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
        check(WinHttpSetOption(
            request.0,
            WINHTTP_OPTION_REDIRECT_POLICY,
            (&policy as *const u32).cast(),
            size_of::<u32>() as u32,
        ))?;
        let headers = wide(if host == "api.github.com" {
            "Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n"
        } else {
            "Accept: application/octet-stream\r\n"
        });
        check(WinHttpSendRequest(
            request.0,
            headers.as_ptr(),
            (headers.len() - 1) as u32,
            null(),
            0,
            0,
            0,
        ))?;
        check(WinHttpReceiveResponse(request.0, null_mut()))?;
        let mut status = 0u32;
        let mut length = size_of::<u32>() as u32;
        check(WinHttpQueryHeaders(
            request.0,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            null(),
            (&mut status as *mut u32).cast(),
            &mut length,
            null_mut(),
        ))?;
        if status != 200 {
            return Err(format!("Update server returned HTTP {status}."));
        }
        let start = Instant::now();
        let mut bytes = Vec::new();
        let mut buffer = [0u8; 65536];
        loop {
            let mut count = 0;
            check(WinHttpReadData(
                request.0,
                buffer.as_mut_ptr().cast(),
                buffer.len() as u32,
                &mut count,
            ))?;
            if count == 0 {
                return Ok(bytes);
            }
            if bytes.len() + count as usize > limit {
                return Err("Update download exceeds the size limit.".into());
            }
            if start.elapsed() > Duration::from_secs(300) {
                return Err("Update download timed out.".into());
            }
            bytes.extend_from_slice(&buffer[..count as usize]);
        }
    }
}
