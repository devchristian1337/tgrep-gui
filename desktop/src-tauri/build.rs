fn main() {
    let manifest = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let bundled = manifest.join("binaries").join("tgrep.exe");
    if !bundled.exists() {
        let tools = manifest.join("../../.tools/tgrep/tgrep.exe");
        if tools.exists() {
            std::fs::create_dir_all(bundled.parent().unwrap()).expect("binaries directory");
            std::fs::copy(&tools, &bundled).expect("copy bundled tgrep.exe");
        }
    }
    tauri_build::build()
}
