//! Recover UTF-8 that older settings tools decoded as Windows-1252.
//!
//! Only use this at persisted-text import boundaries, never when drawing text or
//! enumerating files: real Unicode filenames must retain their exact spelling.

pub fn repair(input: &str) -> String {
    let mut current = input.to_owned();
    loop {
        let mut output = String::with_capacity(current.len());
        let mut chars = current.chars();
        while let Some(first) = chars.next() {
            let length = match legacy_byte(first) {
                Some(0xc2..=0xdf) => 2,
                Some(0xe0..=0xef) => 3,
                Some(0xf0..=0xf4) => 4,
                _ => {
                    output.push(first);
                    continue;
                }
            };
            let mut candidate = chars.clone();
            let mut bytes = [0; 4];
            bytes[0] = legacy_byte(first).unwrap();
            let complete = bytes[1..length].iter_mut().all(|byte| {
                if let Some(value @ 0x80..=0xbf) = candidate.next().and_then(legacy_byte) {
                    *byte = value;
                    true
                } else {
                    false
                }
            });
            if complete {
                if let Ok(decoded) = std::str::from_utf8(&bytes[..length]) {
                    output.push_str(decoded);
                    chars = candidate;
                    continue;
                }
            }
            output.push(first);
        }
        // Each successful decoding strictly shrinks the UTF-8 byte length. This
        // also handles settings that were passed through the faulty tool twice.
        if output.len() == current.len() {
            return current;
        }
        current = output;
    }
}

fn legacy_byte(ch: char) -> Option<u8> {
    // Windows-1252 differs from Latin-1 in this range. Keep the C1 controls too
    // so files produced by either legacy decoding can be recovered losslessly.
    const WINDOWS_1252: &str = "€\u{81}‚ƒ„…†‡ˆ‰Š‹Œ\u{8d}Ž\u{8f}\u{90}‘’“”•–—˜™š›œ\u{9d}žŸ";
    u8::try_from(ch as u32).ok().or_else(|| {
        WINDOWS_1252
            .chars()
            .position(|value| value == ch)
            .map(|index| 0x80 + index as u8)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repairs_repeated_encoding_and_mixed_unicode() {
        for (broken, expected) in [
            ("VerknÃ¼pfungen", "Verknüpfungen"),
            ("VerknÃƒÂ¼pfungen", "Verknüpfungen"),
            ("RosÃƒÂ©", "Rosé"),
            ("Ã„ Ã– Ãœ Ã¤ Ã¶ Ã¼ ÃŸ", "Ä Ö Ü ä ö ü ß"),
            ("MenÃ¼ â€“ â€œÃ¶ffnenâ€\u{9d}â€¦", "Menü – “öffnen”…"),
            ("LatvieÅ¡u â‚¬ ðŸ˜€", "Latviešu € 😀"),
            (
                "Änderung 日本語 / VerknÃƒÂ¼pfungen 😀",
                "Änderung 日本語 / Verknüpfungen 😀",
            ),
            ("GrÃ¼Ã\u{9f}e", "Grüße"),
        ] {
            let actual = repair(broken);
            assert_eq!(actual, expected, "{broken}");
            assert_eq!(repair(&actual), actual);
        }
    }

    #[test]
    fn preserves_unicode_and_incomplete_or_invalid_sequences() {
        for text in [
            "Ä Ö Ü ä ö ü ß – … € © ™ 😀 日本語 Україна Latviešu",
            "São Tomé / Ångström / Âge / Île / Øresund",
            "C:\\Benutzer\\Jürgen\\Büro\\Grüße.txt",
            "Ã",
            "Ã text",
            "â€",
            "ðŸ",
            "í\u{a0}\u{80}",
            "�",
        ] {
            assert_eq!(repair(text), text);
        }
    }
}
