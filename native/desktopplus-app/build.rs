fn main() {
    println!("cargo:rerun-if-env-changed=DESKTOPPLUS_VERSION");
    let version = std::env::var("DESKTOPPLUS_VERSION")
        .unwrap_or_else(|_| std::env::var("CARGO_PKG_VERSION").unwrap());
    println!("cargo:rustc-env=DESKTOPPLUS_VERSION={version}");
    assert!(
        version
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ".+-".contains(ch)),
        "Invalid application version"
    );
    let core = version.split(['-', '+']).next().unwrap();
    let parts: Vec<u16> = core
        .split('.')
        .map(|part| {
            part.parse()
                .expect("Version components must fit in 16 bits")
        })
        .collect();
    assert_eq!(parts.len(), 3, "Version must have three numeric components");
    let macros = [
        format!("DP_VERSION=\"{version}\""),
        format!("DP_VERSION_NUM={},{},{},0", parts[0], parts[1], parts[2]),
    ];
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed=app.manifest");
    println!("cargo:rerun-if-changed=../../Resources/desktopplus_icon.ico");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        embed_resource::compile("app.rc", embed_resource::ParamsMacros(macros))
            .manifest_required()
            .unwrap();
    }
}
