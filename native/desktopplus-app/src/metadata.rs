use std::path::Path;

pub const COLUMNS: [(&str, &str, i32); 10] = [
    ("name", "Name", 220),
    ("type", "Type", 80),
    ("size", "Size", 85),
    ("modified", "Modified", 110),
    ("created", "Created", 110),
    ("dimensions", "Dimensions", 110),
    ("authors", "Authors", 130),
    ("categories", "Categories", 130),
    ("tags", "Tags", 130),
    ("title", "Title", 180),
];

pub fn read(path: &Path, column: usize) -> Vec<u16> {
    use windows::{
        Win32::{
            Foundation::PROPERTYKEY,
            System::Com::{CoTaskMemFree, StructuredStorage::PropVariantClear},
            UI::Shell::{IShellItem2, PropertiesSystem::*, SHCreateItemFromParsingName},
        },
        core::PCWSTR,
    };
    let property = match column {
        5 => "System.Image.Dimensions",
        6 => "System.Author",
        7 => "System.Category",
        8 => "System.Keywords",
        9 => "System.Title",
        _ => return Vec::new(),
    };
    unsafe {
        let Ok(item): Result<IShellItem2, _> =
            SHCreateItemFromParsingName(PCWSTR(crate::shell::wide(path).as_ptr()), None)
        else {
            return Vec::new();
        };
        let mut key = PROPERTYKEY::default();
        if PSGetPropertyKeyFromName(PCWSTR(crate::shell::wide(property).as_ptr()), &mut key)
            .is_err()
        {
            return Vec::new();
        }
        let Ok(mut value) = item.GetProperty(&key) else {
            return Vec::new();
        };
        let text = PSFormatForDisplayAlloc(&key, &value, PDFF_DEFAULT);
        let _ = PropVariantClear(&mut value);
        if let Ok(text) = text {
            let result = text.as_wide().to_vec();
            CoTaskMemFree(Some(text.0.cast()));
            result
        } else {
            Vec::new()
        }
    }
}
