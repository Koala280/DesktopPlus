//! Resolve panel drops before handing external destinations to the Shell.
use super::*;
use std::path::Path;

#[derive(Debug, PartialEq)]
enum Target {
    Ignore,
    Background,
    Folder(PathBuf),
}

fn target(source: usize, destination: usize, folder: Option<PathBuf>, files: &[PathBuf]) -> Target {
    if let Some(folder) = folder {
        // A selected folder is not a destination for itself or the rest of a
        // multi-selection. Neither are any of its descendants.
        if files.iter().any(|file| contains_path(file, &folder)) {
            Target::Ignore
        } else {
            Target::Folder(folder)
        }
    } else if source == destination {
        // This also covers search results and pinned items whose actual parent
        // directory differs from the folder currently shown in the panel.
        Target::Ignore
    } else {
        Target::Background
    }
}

fn contains_path(parent: &Path, child: &Path) -> bool {
    let mut child = child.components();
    parent.components().all(|part| {
        child
            .next()
            .is_some_and(|next| part.as_os_str().eq_ignore_ascii_case(next.as_os_str()))
    })
}

pub(super) fn folder_at(hwnd: HWND, mut point: POINT) -> Option<PathBuf> {
    let list = control(hwnd, LIST);
    let mut bounds = RECT::default();
    unsafe {
        if list.is_null()
            || ScreenToClient(list, &mut point) == 0
            || GetClientRect(list, &mut bounds) == 0
            || PtInRect(&bounds, point) == 0
        {
            return None;
        }
    }
    let mut hit = LVHITTESTINFO {
        pt: point,
        ..Default::default()
    };
    let item = unsafe { SendMessageW(list, LVM_HITTEST, 0, &mut hit as *mut _ as isize) };
    if item < 0 {
        return None;
    }
    VIEWS.with(|views| {
        let views = views.borrow();
        let view = views.get(&(hwnd as usize))?;
        if view.is_main || view.kind.eq_ignore_ascii_case("RecycleBin") {
            return None;
        }
        view.rows
            .get(item as usize)
            .filter(|row| row.directory)
            .map(|row| row.path.clone())
    })
}

/// Return true when the panel has consumed the drop, even if it is a no-op.
/// This runs inside OLE's drag loop, before a legacy WM_DROPFILES is posted.
pub(super) fn finish_internal(source: usize, files: &[PathBuf]) -> bool {
    let mut point = POINT::default();
    let hwnd = unsafe {
        if GetCursorPos(&mut point) == 0 {
            return false;
        }
        GetAncestor(WindowFromPoint(point), GA_ROOT)
    };
    let panel = VIEWS.with(|views| {
        views
            .borrow()
            .get(&(hwnd as usize))
            .is_some_and(|view| !view.is_main)
    });
    if !panel {
        return false;
    }
    let folder = match target(source, hwnd as usize, folder_at(hwnd, point), files) {
        Target::Ignore => return true,
        Target::Background => None,
        Target::Folder(folder) => Some(folder),
    };
    let copy = unsafe { GetKeyState(VK_CONTROL as i32) } < 0;
    let shift = unsafe { GetKeyState(VK_SHIFT as i32) } < 0 || (source == hwnd as usize && !copy);
    emit(Event::Drop(
        hwnd as usize,
        files.to_vec(),
        copy,
        shift,
        folder,
    ));
    true
}

#[cfg(test)]
mod tests;
