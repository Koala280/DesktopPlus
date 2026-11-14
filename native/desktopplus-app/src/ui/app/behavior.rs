use super::*;
impl App {
    pub(super) fn header_double_click(&mut self, hwnd: usize) -> Result<(), String> {
        let Some(index) = self.panel_index(hwnd) else {
            return Ok(());
        };
        let options = panel_options::Options::read(&self.settings.windows[index]);
        if options.collapse == "button" {
            return Ok(());
        }
        if !options.hover {
            return self.command(hwnd, COLLAPSE);
        }
        let window = self.windows.get_mut(&hwnd).unwrap();
        window.hover_held_open = !window.hover_held_open;
        let collapsed = !window.hover_held_open;
        // Promote the current expansion without restoring the collapsed bounds,
        // including when the double-click arrives during the hover animation.
        window.hover_restore = None;
        self.set_panel_collapsed(hwnd, collapsed);
        Ok(())
    }
    fn stop_hover_tracking(&self, hwnd: usize) {
        unsafe {
            KillTimer(hwnd as _, HOVER_TIMER);
        }
        VIEWS.with(|views| {
            if let Some(view) = views.borrow_mut().get_mut(&hwnd) {
                view.hover_tracking = false;
            }
        });
    }
    pub(super) fn set_panel_collapsed(&mut self, hwnd: usize, collapsed: bool) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        self.capture_geometry();
        self.stop_hover_tracking(hwnd);
        let panel = &mut self.settings.windows[index];
        panel.is_collapsed = collapsed;
        let suppress = collapsed && panel_options::Options::read(panel).hover;
        self.windows.get_mut(&hwnd).unwrap().hover_suppressed = suppress;
        unsafe {
            let mut bounds = RECT::default();
            GetWindowRect(hwnd as _, &mut bounds);
            bounds = motion::destination(hwnd as _).unwrap_or(bounds);
            let height = if collapsed {
                chrome::scale(hwnd as _, PANEL_HEADER_HEIGHT)
            } else {
                panel.expanded_height.max(200.0) as i32
            };
            let mut monitor = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..std::mem::zeroed()
            };
            if GetMonitorInfoW(
                MonitorFromRect(&bounds, MONITOR_DEFAULTTONEAREST),
                &mut monitor,
            ) != 0
            {
                bounds = resized_panel_bounds(bounds, height, monitor.rcWork);
            } else {
                bounds.bottom = bounds.top + height;
            }
            motion::resize(hwnd as _, bounds, collapsed);
            if suppress {
                // A manual collapse stays closed until the pointer leaves;
                // otherwise the next cursor message would reopen it at once.
                VIEWS.with(|views| {
                    if let Some(view) = views.borrow_mut().get_mut(&hwnd) {
                        view.hover_tracking = true;
                    }
                });
                SetTimer(hwnd as _, HOVER_TIMER, 32, None);
            }
        }
        layout(hwnd as _);
        self.changed();
    }
    pub(super) fn apply_panel_theme(&self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        let panel = &self.settings.windows[index];
        let theme = self.settings.panel_theme(panel);
        PANEL_PALETTES.with(|palettes| {
            palettes.borrow_mut().insert(hwnd, Palette::new(&theme));
        });
        refresh_theme(hwnd as _);
        glass::apply(hwnd as _, &theme);
    }
    pub(super) fn restore_hover(&mut self, hwnd: usize) {
        motion::cancel_resize(hwnd as _);
        let rect = self
            .windows
            .get_mut(&hwnd)
            .and_then(|window| window.hover_restore.take());
        unsafe {
            KillTimer(hwnd as _, HOVER_TIMER);
        }
        VIEWS.with(|views| {
            if let Some(view) = views.borrow_mut().get_mut(&hwnd) {
                view.hover_tracking = false;
                if rect.is_some() {
                    view.collapsed = true;
                }
            }
        });
        if let Some(rect) = rect {
            unsafe {
                SetWindowPos(
                    hwnd as _,
                    null_mut(),
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    SWP_NOZORDER | SWP_NOACTIVATE,
                );
            }
            layout(hwnd as _);
        }
    }
    pub(super) fn hover(&mut self, hwnd: usize) {
        unsafe {
            if IsWindowEnabled(hwnd as _) == 0 {
                return;
            }
            let mut gui = GUITHREADINFO {
                cbSize: std::mem::size_of::<GUITHREADINFO>() as u32,
                ..std::mem::zeroed()
            };
            GetGUIThreadInfo(0, &mut gui);
            if gui.flags & GUI_INMENUMODE != 0 || !GetCapture().is_null() {
                return;
            }
            let mut pointer = POINT::default();
            GetCursorPos(&mut pointer);
            let mut rect = RECT::default();
            GetWindowRect(hwnd as _, &mut rect);
            let under = WindowFromPoint(pointer);
            let inside = PtInRect(&rect, pointer) != 0
                && (under == hwnd as _ || IsChild(hwnd as _, under) != 0);
            self.hover_at(hwnd, inside, rect);
        }
    }
    pub(super) fn hover_at(&mut self, hwnd: usize, inside: bool, rect: RECT) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        if !panel_options::Options::read(&self.settings.windows[index]).hover {
            let window = self.windows.get_mut(&hwnd).unwrap();
            window.hover_held_open = false;
            window.hover_suppressed = false;
            self.restore_hover(hwnd);
            return;
        }
        if !self.settings.windows[index].is_collapsed {
            // Timer events already queued before pinning must not undo it or
            // finish an in-progress expansion early.
            self.stop_hover_tracking(hwnd);
            return;
        }
        unsafe {
            let window = self.windows.get_mut(&hwnd).unwrap();
            if inside {
                if window.hover_suppressed {
                    return;
                }
                if window.hover_restore.is_none() && self.settings.windows[index].is_collapsed {
                    let collapsed = motion::destination(hwnd as _).unwrap_or(rect);
                    window.hover_restore = Some(collapsed);
                    let mut monitor = MONITORINFO {
                        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                        ..std::mem::zeroed()
                    };
                    GetMonitorInfoW(
                        MonitorFromRect(&rect, MONITOR_DEFAULTTONEAREST),
                        &mut monitor,
                    );
                    let expanded = resized_panel_bounds(
                        collapsed,
                        self.settings.windows[index].expanded_height.max(220.0) as i32,
                        monitor.rcWork,
                    );
                    motion::resize(hwnd as _, expanded, false);
                }
            } else {
                window.hover_suppressed = false;
                if let Some(collapsed) = window.hover_restore.take() {
                    // Start returning on the first leave check, instead of waiting
                    // another 280 ms. Keep tracking so re-entry can reverse smoothly.
                    motion::resize(hwnd as _, collapsed, true);
                } else if !motion::resizing(hwnd as _) {
                    self.restore_hover(hwnd);
                }
            }
        }
    }
    pub(super) fn fit_contents(&mut self, hwnd: usize, fit_width: bool) -> bool {
        self.restore_hover(hwnd);
        let Some(index) = self.panel_index(hwnd) else {
            return false;
        };
        let panel = &mut self.settings.windows[index];
        let view = panel.active().view_mode.as_str();
        let list = control(hwnd as _, LIST);
        let changed;
        unsafe {
            let count = SendMessageW(list, LVM_GETITEMCOUNT, 0, 0) as i32;
            let mut bounds = RECT::default();
            GetWindowRect(hwnd as _, &mut bounds);
            let mut monitor = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..std::mem::zeroed()
            };
            GetMonitorInfoW(
                MonitorFromRect(&bounds, MONITOR_DEFAULTTONEAREST),
                &mut monitor,
            );
            let original = bounds;
            let mut client = RECT::default();
            GetClientRect(list, &mut client);
            let mut list_window = RECT::default();
            GetWindowRect(list, &mut list_window);
            let side_space = (bounds.right - bounds.left) - (client.right - client.left);
            let (cell_width, cell_height) = images::cell(hwnd as _);
            if fit_width {
                let content_width = if view == "details" {
                    (0..10)
                        .map(|i| SendMessageW(list, LVM_GETCOLUMNWIDTH, i, 0) as i32)
                        .sum::<i32>()
                } else if count > 0 {
                    let mut first = RECT {
                        left: LVIR_BOUNDS as i32,
                        ..Default::default()
                    };
                    SendMessageW(list, LVM_GETITEMRECT, 0, &mut first as *mut RECT as isize);
                    let mut columns = 1;
                    for index in 1..count.min(512) {
                        let mut item = RECT {
                            left: LVIR_BOUNDS as i32,
                            ..Default::default()
                        };
                        if SendMessageW(
                            list,
                            LVM_GETITEMRECT,
                            index as usize,
                            &mut item as *mut RECT as isize,
                        ) == 0
                            || item.top != first.top
                        {
                            break;
                        }
                        columns += 1;
                    }
                    first.left
                        + columns * images::minimum_cell(hwnd as _).0
                        + chrome::scale(hwnd as _, 2)
                } else {
                    cell_width
                };
                bounds.right =
                    bounds.left + (content_width + side_space).max(chrome::scale(hwnd as _, 220));
            } else {
                let header = if view == "details" {
                    chrome::content_scale(hwnd as _, 36)
                } else {
                    0
                };
                let content_height = if view == "details" {
                    count * chrome::content_scale(hwnd as _, 30) + header
                } else {
                    let columns = ((client.right - client.left) / cell_width).max(1);
                    ((count + columns - 1) / columns) * cell_height + chrome::scale(hwnd as _, 8)
                };
                let outside = (bounds.bottom - bounds.top) - (client.bottom - client.top);
                bounds.bottom =
                    bounds.top + (content_height + outside).max(chrome::scale(hwnd as _, 160));
                panel.is_collapsed = false;
                VIEWS.with(|v| {
                    if let Some(v) = v.borrow_mut().get_mut(&hwnd) {
                        v.collapsed = false;
                    }
                });
            }
            bounds = fit_panel_bounds(bounds, monitor.rcWork);
            changed = bounds.right - bounds.left != original.right - original.left
                || bounds.bottom - bounds.top != original.bottom - original.top;
            SetWindowPos(
                hwnd as _,
                null_mut(),
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                SWP_NOZORDER | SWP_NOACTIVATE,
            );
        }
        self.capture_geometry();
        self.changed();
        changed
    }
}
