use super::*;
use crate::update;

impl App {
    pub(super) fn configure_updates(&mut self) {
        unsafe {
            KillTimer(self.main, UPDATE_TIMER);
        }
        if self.settings.auto_check_updates && self.smoke.is_none() && !cfg!(debug_assertions) {
            unsafe {
                SetTimer(self.main, UPDATE_TIMER, 60_000, None);
            }
        } else if !self.settings.auto_check_updates {
            let root = self.store.data.join("Updates");
            if let Ok(Some(pending)) = update::pending(&root) {
                update::discard(&root, &pending);
            }
            update::clear_pending(&root);
        }
    }
    pub(super) fn update_status(&mut self, status: &str) {
        self.updater.status = status.into();
        form::status(control(self.main, dashboard::form_id(GENERAL)), status);
    }
    pub(super) fn check_updates(&mut self, manual: bool) -> Result<(), String> {
        if cfg!(debug_assertions) || self.smoke.is_some() {
            self.update_status(&crate::locale::text(
                "Updates are disabled in test and debug builds.",
            ));
            return Ok(());
        }
        if self
            .updater
            .start(self.main as usize, self.store.data.join("Updates"), manual)?
        {
            self.update_status(&crate::locale::text(
                "Checking for updates and preparing the download…",
            ));
        }
        Ok(())
    }
    pub(super) fn update_results(&mut self) {
        let Some(result) = self.updater.poll() else {
            return;
        };
        let failed = result.result.is_err();
        if self.settings.auto_check_updates {
            unsafe {
                SetTimer(
                    self.main,
                    UPDATE_TIMER,
                    if failed {
                        update::RETRY_MS
                    } else {
                        update::INTERVAL_MS
                    },
                    None,
                );
            }
        }
        let root = self.store.data.join("Updates");
        let outcome = (|| -> Result<(), String> {
            match result.result? {
                None => self.update_status(&crate::locale::text("DesktopPlus is up to date.")),
                Some(pending) => {
                    if self.settings.auto_check_updates {
                        update::remember(&root, &pending)?;
                    }
                    let install = result.manual
                        && confirm(
                            self.main,
                            &crate::locale::text(
                                "Version {version} is ready. Install now and restart DesktopPlus?",
                            )
                            .replace("{version}", &pending.version),
                        );
                    if install {
                        self.capture_geometry();
                        update::launch(&self.store, &self.settings, &pending, false)?;
                        self.exiting = true;
                    } else if self.settings.auto_check_updates {
                        self.update_status(
                            &crate::locale::text(
                                "Version {version} will be installed at the next start.",
                            )
                            .replace("{version}", &pending.version),
                        );
                    } else {
                        update::discard(&root, &pending);
                        self.update_status(&crate::locale::text("Update installation cancelled."));
                    }
                }
            }
            Ok(())
        })();
        if let Err(error) = outcome {
            let status = format!("{} {error}", crate::locale::text("Update failed:"));
            self.update_status(&status);
            let _ =
                crate::config::atomic_write(&root.join("last-check-error.txt"), status.as_bytes());
            if result.manual {
                message(self.main, &status);
            }
        }
    }
}
