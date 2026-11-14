use std::ffi::{OsStr, OsString};
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Path, PathBuf};
use std::ptr::{null, null_mut};
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::System::{Com::*, DataExchange::*, Memory::*};
use windows_sys::Win32::UI::{Shell::*, WindowsAndMessaging::*};

pub fn wide(text: impl AsRef<OsStr>) -> Vec<u16> {
    text.as_ref().encode_wide().chain(Some(0)).collect()
}
pub fn path_wide(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().collect()
}
pub fn from_wide(value: &[u16]) -> PathBuf {
    OsString::from_wide(value).into()
}

pub fn error_text() -> String {
    std::io::Error::last_os_error().to_string()
}

pub fn desktop() -> PathBuf {
    let mut path = null_mut();
    if unsafe { SHGetKnownFolderPath(&FOLDERID_Desktop, 0, null_mut(), &mut path) } >= 0
        && !path.is_null()
    {
        let mut length = 0;
        unsafe {
            while *path.add(length) != 0 {
                length += 1;
            }
        }
        let result = from_wide(unsafe { std::slice::from_raw_parts(path, length) });
        unsafe {
            CoTaskMemFree(path.cast());
        }
        result
    } else {
        std::env::var_os("USERPROFILE")
            .map(PathBuf::from)
            .unwrap_or_default()
            .join("Desktop")
    }
}

pub fn open(owner: HWND, path: &Path) -> Result<(), String> {
    let path = wide(path);
    let result =
        unsafe { ShellExecuteW(owner, null(), path.as_ptr(), null(), null(), SW_SHOWNORMAL) }
            as isize;
    if result <= 32 {
        Err(format!(
            "Windows could not open this item (error {result})."
        ))
    } else {
        Ok(())
    }
}
pub fn reveal(owner: HWND, path: &Path) -> Result<(), String> {
    let mut pidl = null_mut();
    if unsafe { SHParseDisplayName(wide(path).as_ptr(), null_mut(), &mut pidl, 0, null_mut()) } < 0
    {
        return open(owner, path.parent().unwrap_or(path));
    }
    let result = unsafe { SHOpenFolderAndSelectItems(pidl, 0, null(), 0) };
    unsafe {
        CoTaskMemFree(pidl.cast());
    }
    if result < 0 {
        Err(format!("Cannot reveal item ({result:#x})."))
    } else {
        Ok(())
    }
}
pub fn properties(owner: HWND, path: &Path) -> Result<(), String> {
    let path = wide(path);
    let verb = wide("properties");
    let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of_val(&info) as u32;
    info.fMask = SEE_MASK_INVOKEIDLIST;
    info.hwnd = owner;
    info.lpVerb = verb.as_ptr();
    info.lpFile = path.as_ptr();
    info.nShow = SW_SHOWNORMAL;
    if unsafe { ShellExecuteExW(&mut info) } == 0 {
        Err(error_text())
    } else {
        Ok(())
    }
}

pub fn choose_folder(owner: HWND) -> Result<Option<PathBuf>, String> {
    choose_path(owner, true)
}
pub fn choose_path(owner: HWND, folder: bool) -> Result<Option<PathBuf>, String> {
    use windows::{
        Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance},
        Win32::UI::Shell::*,
        core::Interface,
    };
    unsafe {
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
            .map_err(|e| e.to_string())?;
        dialog
            .SetOptions(
                (if folder {
                    FOS_PICKFOLDERS
                } else {
                    FOS_FILEMUSTEXIST
                }) | FOS_FORCEFILESYSTEM
                    | FOS_PATHMUSTEXIST,
            )
            .map_err(|e| e.to_string())?;
        if let Err(error) = dialog.Show(Some(windows::Win32::Foundation::HWND(owner))) {
            if error.code().0 as u32 == 0x800704c7 {
                return Ok(None);
            }
            return Err(error.to_string());
        }
        let item = dialog.GetResult().map_err(|e| e.to_string())?;
        let text = item
            .GetDisplayName(SIGDN_FILESYSPATH)
            .map_err(|e| e.to_string())?;
        let result = PathBuf::from(OsString::from_wide(text.as_wide()));
        windows::Win32::System::Com::CoTaskMemFree(Some(text.as_ptr().cast()));
        let _ = dialog.cast::<IFileDialog>();
        Ok(Some(result))
    }
}

#[derive(Clone, Copy, Debug)]
pub enum FileAction {
    Copy,
    Move,
    Recycle,
}

pub fn transfer(
    owner: HWND,
    action: FileAction,
    sources: &[PathBuf],
    destination: Option<&Path>,
) -> Result<(), String> {
    use windows::{
        Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance},
        Win32::UI::Shell::*,
        core::PCWSTR,
    };
    unsafe {
        let operation: IFileOperation =
            CoCreateInstance(&FileOperation, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| e.to_string())?;
        operation
            .SetOwnerWindow(windows::Win32::Foundation::HWND(owner))
            .map_err(|e| e.to_string())?;
        // Keep Windows' conflict/permission dialogs and undo/recycle behavior.
        operation
            .SetOperationFlags(
                FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOFX_ADDUNDORECORD | FOFX_RECYCLEONDELETE,
            )
            .map_err(|e| e.to_string())?;
        let target: Option<IShellItem> = destination
            .map(|path| SHCreateItemFromParsingName(PCWSTR(wide(path).as_ptr()), None))
            .transpose()
            .map_err(|e| e.to_string())?;
        for source in sources {
            let source: IShellItem =
                SHCreateItemFromParsingName(PCWSTR(wide(source).as_ptr()), None)
                    .map_err(|e| e.to_string())?;
            match action {
                FileAction::Copy => operation.CopyItem(
                    &source,
                    target.as_ref().ok_or("No destination")?,
                    PCWSTR::null(),
                    None,
                ),
                FileAction::Move => operation.MoveItem(
                    &source,
                    target.as_ref().ok_or("No destination")?,
                    PCWSTR::null(),
                    None,
                ),
                FileAction::Recycle => operation.DeleteItem(&source, None),
            }
            .map_err(|e| e.to_string())?;
        }
        operation.PerformOperations().map_err(|e| e.to_string())?;
        if operation
            .GetAnyOperationsAborted()
            .map_err(|e| e.to_string())?
            .as_bool()
        {
            return Err("File operation cancelled.".into());
        }
        Ok(())
    }
}

pub fn clipboard_set(owner: HWND, paths: &[PathBuf], cut: bool) -> Result<(), String> {
    let mut names: Vec<u16> = paths.iter().flat_map(wide).collect();
    names.push(0);
    let size = std::mem::size_of::<DROPFILES>() + names.len() * 2;
    unsafe {
        if OpenClipboard(owner) == 0 {
            return Err(error_text());
        }
        struct Close;
        impl Drop for Close {
            fn drop(&mut self) {
                unsafe {
                    CloseClipboard();
                }
            }
        }
        let _close = Close;
        let memory = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, size);
        if memory.is_null() {
            return Err(error_text());
        }
        let pointer = GlobalLock(memory).cast::<u8>();
        if pointer.is_null() {
            GlobalFree(memory);
            return Err(error_text());
        }
        let header = pointer.cast::<DROPFILES>();
        (*header).pFiles = std::mem::size_of::<DROPFILES>() as u32;
        (*header).fWide = 1;
        std::ptr::copy_nonoverlapping(
            names.as_ptr().cast::<u8>(),
            pointer.add(std::mem::size_of::<DROPFILES>()),
            names.len() * 2,
        );
        GlobalUnlock(memory);
        if EmptyClipboard() == 0 || SetClipboardData(15, memory).is_null() {
            GlobalFree(memory);
            return Err(error_text());
        }
        let format = RegisterClipboardFormatW(wide("Preferred DropEffect").as_ptr());
        let effect = GlobalAlloc(GMEM_MOVEABLE, 4);
        if !effect.is_null() {
            let pointer = GlobalLock(effect).cast::<u32>();
            if !pointer.is_null() {
                *pointer = if cut { 2 } else { 1 };
                GlobalUnlock(effect);
                if SetClipboardData(format, effect).is_null() {
                    GlobalFree(effect);
                }
            } else {
                GlobalFree(effect);
            }
        }
        Ok(())
    }
}

pub fn clipboard_get(owner: HWND) -> Result<(Vec<PathBuf>, bool), String> {
    unsafe {
        if OpenClipboard(owner) == 0 {
            return Err(error_text());
        }
        struct Close;
        impl Drop for Close {
            fn drop(&mut self) {
                unsafe {
                    CloseClipboard();
                }
            }
        }
        let _close = Close;
        let data = GetClipboardData(15);
        if data.is_null() {
            return Ok((Vec::new(), false));
        }
        let files = dropped_paths(data);
        let format = RegisterClipboardFormatW(wide("Preferred DropEffect").as_ptr());
        let effect = GetClipboardData(format);
        let mut cut = false;
        if !effect.is_null() && GlobalSize(effect) >= 4 {
            let value = GlobalLock(effect).cast::<u32>();
            if !value.is_null() {
                cut = *value & 2 != 0;
                GlobalUnlock(effect);
            }
        }
        Ok((files, cut))
    }
}

pub fn dropped_paths(drop: HDROP) -> Vec<PathBuf> {
    let count = unsafe { DragQueryFileW(drop, u32::MAX, null_mut(), 0) };
    (0..count)
        .map(|i| {
            let length = unsafe { DragQueryFileW(drop, i, null_mut(), 0) };
            let mut text = vec![0; length as usize + 1];
            unsafe {
                DragQueryFileW(drop, i, text.as_mut_ptr(), text.len() as u32);
            }
            from_wide(&text[..length as usize])
        })
        .collect()
}

pub fn create_shortcut(source: &Path, destination: &Path) -> Result<(), String> {
    use windows::{
        Win32::System::Com::*,
        Win32::UI::Shell::*,
        core::{Interface, PCWSTR},
    };
    unsafe {
        let link: IShellLinkW =
            CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).map_err(|e| e.to_string())?;
        link.SetPath(PCWSTR(wide(source).as_ptr()))
            .map_err(|e| e.to_string())?;
        if let Some(parent) = source.parent() {
            link.SetWorkingDirectory(PCWSTR(wide(parent).as_ptr()))
                .map_err(|e| e.to_string())?;
        }
        let file: IPersistFile = link.cast().map_err(|e| e.to_string())?;
        file.Save(PCWSTR(wide(destination).as_ptr()), true)
            .map_err(|e| e.to_string())
    }
}

pub fn startup(enabled: bool) -> Result<(), String> {
    crate::startup::configure(enabled)
}

thread_local! {
    static ACTIVE_MENU: std::cell::RefCell<Option<windows::Win32::UI::Shell::IContextMenu3>> = const { std::cell::RefCell::new(None) };
}
pub fn menu_message(message: u32, wparam: usize, lparam: isize) -> Option<isize> {
    if !matches!(
        message,
        WM_INITMENUPOPUP | WM_DRAWITEM | WM_MEASUREITEM | WM_MENUCHAR
    ) {
        return None;
    }
    let menu = ACTIVE_MENU.with(|menu| menu.borrow().clone())?;
    let mut result = windows::Win32::Foundation::LRESULT(0);
    unsafe {
        menu.HandleMenuMsg2(
            message,
            windows::Win32::Foundation::WPARAM(wparam),
            windows::Win32::Foundation::LPARAM(lparam),
            Some(&mut result),
        )
        .ok()?;
    }
    Some(result.0)
}

pub fn context_menu(owner: HWND, path: &Path, point: POINT) -> Result<(), String> {
    use windows::{
        Win32::{
            Foundation::HWND as Window,
            UI::{Shell::*, WindowsAndMessaging::HMENU as Menu},
        },
        core::{Interface, PCSTR, PCWSTR},
    };
    unsafe {
        let mut pidl = null_mut();
        SHParseDisplayName(PCWSTR(wide(path).as_ptr()), None, &mut pidl, 0, None)
            .map_err(|e| e.to_string())?;
        let result = (|| {
            let mut child = null_mut();
            let parent: IShellFolder =
                SHBindToParent(pidl, Some(&mut child)).map_err(|e| e.to_string())?;
            let context: IContextMenu = parent
                .GetUIObjectOf(Window(owner), &[child], None)
                .map_err(|e| e.to_string())?;
            let menu = CreatePopupMenu();
            if menu.is_null() {
                return Err(error_text());
            }
            let queried = context.QueryContextMenu(Menu(menu), 0, 1, 0x7fff, CMF_NORMAL);
            if queried.is_err() {
                DestroyMenu(menu);
                return Err(format!("Windows context menu failed ({queried:?})."));
            }
            ACTIVE_MENU.with(|active| *active.borrow_mut() = context.cast().ok());
            let chosen = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.x,
                point.y,
                0,
                owner,
                null(),
            );
            ACTIVE_MENU.with(|active| *active.borrow_mut() = None);
            DestroyMenu(menu);
            if chosen > 0 {
                let info = CMINVOKECOMMANDINFO {
                    cbSize: std::mem::size_of::<CMINVOKECOMMANDINFO>() as u32,
                    hwnd: Window(owner),
                    lpVerb: PCSTR((chosen as usize - 1) as *const u8),
                    nShow: SW_SHOWNORMAL,
                    ..Default::default()
                };
                context.InvokeCommand(&info).map_err(|e| e.to_string())?;
            }
            Ok(())
        })();
        windows::Win32::System::Com::CoTaskMemFree(Some(pidl.cast()));
        result
    }
}

pub struct Bitmap(pub isize);
#[derive(Clone, Copy, Debug, Hash, Eq, PartialEq)]
pub struct ImageSpec {
    pub edge: i32,
    pub height: i32,
    pub thumbnail: bool,
}
impl Drop for Bitmap {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Graphics::Gdi::DeleteObject(self.0 as _);
        }
    }
}
fn shortcut_image_target(path: &Path) -> Option<PathBuf> {
    if !path.extension()?.eq_ignore_ascii_case("lnk") {
        return None;
    }
    use windows::{
        Win32::{System::Com::*, UI::Shell::*},
        core::{Interface, PCWSTR},
    };
    unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).ok()?;
        let file: IPersistFile = link.cast().ok()?;
        file.Load(PCWSTR(wide(path).as_ptr()), STGM_READ).ok()?;
        let mut icon = vec![0u16; 32768];
        let mut index = 0;
        link.GetIconLocation(&mut icon, &mut index).ok()?;
        let length = icon.iter().position(|v| *v == 0).unwrap_or(icon.len());
        let icon_path = from_wide(&icon[..length]);
        // Keep existing custom icons (including environment-based locations).
        // A stale installer icon may disappear while the actual EXE survives.
        if length != 0 && (icon_path.exists() || icon_path.to_string_lossy().contains('%')) {
            return None;
        }
        let mut target = vec![0u16; 32768];
        link.GetPath(&mut target, null_mut(), SLGP_RAWPATH.0 as u32)
            .ok()?;
        let length = target.iter().position(|v| *v == 0).unwrap_or(target.len());
        let target = from_wide(&target[..length]);
        (length != 0 && target.exists()).then_some(target)
    }
}
pub fn image(path: &Path, spec: ImageSpec) -> Option<Bitmap> {
    use windows::{
        Win32::{
            Foundation::SIZE,
            Graphics::{Gdi as G, Imaging::*},
            UI::Shell::*,
        },
        core::PCWSTR,
    };
    use windows_sys::Win32::Graphics::Gdi::*;
    unsafe {
        // Ask the shell for the original shortcut/application image at a larger
        // resolution, then downsample once to the current zoom and monitor DPI.
        let target = shortcut_image_target(path);
        let path = target.as_deref().unwrap_or(path);
        let factory: IShellItemImageFactory =
            SHCreateItemFromParsingName(PCWSTR(wide(path).as_ptr()), None).ok()?;
        let flags = if spec.thumbnail {
            SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK | SIIGBF_CROPTOSQUARE
        } else {
            SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK
        };
        let requested = (spec.edge * 2).clamp(32, 1024);
        let source = Bitmap(
            factory
                .GetImage(
                    SIZE {
                        cx: requested,
                        cy: requested,
                    },
                    flags,
                )
                .ok()?
                .0 as isize,
        );
        let wic: IWICImagingFactory = windows::Win32::System::Com::CoCreateInstance(
            &CLSID_WICImagingFactory,
            None,
            windows::Win32::System::Com::CLSCTX_INPROC_SERVER,
        )
        .ok()?;
        let bitmap = wic
            .CreateBitmapFromHBITMAP(
                G::HBITMAP(source.0 as _),
                G::HPALETTE::default(),
                WICBitmapUsePremultipliedAlpha,
            )
            .ok()?;
        let mut width = 0;
        let mut height = 0;
        bitmap.GetSize(&mut width, &mut height).ok()?;
        if width == 0 || height == 0 {
            return None;
        }
        let factor = (spec.edge as f64 / width as f64).min(spec.edge as f64 / height as f64);
        let out_width = (width as f64 * factor).round().max(1.0) as u32;
        let out_height = (height as f64 * factor).round().max(1.0) as u32;
        let scaler = wic.CreateBitmapScaler().ok()?;
        scaler
            .Initialize(
                &bitmap,
                out_width,
                out_height,
                WICBitmapInterpolationModeFant,
            )
            .ok()?;
        let converter = wic.CreateFormatConverter().ok()?;
        converter
            .Initialize(
                &scaler,
                &GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherTypeNone,
                None,
                0.0,
                WICBitmapPaletteTypeCustom,
            )
            .ok()?;
        let mut pixels = vec![0u8; (out_width * out_height * 4) as usize];
        converter
            .CopyPixels(null(), out_width * 4, &mut pixels)
            .ok()?;
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: spec.edge,
                biHeight: -spec.height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut bits = null_mut();
        let result = CreateDIBSection(null_mut(), &info, DIB_RGB_COLORS, &mut bits, null_mut(), 0);
        if result.is_null() {
            return None;
        }
        let target =
            std::slice::from_raw_parts_mut(bits as *mut u8, (spec.edge * spec.height * 4) as usize);
        target.fill(0);
        let x = (spec.edge - out_width as i32) / 2;
        let y = (spec.height - out_height as i32) / 2;
        for row in 0..out_height as usize {
            let start = ((y as usize + row) * spec.edge as usize + x as usize) * 4;
            let source = row * out_width as usize * 4;
            target[start..start + out_width as usize * 4]
                .copy_from_slice(&pixels[source..source + out_width as usize * 4]);
        }
        Some(Bitmap(result as isize))
    }
}

pub fn recycle_rows(extensions: bool) -> Result<Vec<crate::worker::Row>, String> {
    use windows::{Win32::UI::Shell::*, core::PCWSTR};
    unsafe {
        let mut pidl = null_mut();
        SHParseDisplayName(
            PCWSTR(wide("shell:RecycleBinFolder").as_ptr()),
            None,
            &mut pidl,
            0,
            None,
        )
        .map_err(|e| e.to_string())?;
        let result = (|| {
            let desktop = SHGetDesktopFolder().map_err(|e| e.to_string())?;
            let folder: IShellFolder = desktop
                .BindToObject(pidl, None)
                .map_err(|e| e.to_string())?;
            let mut enumerator = None;
            folder
                .EnumObjects(
                    windows::Win32::Foundation::HWND(null_mut()),
                    (SHCONTF_FOLDERS.0 | SHCONTF_NONFOLDERS.0) as u32,
                    &mut enumerator,
                )
                .ok()
                .map_err(|e| e.to_string())?;
            let Some(enumerator) = enumerator else {
                return Ok(Vec::new());
            };
            let mut result = Vec::new();
            loop {
                let mut child = [null_mut()];
                let mut count = 0;
                if enumerator.Next(&mut child, Some(&mut count)).is_err() || count == 0 {
                    break;
                }
                let absolute = ILCombine(Some(pidl), Some(child[0]));
                if !absolute.is_null() {
                    let item: Result<IShellItem, _> = SHCreateItemFromIDList(absolute);
                    if let Ok(item) = item {
                        let get = |kind| -> Option<PathBuf> {
                            let value = item.GetDisplayName(kind).ok()?;
                            let path = PathBuf::from(OsString::from_wide(value.as_wide()));
                            windows::Win32::System::Com::CoTaskMemFree(Some(value.as_ptr().cast()));
                            Some(path)
                        };
                        if let (Some(parsing), Some(name)) =
                            (get(SIGDN_DESKTOPABSOLUTEPARSING), get(SIGDN_NORMALDISPLAY))
                        {
                            let physical = get(SIGDN_FILESYSPATH);
                            let metadata = physical
                                .as_ref()
                                .and_then(|path| path.symlink_metadata().ok());
                            use std::os::windows::fs::MetadataExt;
                            let directory = metadata.as_ref().is_some_and(|meta| meta.is_dir());
                            // Some providers return the original absolute path for
                            // NORMALDISPLAY. The tile label should only be its name.
                            let leaf = name.file_name().unwrap_or(name.as_os_str());
                            let display = if !extensions && !directory {
                                Path::new(leaf).file_stem().unwrap_or(leaf)
                            } else {
                                leaf
                            };
                            result.push(crate::worker::Row {
                                parent_navigation: false,
                                path: parsing,
                                name: display.encode_wide().collect(),
                                kind: if directory { "Folder" } else { "Deleted item" }
                                    .encode_utf16()
                                    .collect(),
                                directory,
                                size: metadata.as_ref().map_or(0, |meta| meta.len()),
                                modified: metadata
                                    .as_ref()
                                    .map_or(0, |meta| meta.last_write_time()),
                                created: metadata.as_ref().map_or(0, |meta| meta.creation_time()),
                                hidden: false,
                            });
                        }
                    }
                    windows::Win32::System::Com::CoTaskMemFree(Some(absolute.cast()));
                }
                windows::Win32::System::Com::CoTaskMemFree(Some(child[0].cast()));
            }
            Ok(result)
        })();
        windows::Win32::System::Com::CoTaskMemFree(Some(pidl.cast()));
        result
    }
}

pub fn drag_out(
    owner: HWND,
    paths: &[PathBuf],
    handle_drop: impl Fn() -> bool + 'static,
) -> Result<(), String> {
    use windows::Win32::Foundation::{
        DRAGDROP_S_CANCEL, DRAGDROP_S_DROP, DRAGDROP_S_USEDEFAULTCURSORS,
    };
    use windows::{
        Win32::{
            System::{Com::IDataObject, Ole::*, SystemServices::*},
            UI::Shell::*,
        },
        core::{BOOL, HRESULT, PCWSTR, implement},
    };
    #[implement(IDropSource)]
    struct Source {
        handle_drop: Box<dyn Fn() -> bool>,
    }
    impl IDropSource_Impl for Source_Impl {
        fn QueryContinueDrag(&self, escape: BOOL, keys: MODIFIERKEYS_FLAGS) -> HRESULT {
            if escape.as_bool() {
                DRAGDROP_S_CANCEL
            } else if keys & MK_LBUTTON == MODIFIERKEYS_FLAGS(0) {
                // Internal panel drops are resolved before the Shell can post
                // WM_DROPFILES, including no-op drops back onto the source.
                if (self.handle_drop)() {
                    DRAGDROP_S_CANCEL
                } else {
                    DRAGDROP_S_DROP
                }
            } else {
                HRESULT(0)
            }
        }
        fn GiveFeedback(&self, _effect: DROPEFFECT) -> HRESULT {
            DRAGDROP_S_USEDEFAULTCURSORS
        }
    }
    unsafe {
        let mut pidls = Vec::new();
        for path in paths {
            let mut pidl = null_mut();
            match SHParseDisplayName(PCWSTR(wide(path).as_ptr()), None, &mut pidl, 0, None) {
                Ok(()) => pidls.push(pidl),
                Err(error) => {
                    for pidl in pidls {
                        windows::Win32::System::Com::CoTaskMemFree(Some(pidl.cast()));
                    }
                    return Err(error.to_string());
                }
            }
        }
        let result = (|| {
            let pointers: Vec<_> = pidls.iter().map(|pidl| *pidl as *const _).collect();
            let items = SHCreateShellItemArrayFromIDLists(&pointers).map_err(|e| e.to_string())?;
            let data: IDataObject = items
                .BindToHandler(None, &BHID_DataObject)
                .map_err(|e| e.to_string())?;
            let source: IDropSource = Source {
                handle_drop: Box::new(handle_drop),
            }
            .into();
            let mut effect = DROPEFFECT_NONE;
            SetForegroundWindow(owner);
            let result = DoDragDrop(
                &data,
                &source,
                DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK,
                &mut effect,
            );
            result.ok().map_err(|e| e.to_string())
        })();
        for pidl in pidls {
            windows::Win32::System::Com::CoTaskMemFree(Some(pidl.cast()));
        }
        result
    }
}
