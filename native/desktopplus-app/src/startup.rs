//! Start at interactive logon, independently of Explorer's Run-key startup queue.
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use windows::Win32::System::{Com::*, TaskScheduler::*, Variant::VARIANT};
use windows::core::{BSTR, Interface};

const TASK_NAME: &str = "DesktopPlus Startup";

struct Apartment;
impl Apartment {
    fn new() -> windows::core::Result<Self> {
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok()? };
        Ok(Self)
    }
}
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}

fn connect() -> windows::core::Result<ITaskService> {
    unsafe {
        let service: ITaskService = CoCreateInstance(&TaskScheduler, None, CLSCTX_INPROC_SERVER)?;
        let empty = VARIANT::default();
        service.Connect(&empty, &empty, &empty, &empty)?;
        Ok(service)
    }
}

fn path_string(path: &Path) -> BSTR {
    BSTR::from_wide(&path.as_os_str().encode_wide().collect::<Vec<_>>())
}

fn definition(
    service: &ITaskService,
    executable: &Path,
    user: &BSTR,
) -> windows::core::Result<ITaskDefinition> {
    unsafe {
        let task = service.NewTask(0)?;
        task.RegistrationInfo()?.SetDescription(&BSTR::from(
            "Starts DesktopPlus immediately when the user signs in.",
        ))?;
        let principal = task.Principal()?;
        principal.SetUserId(user)?;
        principal.SetLogonType(TASK_LOGON_INTERACTIVE_TOKEN)?;
        principal.SetRunLevel(TASK_RUNLEVEL_LUA)?;

        let trigger: ILogonTrigger = task.Triggers()?.Create(TASK_TRIGGER_LOGON)?.cast()?;
        trigger.SetUserId(user)?;
        trigger.SetDelay(&BSTR::from("PT0S"))?;
        trigger.SetEnabled(true.into())?;

        let settings = task.Settings()?;
        settings.SetEnabled(true.into())?;
        settings.SetDisallowStartIfOnBatteries(false.into())?;
        settings.SetStopIfGoingOnBatteries(false.into())?;
        settings.SetStartWhenAvailable(true.into())?;
        settings.SetRunOnlyIfIdle(false.into())?;
        settings.SetRunOnlyIfNetworkAvailable(false.into())?;
        settings.SetMultipleInstances(TASK_INSTANCES_IGNORE_NEW)?;
        // A desktop companion must survive sessions lasting longer than three days.
        settings.SetExecutionTimeLimit(&BSTR::from("PT0S"))?;
        settings.SetPriority(6)?; // Normal process priority, not the scheduler's low default.

        let action: IExecAction = task.Actions()?.Create(TASK_ACTION_EXEC)?.cast()?;
        action.SetPath(&path_string(executable))?;
        action.SetArguments(&BSTR::from("--startup"))?;
        if let Some(directory) = executable.parent() {
            action.SetWorkingDirectory(&path_string(directory))?;
        }
        Ok(task)
    }
}

fn scheduled(enabled: bool, executable: &Path) -> windows::core::Result<()> {
    let _apartment = Apartment::new()?;
    let service = connect()?;
    unsafe {
        let folder = service.GetFolder(&BSTR::from("\\"))?;
        let name = BSTR::from(TASK_NAME);
        if enabled {
            let domain = service.ConnectedDomain()?.to_string();
            let username = service.ConnectedUser()?.to_string();
            let user = BSTR::from(if domain.is_empty() {
                username
            } else {
                format!("{domain}\\{username}")
            });
            let task = definition(&service, executable, &user)?;
            let empty = VARIANT::default();
            folder.RegisterTaskDefinition(
                &name,
                &task,
                TASK_CREATE_OR_UPDATE.0,
                &VARIANT::from(user),
                &empty,
                TASK_LOGON_INTERACTIVE_TOKEN,
                &empty,
            )?;
        } else if let Err(error) = folder.DeleteTask(&name, 0) {
            // Removing an already absent task is a successful disable operation.
            if error.code().0 as u32 != 0x80070002 {
                return Err(error);
            }
        }
    }
    Ok(())
}

fn remove_run_entries() -> Result<(), String> {
    use windows_sys::Win32::{Foundation::*, System::Registry::*};
    for path in [
        r"Software\Microsoft\Windows\CurrentVersion\Run",
        r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
    ] {
        unsafe {
            let mut key = std::ptr::null_mut();
            let status = RegOpenKeyExW(
                HKEY_CURRENT_USER,
                crate::shell::wide(path).as_ptr(),
                0,
                KEY_SET_VALUE,
                &mut key,
            );
            if status == ERROR_FILE_NOT_FOUND {
                continue;
            }
            if status != ERROR_SUCCESS {
                return Err(format!("Cannot open startup registry key ({status})."));
            }
            let status = RegDeleteValueW(key, crate::shell::wide("DesktopPlus").as_ptr());
            RegCloseKey(key);
            if status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND {
                return Err(format!("Cannot remove old startup entry ({status})."));
            }
        }
    }
    Ok(())
}

pub fn configure(enabled: bool) -> Result<(), String> {
    let executable = std::env::current_exe().map_err(|error| error.to_string())?;
    let task_result = scheduled(enabled, &executable)
        .map_err(|error| format!("Cannot configure DesktopPlus startup task: {error}"));
    if enabled {
        // Keep the previous working startup entry if task registration fails.
        task_result?;
        remove_run_entries()
    } else {
        // Attempt both removals even when one fails; never report partial success.
        let run_result = remove_run_entries();
        task_result.and(run_result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn logon_definition_is_immediate_interactive_and_unrestricted_by_power_or_duration() {
        let _apartment = Apartment::new().unwrap();
        let service = connect().unwrap();
        let user = BSTR::from(r"Example\Tester");
        let task = definition(&service, Path::new(r"C:\Apps\DesktopPlus.exe"), &user).unwrap();
        unsafe {
            let mut xml = BSTR::new();
            task.XmlText(&mut xml).unwrap();
            let xml = xml.to_string();
            for expected in [
                "<LogonTrigger>",
                "<UserId>Example\\Tester</UserId>",
                "<Delay>PT0S</Delay>",
                "<LogonType>InteractiveToken</LogonType>",
                "<RunLevel>LeastPrivilege</RunLevel>",
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
                "<StartWhenAvailable>true</StartWhenAvailable>",
                "<RunOnlyIfIdle>false</RunOnlyIfIdle>",
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>",
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>",
                "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
                "<Priority>6</Priority>",
            ] {
                assert!(xml.contains(expected), "Missing {expected} in {xml}");
            }
            assert!(!xml.contains("<BootTrigger>"));
        }
    }

    #[test]
    fn task_xml_round_trip_preserves_unicode_spaces_and_special_characters_in_paths() {
        let _apartment = Apartment::new().unwrap();
        let service = connect().unwrap();
        let path = Path::new(r"C:\Apps & Tools\Müller's <test>\DesktopPlus.exe");
        let task = definition(&service, path, &BSTR::from(r"Example\Tester")).unwrap();
        unsafe {
            let mut xml = BSTR::new();
            task.XmlText(&mut xml).unwrap();
            let parsed = service.NewTask(0).unwrap();
            parsed.SetXmlText(&xml).unwrap();
            let action: IExecAction = parsed
                .Actions()
                .unwrap()
                .get_Item(1)
                .unwrap()
                .cast()
                .unwrap();
            let mut value = BSTR::new();
            action.Path(&mut value).unwrap();
            assert_eq!(value, path_string(path));
            action.Arguments(&mut value).unwrap();
            assert_eq!(value, BSTR::from("--startup"));
            action.WorkingDirectory(&mut value).unwrap();
            assert_eq!(value, path_string(path.parent().unwrap()));
        }
    }
}
