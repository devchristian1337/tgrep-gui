#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod engine;
mod models;
mod protocol;
mod updater;
use engine::{Engine, Result};
use models::*;
use std::{
    io::Write,
    path::PathBuf,
    sync::{Arc, Mutex},
};
use tauri::{ipc::Channel, Manager, State};
use tokio_util::sync::CancellationToken;
use updater::{LiveHooks, LiveUpdater, UpdateStatus};

struct AppState {
    engine: Arc<Engine>,
    settings_path: PathBuf,
    settings_lock: Mutex<()>,
    updater: LiveUpdater,
    update_token: Mutex<Option<CancellationToken>>,
    update_gate: tokio::sync::Mutex<()>,
}
#[tauri::command]
fn load_settings(state: State<AppState>) -> Result<Settings> {
    let _lock = state.settings_lock.lock().unwrap();
    read_settings(&state.settings_path)
}
fn read_settings(path: &std::path::Path) -> Result<Settings> {
    if !path.exists() {
        return Ok(Settings::default());
    }
    serde_json::from_slice(&std::fs::read(path).map_err(|e| e.to_string())?)
        .map_err(|e| {
            format!("Settings could not be read; the original file has been preserved: {e}")
        })
}
#[tauri::command]
fn save_settings(state: State<AppState>, settings: Settings) -> Result<()> {
    persist_settings(&state, settings)
}
fn persist_settings(state: &AppState, mut settings: Settings) -> Result<()> {
    settings.recent_folders.truncate(12);
    let _lock = state.settings_lock.lock().unwrap();
    let update_policy_changed = read_settings(&state.settings_path).map_or(true, |previous| {
        previous.auto_update_engine != settings.auto_update_engine
            || previous.engine_path.trim() != settings.engine_path.trim()
    });
    let dir = state.settings_path.parent().unwrap();
    std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let mut temp = tempfile::NamedTempFile::new_in(dir).map_err(|e| e.to_string())?;
    temp.write_all(&serde_json::to_vec_pretty(&settings).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    temp.as_file().sync_all().map_err(|e| e.to_string())?;
    temp.persist(&state.settings_path)
        .map_err(|e| e.to_string())?;
    if update_policy_changed {
        if let Some(token) = state.update_token.lock().unwrap().take() {
            token.cancel();
        }
    }
    Ok(())
}
#[tauri::command]
async fn search(
    state: State<'_, AppState>,
    id: u64,
    options: SearchOptions,
    settings: Settings,
    on_event: Channel<SearchEvent>,
) -> Result<Outcome> {
    let token = CancellationToken::new();
    {
        let mut active = state.engine.active.lock().unwrap();
        if active.is_some() {
            return Err("A search is already running.".into());
        }
        *active = Some((id, token.clone()));
    }
    state.engine.cancel_preview();
    let result = async {
        tokio::select! {
            _ = token.cancelled() => return Err("Cancelled".into()),
            _ = pin_session(&state, &settings) => {}
        }
        engine::search(&state.engine, id, options, settings, on_event, token).await
    }.await;
    *state.engine.active.lock().unwrap() = None;
    result
}
#[tauri::command]
fn cancel_search(state: State<AppState>) {
    state.engine.cancel();
}
#[tauri::command]
async fn preview(state: State<'_, AppState>, path: String) -> Result<Preview> {
    let token = CancellationToken::new();
    {
        let mut active = state.engine.preview.lock().unwrap();
        if let Some(old) = active.replace(token.clone()) {
            old.cancel();
        }
    }
    engine::preview(&state.engine, &path, &token).await
}
#[tauri::command]
async fn restart_server(state: State<'_, AppState>) -> Result<()> {
    let token = CancellationToken::new();
    {
        let mut active = state.engine.active.lock().unwrap();
        if active.is_some() {
            return Err("Wait for the current operation to finish.".into());
        }
        *active = Some((0, token.clone()));
    }
    let result = engine::restart(&state.engine, &token).await;
    *state.engine.active.lock().unwrap() = None;
    result
}
#[tauri::command]
fn set_zoom(window: tauri::WebviewWindow, factor: f64) -> Result<()> {
    if !factor.is_finite() || !(0.5..=2.0).contains(&factor) {
        return Err("Zoom must be between 50% and 200%.".into());
    }
    window.set_zoom(factor).map_err(|e| e.to_string())
}
#[tauri::command]
fn set_theme(window: tauri::WebviewWindow, theme: String) -> Result<()> {
    let theme = match theme.as_str() {
        "light" => tauri::Theme::Light,
        "dark" => tauri::Theme::Dark,
        _ => return Err("Invalid window theme.".into()),
    };
    window.set_theme(Some(theme)).map_err(|e| e.to_string())
}
#[tauri::command]
fn restart_app(app: tauri::AppHandle, state: State<AppState>) -> Result<()> {
    if state.engine.active.lock().unwrap().is_some() {
        return Err("Wait for the current operation to finish.".into());
    }
    if let Some(token) = state.update_token.lock().unwrap().take() {
        token.cancel();
    }
    // Deliver Exit so the existing shutdown hook can stop our owned server.
    app.request_restart();
    Ok(())
}
#[tauri::command]
fn get_logs(state: State<AppState>) -> Vec<String> {
    state.engine.logs.lock().unwrap().clone()
}
#[tauri::command]
async fn engine_version(state: State<'_, AppState>, settings: Settings) -> Result<String> {
    pin_session(&state, &settings).await;
    let exe = engine::discover_from(
        &settings.engine_path,
        state.engine.session_exe.get().and_then(Option::as_deref),
    )?;
    let out = tokio::time::timeout(
        std::time::Duration::from_secs(5),
        engine::command(&exe).arg("--version").output(),
    )
    .await
    .map_err(|_| "Engine version check timed out.")?
    .map_err(|e| e.to_string())?;
    if !out.status.success() {
        return Err("Engine version check failed.".into());
    }
    Ok(String::from_utf8_lossy(&out.stdout).trim().into())
}
#[tauri::command]
async fn check_engine_update(state: State<'_, AppState>, settings: Settings) -> Result<UpdateStatus> {
    perform_update(&state, settings).await
}
async fn perform_update(state: &AppState, settings: Settings) -> Result<UpdateStatus> {
    let _gate = state.update_gate.try_lock()
        .map_err(|_| "An engine update is already running.".to_string())?;
    if !settings.engine_path.trim().is_empty() {
        return Ok("A custom engine path is configured. Clear it and save settings to use managed updates.".into());
    }
    let token = CancellationToken::new();
    *state.update_token.lock().unwrap() = Some(token.clone());
    let result = async {
        tokio::select! {
            _ = token.cancelled() => return Err("Cancelled".into()),
            _ = pin_session(state, &settings) => {}
        }
        let active = engine::discover_from(
            &settings.engine_path,
            state.engine.session_exe.get().and_then(Option::as_deref),
        ).ok();
        state.updater.check(active.as_deref(), &token).await
    }.await;
    state.update_token.lock().unwrap().take();
    if let Ok(msg) = &result {
        state.engine.log(msg.message.clone());
    }
    result
}
async fn pin_session(state: &AppState, settings: &Settings) {
    if !settings.engine_path.trim().is_empty() {
        return;
    }
    state.engine.session_exe.get_or_init(|| async {
        let bundled = engine::discover("").ok();
        let min = match &bundled {
            Some(path) => updater::read_version(path, &CancellationToken::new()).await.ok(),
            None => None,
        };
        state.updater.resolve(min).await.or(bundled)
    }).await;
}
#[tauri::command]
fn open_result(
    state: State<AppState>,
    path: String,
    line: u64,
    containing_folder: bool,
    settings: Settings,
) -> Result<()> {
    let c = state.engine.context.lock().unwrap();
    if !c.as_ref().is_some_and(|c| c.allowed.contains(&path)) {
        return Err("Select a file from the current results.".into());
    }
    if containing_folder {
        let folder = std::path::Path::new(&path)
            .parent()
            .ok_or("No containing directory.")?;
        return open::that(folder).map_err(|e| e.to_string());
    }
    if settings.editor_path.trim().is_empty() {
        return open::that(&path).map_err(|e| e.to_string());
    }
    let args = editor_args(&settings.editor_arguments, &path, line)?;
    // Launch directly, never interpolate the filename into a shell command.
    std::process::Command::new(&settings.editor_path)
        .args(args)
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
}
fn editor_args(template: &str, path: &str, line: u64) -> Result<Vec<String>> {
    Ok(shell_words::split(template)
        .map_err(|e| e.to_string())?
        .into_iter()
        .map(|a| {
            // Substitute the numeric token first so a filename containing
            // "$LINE" is never interpreted as part of the template.
            a.replace("$LINE", &line.to_string()).replace("$FILE", path)
        })
        .collect())
}
#[tauri::command]
fn get_prefill() -> std::collections::HashMap<String, String> {
    let mut out = std::collections::HashMap::new();
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        let (key, value) = if let Some((k, v)) = arg.split_once('=') {
            (k.to_string(), Some(v.to_string()))
        } else {
            (arg, None)
        };
        let key = match key.as_str() {
            "--folder" | "-f" => "folder",
            "--include-files" | "-i" => "include",
            "--exclude-files" | "-e" => "exclude",
            "--text" | "-t" => "pattern",
            _ => continue,
        };
        if let Some(v) = value.or_else(|| args.next()) {
            out.insert(key.into(), v);
        }
    }
    out
}
fn main() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let settings_path = app.path().app_config_dir()?.join("settings.json");
            let engine = Arc::new(Engine::default());
            let log = engine.clone();
            let arch = updater::host_arch().unwrap_or("unsupported");
            let root = updater::default_root()
                .unwrap_or_else(|_| PathBuf::from("."))
                .join(arch);
            app.manage(AppState {
                engine,
                settings_path,
                settings_lock: Mutex::new(()),
                updater: updater::Updater::new(root, arch.into(), LiveHooks::new(), move |t| {
                    log.log(t);
                }),
                update_token: Mutex::new(None),
                update_gate: tokio::sync::Mutex::new(()),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            load_settings,
            save_settings,
            search,
            cancel_search,
            preview,
            restart_server,
            restart_app,
            set_zoom,
            set_theme,
            get_logs,
            engine_version,
            check_engine_update,
            open_result,
            get_prefill
        ])
        .build(tauri::generate_context!())
        .expect("Unable to start tgrep Studio");
    app.run(|app, event| {
        if let tauri::RunEvent::Exit = event {
            let state = app.state::<AppState>();
            tauri::async_runtime::block_on(state.engine.shutdown());
        }
    });
}

#[cfg(test)]
mod settings_tests {
    use super::*;

    #[test]
    fn editor_preserves_placeholder_text_inside_filenames() {
        let path = r#"C:\my files\$LINE-$FILE.rs"#;
        assert_eq!(
            editor_args(r#"--goto "$FILE:$LINE""#, path, 7).unwrap(),
            vec!["--goto".to_string(), format!("{path}:7")]
        );
        assert!(editor_args("\"unclosed", path, 7).is_err());
    }

    fn state(dir: &std::path::Path) -> AppState {
        AppState {
            engine: Arc::default(),
            settings_path: dir.join("settings.json"),
            settings_lock: Mutex::new(()),
            updater: updater::Updater::new(
                dir.join("engines"),
                "x86_64".into(),
                LiveHooks::new(),
                |_| {},
            ),
            update_token: Mutex::new(None),
            update_gate: tokio::sync::Mutex::new(()),
        }
    }

    #[test]
    fn unrelated_saves_preserve_updates_but_policy_changes_cancel_them() {
        let dir = tempfile::tempdir().unwrap();
        let state = state(dir.path());
        let mut settings = Settings::default();
        persist_settings(&state, settings.clone()).unwrap();
        for automatic in [true, false] {
            settings.auto_update_engine = automatic;
            persist_settings(&state, settings.clone()).unwrap();
            let token = CancellationToken::new();
            *state.update_token.lock().unwrap() = Some(token.clone());
            settings.recent_folders = vec!["C:\\projects\\atlas".into()];
            settings.theme = "dark".into();
            persist_settings(&state, settings.clone()).unwrap();
            assert!(
                !token.is_cancelled(),
                "recent folders and appearance must not cancel updates"
            );
            assert_eq!(
                read_settings(&state.settings_path).unwrap().recent_folders,
                settings.recent_folders
            );
            if automatic {
                settings.auto_update_engine = false;
            } else {
                settings.engine_path = "C:\\tools\\tgrep.exe".into();
            }
            persist_settings(&state, settings.clone()).unwrap();
            assert!(token.is_cancelled());
        }
    }

    #[tokio::test]
    async fn concurrent_update_request_preserves_the_running_requests_token() {
        let dir = tempfile::tempdir().unwrap();
        let mut state = state(dir.path());
        // Prevent network access if the command accidentally reaches the updater.
        state.updater = updater::Updater::new(dir.path().join("engines"), "unsupported".into(), LiveHooks::new(), |_| {});
        let _running = state.update_gate.lock().await;
        let token = CancellationToken::new();
        *state.update_token.lock().unwrap() = Some(token.clone());
        let result = perform_update(&state, Settings::default()).await;
        assert!(result.is_err(), "a concurrent request must be rejected");
        let settings = Settings { auto_update_engine: false, ..Settings::default() };
        persist_settings(&state, settings).unwrap();
        assert!(token.is_cancelled(), "settings must still cancel the original request");
    }

    #[tokio::test]
    #[ignore = "requires an installed Microsoft tgrep"]
    async fn concurrent_session_checks_pin_the_fallback_engine() {
        let dir = tempfile::tempdir().unwrap();
        let state = state(dir.path());
        let settings = Settings::default();
        let expected = engine::discover("").unwrap();
        tokio::join!(pin_session(&state, &settings), pin_session(&state, &settings));
        assert_eq!(state.engine.session_exe.get().and_then(Option::as_deref), Some(expected.as_str()));
    }

    #[test]
    fn failed_settings_save_does_not_cancel_update() {
        let dir = tempfile::tempdir().unwrap();
        let state = state(dir.path());
        std::fs::create_dir(&state.settings_path).unwrap();
        let token = CancellationToken::new();
        *state.update_token.lock().unwrap() = Some(token.clone());
        let settings = Settings {
            auto_update_engine: false,
            ..Settings::default()
        };
        assert!(persist_settings(&state, settings).is_err());
        assert!(!token.is_cancelled());
    }

    #[test]
    fn corrupt_settings_are_preserved_and_explicit_save_recovers() {
        let dir = tempfile::tempdir().unwrap();
        let state = state(dir.path());
        let original = b"{broken json";
        std::fs::write(&state.settings_path, original).unwrap();
        assert!(read_settings(&state.settings_path).is_err());
        assert_eq!(std::fs::read(&state.settings_path).unwrap(), original);
        persist_settings(&state, Settings::default()).unwrap();
        assert_eq!(read_settings(&state.settings_path).unwrap().theme, "system");
    }

    #[cfg(windows)]
    #[test]
    fn locked_settings_preserve_the_original_file_on_save_failure() {
        use std::os::windows::fs::OpenOptionsExt;
        let dir = tempfile::tempdir().unwrap();
        let state = state(dir.path());
        persist_settings(&state, Settings::default()).unwrap();
        let original = std::fs::read(&state.settings_path).unwrap();
        let locked = std::fs::OpenOptions::new().read(true).share_mode(1).open(&state.settings_path).unwrap();
        let changed = Settings { theme: "dark".into(), ..Settings::default() };
        assert!(persist_settings(&state, changed).is_err());
        assert_eq!(std::fs::read(&state.settings_path).unwrap(), original);
        drop(locked);
    }
}
