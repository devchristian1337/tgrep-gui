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
}
#[tauri::command]
fn load_settings(state: State<AppState>) -> Result<Settings> {
    let _lock = state.settings_lock.lock().unwrap();
    if !state.settings_path.exists() {
        return Ok(Settings::default());
    }
    serde_json::from_slice(&std::fs::read(&state.settings_path).map_err(|e| e.to_string())?)
        .map_err(|e| {
            format!("Settings could not be read; the original file has been preserved: {e}")
        })
}
#[tauri::command]
fn save_settings(state: State<AppState>, mut settings: Settings) -> Result<()> {
    settings.recent_folders.truncate(12);
    if let Some(token) = state.update_token.lock().unwrap().take() {
        token.cancel();
    }
    let _lock = state.settings_lock.lock().unwrap();
    let dir = state.settings_path.parent().unwrap();
    std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let mut temp = tempfile::NamedTempFile::new_in(dir).map_err(|e| e.to_string())?;
    temp.write_all(&serde_json::to_vec_pretty(&settings).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    temp.as_file().sync_all().map_err(|e| e.to_string())?;
    temp.persist(&state.settings_path)
        .map_err(|e| e.to_string())?;
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
    let result = engine::search(&state.engine, id, options, settings, on_event, token).await;
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
        state.engine.session_exe.lock().unwrap().as_deref(),
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
    if !settings.engine_path.trim().is_empty() {
        return Ok("A custom engine path is configured. Clear it and save settings to use managed updates.".into());
    }
    pin_session(&state, &settings).await;
    let active = engine::discover_from(
        &settings.engine_path,
        state.engine.session_exe.lock().unwrap().as_deref(),
    )
    .ok();
    let token = CancellationToken::new();
    *state.update_token.lock().unwrap() = Some(token.clone());
    let msg = state.updater.check(active.as_deref(), &token).await?;
    state.engine.log(msg.message.clone());
    Ok(msg)
}
async fn pin_session(state: &State<'_, AppState>, settings: &Settings) {
    if !settings.engine_path.trim().is_empty() {
        return;
    }
    if state.engine.session_exe.lock().unwrap().is_some() {
        return;
    }
    let bundled = engine::discover("").ok();
    let min = match &bundled {
        Some(path) => updater::read_version(path, &CancellationToken::new())
            .await
            .ok(),
        None => None,
    };
    if let Some(path) = state.updater.resolve(min).await {
        *state.engine.session_exe.lock().unwrap() = Some(path);
    }
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
    let args = shell_words::split(&settings.editor_arguments)
        .map_err(|e| e.to_string())?
        .into_iter()
        .map(|a| {
            a.replace("$FILE", &path)
                .replace("$LINE", &line.to_string())
        })
        .collect::<Vec<_>>();
    // Launch directly, never interpolate the filename into a shell command.
    std::process::Command::new(&settings.editor_path)
        .args(args)
        .spawn()
        .map_err(|e| e.to_string())?;
    Ok(())
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
