use super::*;

impl App {
    pub(super) fn manage_tabs(&mut self, hwnd: usize) {
        let Some(index) = self.panel_index(hwnd) else {
            return;
        };
        let panel = &self.settings.windows[index];
        let subtitle = format!(
            "{} · {}",
            panel.panel_title,
            crate::locale::text("Hidden tabs are kept.")
        );
        let pages = visibility_pages(panel);
        // Use the overview as owner even when the panel is hidden. Saving all
        // switches off may hide the panel, but must leave recovery reachable.
        if let Some(answer) = form::show(self.main, "Manage tabs", &subtitle, pages, &|_| Ok(())) {
            let visibility: Vec<_> = answer.values.iter().map(|value| value == "1").collect();
            self.apply_tab_visibility(index, &visibility);
        }
    }

    pub(super) fn apply_tab_visibility(&mut self, index: usize, visibility: &[bool]) {
        let Some(panel) = self.settings.windows.get_mut(index) else {
            return;
        };
        let tabs = panel.tabs.as_mut().unwrap();
        if tabs.len() != visibility.len() {
            return;
        }
        for (tab, visible) in tabs.iter_mut().zip(visibility) {
            tab.is_hidden = !visible;
        }
        panel.ensure_visible_active_tab();
        let id = panel.panel_id.clone();
        if let Some(hwnd) = self
            .windows
            .iter()
            .find_map(|(hwnd, window)| (window.id == id).then_some(*hwnd))
        {
            self.tabs(hwnd);
            self.configure();
            self.refresh(hwnd);
        }
        self.sync_panel_visibility();
        self.changed();
    }
}

pub(super) fn visibility_pages(panel: &Panel) -> Vec<form::Page> {
    vec![form::Page {
        name: "Visible tabs",
        fields: panel
            .tabs
            .as_ref()
            .unwrap()
            .iter()
            .map(|tab| form::Input::Check(tab.tab_name.clone(), !tab.is_hidden))
            .collect(),
    }]
}
