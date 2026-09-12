use crate::{
    engine::{self, Engine, Result},
    models::SearchOptions,
    protocol,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{copy, Read, Write},
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};
use tokio_util::sync::CancellationToken;

const MAX_ARCHIVE: u64 = 64 * 1024 * 1024;
const MAX_EXECUTABLE: u64 = 128 * 1024 * 1024;
const MAX_API: u64 = 4 * 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Version(u32, u32, u32);
impl Version {
    pub fn parse(text: &str) -> Option<Self> {
        let mut p = text.split('.');
        let (a, b, c) = (p.next()?, p.next()?, p.next()?);
        if p.next().is_some() || !text.chars().all(|c| c.is_ascii_digit() || c == '.') {
            return None;
        }
        Some(Self(a.parse().ok()?, b.parse().ok()?, c.parse().ok()?))
    }
}
impl std::fmt::Display for Version {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}.{}.{}", self.0, self.1, self.2)
    }
}

#[derive(Clone, Debug)]
pub struct Release {
    pub version: Version,
    pub url: String,
    pub sha256: String,
}
#[derive(Debug)]
pub struct Selection {
    pub candidate: Option<Release>,
    pub latest_without_windows: Option<Version>,
    pub count: usize,
}

#[derive(Serialize, Deserialize, Clone)]
struct Installed {
    #[serde(rename = "Version")]
    version: String,
    #[serde(rename = "Sha256")]
    sha256: String,
}
#[derive(Serialize, Deserialize, Default)]
struct State {
    #[serde(rename = "Current")]
    current: Option<Installed>,
    #[serde(rename = "Previous")]
    previous: Option<Installed>,
}

pub trait Hooks: Send + Sync {
    fn get(
        &self,
        url: &str,
    ) -> impl std::future::Future<Output = Result<Vec<u8>>> + Send;
    fn version(
        &self,
        exe: &str,
    ) -> impl std::future::Future<Output = Result<Version>> + Send;
    fn probe(
        &self,
        exe: &str,
        expected: Version,
    ) -> impl std::future::Future<Output = Result<()>> + Send;
    fn migrate(
        &self,
        previous: &str,
        candidate: &str,
    ) -> impl std::future::Future<Output = Result<()>> + Send;
}

pub struct LiveHooks {
    http: reqwest::Client,
}
impl LiveHooks {
    pub fn new() -> Self {
        Self {
            http: reqwest::Client::builder()
                .timeout(std::time::Duration::from_secs(90))
                .user_agent("tgrep-gui/2.0")
                .build()
                .expect("http client"),
        }
    }
}
impl Hooks for LiveHooks {
    async fn get(&self, url: &str) -> Result<Vec<u8>> {
        let mut req = self.http.get(url);
        if url.starts_with("https://api.github.com/") {
            req = req.header("Accept", "application/vnd.github+json");
        }
        let response = req.send().await.map_err(|e| e.to_string())?;
        if !response.status().is_success() {
            return Err(format!("HTTP {}", response.status()));
        }
        let limit = if url.starts_with("https://api.github.com/") {
            MAX_API
        } else {
            MAX_ARCHIVE
        };
        read_download(response, limit).await
    }
    async fn version(&self, exe: &str) -> Result<Version> {
        read_version(exe, &CancellationToken::new()).await
    }
    async fn probe(&self, exe: &str, expected: Version) -> Result<()> {
        probe_engine(exe, expected, &CancellationToken::new()).await
    }
    async fn migrate(&self, previous: &str, candidate: &str) -> Result<()> {
        probe_index_upgrade(previous, candidate, &CancellationToken::new()).await
    }
}

pub struct Updater<H: Hooks> {
    root: PathBuf,
    arch: String,
    hooks: H,
    log: Arc<dyn Fn(&str) + Send + Sync>,
}

async fn read_download(mut response: reqwest::Response, limit: u64) -> Result<Vec<u8>> {
    if response.content_length().is_some_and(|length| length > limit) {
        return Err("Download exceeds the size limit.".into());
    }
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|e| e.to_string())? {
        if chunk.len() as u64 > limit.saturating_sub(bytes.len() as u64) {
            return Err("Download exceeds the size limit.".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}
pub type LiveUpdater = Updater<LiveHooks>;

impl<H: Hooks> Updater<H> {
    pub fn new(
        root: PathBuf,
        arch: String,
        hooks: H,
        log: impl Fn(&str) + Send + Sync + 'static,
    ) -> Self {
        Self {
            root,
            arch,
            hooks,
            log: Arc::new(log),
        }
    }
    fn emit(&self, text: &str) {
        (self.log)(text);
    }
    fn state_path(&self) -> PathBuf {
        self.root.join("installed.json")
    }
    fn executable(&self, engine: &Installed) -> Result<PathBuf> {
        if Version::parse(&engine.version).is_none() || !is_hash(&engine.sha256) {
            return Err("Invalid installed engine metadata.".into());
        }
        Ok(self
            .root
            .join(&engine.version)
            .join(&engine.sha256)
            .join("tgrep.exe"))
    }
    fn read_state(&self) -> State {
        let path = self.state_path();
        let bytes = match fs::read(&path) {
            Ok(b) => b,
            Err(_) => return State::default(),
        };
        if bytes.len() > 4096 {
            self.emit("Installed engine metadata ignored: Engine metadata is too large.");
            return State::default();
        }
        match serde_json::from_slice::<State>(&bytes) {
            Ok(state) => {
                for item in [&state.current, &state.previous].into_iter().flatten() {
                    if self.executable(item).is_err() {
                        self.emit("Installed engine metadata ignored: Invalid installed engine metadata.");
                        return State::default();
                    }
                }
                state
            }
            Err(e) => {
                self.emit(&format!("Installed engine metadata ignored: {e}"));
                State::default()
            }
        }
    }
    pub async fn resolve(&self, minimum: Option<Version>) -> Option<String> {
        let state = self.read_state();
        for item in [state.current, state.previous].into_iter().flatten() {
            let version = match Version::parse(&item.version) {
                Some(v) => v,
                None => continue,
            };
            if minimum.is_some_and(|m| version < m) {
                continue;
            }
            match self.verify_installed(&item, version).await {
                Ok(path) => {
                    self.emit(&format!("Using verified tgrep {version}."));
                    return Some(path);
                }
                Err(e) => self.emit(&format!("Engine skipped: {e}")),
            }
        }
        None
    }
    async fn verify_installed(&self, item: &Installed, version: Version) -> Result<String> {
        let path = self.executable(item)?;
        if !path.is_file() || hash_file(&path)? != item.sha256 {
            return Err("Installed engine checksum mismatch.".into());
        }
        let exe = path.to_string_lossy().into_owned();
        tokio::time::timeout(
            std::time::Duration::from_secs(45),
            self.hooks.probe(&exe, version),
        )
        .await
        .map_err(|_| "Engine compatibility test timed out.".to_string())??;
        Ok(exe)
    }
    pub async fn check(
        &self,
        active: Option<&str>,
        token: &CancellationToken,
    ) -> Result<UpdateStatus> {
        if self.arch != "x86_64" && self.arch != "aarch64" {
            return Ok("Automatic updates are unavailable for this architecture.".into());
        }
        fs::create_dir_all(&self.root).map_err(|e| e.to_string())?;
        let _lock = match lock_update(&self.root.join("update.lock")) {
            Ok(file) => file,
            Err(e) if e.contains("Another app instance") => return Ok(e.into()),
            Err(e) => return Err(e),
        };
        let stage = self.root.join(format!(
            "stage-{:x}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        let result = self.check_locked(active, token, &stage).await;
        let _ = fs::remove_dir_all(&stage);
        match result {
            Ok(msg) => Ok(msg),
            Err(e) if e == "Cancelled" || token.is_cancelled() => Err("Cancelled".into()),
            Err(e) => Ok(
                format!("Engine update unavailable; keeping the current engine. {e}").into(),
            ),
        }
    }
    async fn check_locked(
        &self,
        active: Option<&str>,
        token: &CancellationToken,
        stage: &Path,
    ) -> Result<UpdateStatus> {
        cancel(token)?;
        let current = match active {
            Some(exe) => self.hooks.version(exe).await?,
            None => Version(0, 0, 0),
        };
        let mut release: Option<Release> = None;
        let mut latest_without_windows: Option<Version> = None;
        for page in 1.. {
            cancel(token)?;
            let url = format!(
                "https://api.github.com/repos/microsoft/tgrep/releases?per_page=30&page={page}"
            );
            let body = with_token(token, self.hooks.get(&url)).await?;
            if body.len() as u64 > MAX_API {
                return Err("Download exceeds the size limit.".into());
            }
            let selection = select_release(&body, &self.arch, current)?;
            if let Some(c) = selection.candidate {
                if release.as_ref().is_none_or(|r| c.version > r.version) {
                    release = Some(c);
                }
            }
            if let Some(v) = selection.latest_without_windows {
                if latest_without_windows.is_none_or(|o| v > o) {
                    latest_without_windows = Some(v);
                }
            }
            if selection.count < 30 {
                break;
            }
        }
        let Some(release) = release else {
            let status = if active.is_none() {
                format!(
                    "No Windows engine is available for {}.",
                    self.arch
                )
            } else {
                format!(
                    "No newer Windows engine is available for {}; keeping tgrep {current}.",
                    self.arch
                )
            };
            return Ok(
                (match latest_without_windows {
                    Some(v) => format!(
                        "{status} tgrep {v} has no Windows package for this architecture."
                    ),
                    None => status,
                })
                .into(),
            );
        };
        let state = self.read_state();
        if let Some(pending) = &state.current {
            if Version::parse(&pending.version).is_some_and(|v| v >= release.version) {
                let path = self.executable(pending)?;
                if path.is_file() && hash_file(&path)? == pending.sha256 {
                    return Ok(UpdateStatus::ready(format!(
                        "tgrep {} is already installed. Restart the app to use it.",
                        pending.version
                    )));
                }
            }
        }
        self.emit(&format!("Downloading tgrep {}…", release.version));
        fs::create_dir_all(stage).map_err(|e| e.to_string())?;
        let archive = stage.join("download.zip");
        let zip = with_token(token, self.hooks.get(&release.url)).await?;
        if zip.len() as u64 > MAX_ARCHIVE {
            return Err("Download exceeds the size limit.".into());
        }
        fs::write(&archive, &zip).map_err(|e| e.to_string())?;
        if hash_bytes(&zip) != release.sha256 {
            return Err("Release archive checksum mismatch.".into());
        }
        let candidate = stage.join("tgrep.exe");
        extract_tgrep(&archive, &candidate)?;
        self.emit(&format!(
            "Testing tgrep {} compatibility…",
            release.version
        ));
        let candidate_s = candidate.to_string_lossy().into_owned();
        with_token(token, self.hooks.probe(&candidate_s, release.version)).await?;
        if let Some(active) = active {
            with_token(token, self.hooks.migrate(active, &candidate_s)).await?;
        }
        let installed = Installed {
            version: release.version.to_string(),
            sha256: hash_file(&candidate)?,
        };
        let destination = self.executable(&installed)?;
        fs::create_dir_all(destination.parent().unwrap()).map_err(|e| e.to_string())?;
        if !destination.exists() {
            fs::rename(&candidate, &destination).map_err(|e| e.to_string())?;
        } else if hash_file(&destination)? != installed.sha256 {
            return Err("Cached engine checksum mismatch.".into());
        }
        let state = self.read_state();
        let previous = [state.current, state.previous]
            .into_iter()
            .flatten()
            .find(|item| {
                self.executable(item)
                    .ok()
                    .and_then(|p| dunce::canonicalize(p).ok())
                    .zip(active.and_then(|a| dunce::canonicalize(a).ok()))
                    .is_some_and(|(a, b)| a == b)
            });
        let next = State {
            current: Some(installed),
            previous,
        };
        cancel(token)?;
        write_state(&self.state_path(), &next)?;
        Ok(UpdateStatus::ready(format!(
            "tgrep {} verified and ready. Restart the app to use it.",
            release.version
        )))
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateStatus {
    pub message: String,
    pub restart_required: bool,
}
impl UpdateStatus {
    fn ready(message: String) -> Self {
        Self {
            message,
            restart_required: true,
        }
    }
}
impl From<String> for UpdateStatus {
    fn from(message: String) -> Self {
        Self {
            message,
            restart_required: false,
        }
    }
}
impl From<&str> for UpdateStatus {
    fn from(message: &str) -> Self {
        message.to_string().into()
    }
}

pub fn host_arch() -> Result<&'static str> {
    match std::env::consts::ARCH {
        "x86_64" => Ok("x86_64"),
        "aarch64" => Ok("aarch64"),
        _ => Err("unsupported".into()),
    }
}

fn write_state(dest: &Path, state: &State) -> Result<()> {
    let dir = dest.parent().ok_or("Engine metadata directory is missing.")?;
    let mut tmp = tempfile::NamedTempFile::new_in(dir).map_err(|e| e.to_string())?;
    tmp.write_all(&serde_json::to_vec(state).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    tmp.as_file().sync_all().map_err(|e| e.to_string())?;
    tmp.persist(dest).map_err(|e| e.to_string())?;
    Ok(())
}
pub fn default_root() -> Result<PathBuf> {
    let local = std::env::var_os("LOCALAPPDATA").ok_or("LOCALAPPDATA is not set.")?;
    Ok(PathBuf::from(local).join("tgrep-gui").join("engines"))
}
pub fn parse_release(metadata: &[u8], arch: &str) -> Result<Release> {
    if arch != "x86_64" && arch != "aarch64" {
        return Err("Unsupported architecture.".into());
    }
    let release: Value = serde_json::from_slice(metadata).map_err(|e| e.to_string())?;
    let tag = release["tag_name"].as_str().unwrap_or("");
    if release["draft"].as_bool() == Some(true)
        || release["prerelease"].as_bool() == Some(true)
        || !tag.starts_with('v')
        || Version::parse(&tag[1..]).is_none()
    {
        return Err("Only stable versioned releases are supported.".into());
    }
    let version = Version::parse(&tag[1..]).unwrap();
    let name = format!("tgrep-{tag}-{arch}-pc-windows-msvc.zip");
    let assets = release["assets"]
        .as_array()
        .ok_or("Windows release asset not found.")?;
    let matches: Vec<&Value> = assets
        .iter()
        .filter(|a| a["name"].as_str() == Some(&name))
        .collect();
    if matches.len() != 1 {
        return Err("Windows release asset not found.".into());
    }
    let asset = matches[0];
    let expected = format!("https://github.com/microsoft/tgrep/releases/download/{tag}/{name}");
    let digest = asset["digest"].as_str().unwrap_or("");
    let size = asset["size"].as_u64().unwrap_or(0);
    if asset["browser_download_url"].as_str() != Some(&expected)
        || !digest.starts_with("sha256:")
        || !is_hash(&digest[7..])
        || size == 0
        || size > MAX_ARCHIVE
    {
        return Err("Release URL, size or SHA-256 digest is invalid.".into());
    }
    Ok(Release {
        version,
        url: expected,
        sha256: digest[7..].into(),
    })
}
pub fn select_release(metadata: &[u8], arch: &str, current: Version) -> Result<Selection> {
    if arch != "x86_64" && arch != "aarch64" {
        return Err("Unsupported architecture.".into());
    }
    let list: Vec<Value> = serde_json::from_slice(metadata).map_err(|e| e.to_string())?;
    let mut candidate = None;
    let mut latest_without_windows = None;
    for release in &list {
        let tag = release["tag_name"].as_str().unwrap_or("");
        if release["draft"].as_bool() == Some(true)
            || release["prerelease"].as_bool() == Some(true)
            || !tag.starts_with('v')
        {
            continue;
        }
        let Some(version) = Version::parse(&tag[1..]) else {
            continue;
        };
        if version <= current {
            continue;
        }
        let name = format!("tgrep-{tag}-{arch}-pc-windows-msvc.zip");
        let has_windows = release["assets"]
            .as_array()
            .is_some_and(|a| a.iter().any(|x| x["name"].as_str() == Some(&name)));
        if !has_windows {
            if latest_without_windows.is_none_or(|v| version > v) {
                latest_without_windows = Some(version);
            }
            continue;
        }
        let parsed = parse_release(&serde_json::to_vec(release).unwrap(), arch)?;
        if candidate.as_ref().is_none_or(|c: &Release| parsed.version > c.version) {
            candidate = Some(parsed);
        }
    }
    Ok(Selection {
        candidate,
        latest_without_windows,
        count: list.len(),
    })
}

pub async fn read_version(exe: &str, token: &CancellationToken) -> Result<Version> {
    let mut text = String::new();
    let dir = Path::new(exe)
        .parent()
        .unwrap_or(Path::new("."))
        .to_string_lossy()
        .into_owned();
    let args = ["--version".into()];
    let call = engine::run(
        exe,
        &args,
        &dir,
        token,
        Arc::new(Mutex::new(vec![])),
        |line| {
            text.push_str(&line);
            text.push('\n');
            Ok(())
        },
    );
    tokio::time::timeout(std::time::Duration::from_secs(5), call)
        .await
        .map_err(|_| "Engine version check timed out.".to_string())??;
    parse_version_output(&text)
}
pub fn parse_version_output(text: &str) -> Result<Version> {
    let line = text.trim().lines().next().unwrap_or("");
    let rest = line.strip_prefix("tgrep ").ok_or("Unexpected tgrep version response.")?;
    Version::parse(rest).ok_or_else(|| "Unexpected tgrep version response.".into())
}

async fn probe_engine(exe: &str, expected: Version, token: &CancellationToken) -> Result<()> {
    if read_version(exe, token).await? != expected {
        return Err("Downloaded executable version differs from the release.".into());
    }
    let fixture = tempfile::tempdir().map_err(|e| e.to_string())?;
    let file = fixture.path().join("caffè file.txt");
    fs::write(&file, "è😀 needle needle\nNEEDLE\nneedles\n").map_err(|e| e.to_string())?;
    let engine = Engine::default();
    let folder = fixture.path().to_string_lossy().into_owned();
    let file_s = dunce::canonicalize(&file)
        .map_err(|e| e.to_string())?
        .to_string_lossy()
        .into_owned();
    for indexed in [false, true] {
        let o = SearchOptions {
            folder: folder.clone(),
            pattern: "needle".into(),
            include: "*.txt".into(),
            exclude: String::new(),
            ignore_case: true,
            literal: true,
            whole_word: true,
            use_index: indexed,
        };
        let mut s = crate::models::Settings {
            engine_path: exe.into(),
            ..Default::default()
        };
        if indexed {
            engine::prepare(&engine, exe, &o, &s, token, |_| {}).await?;
        }
        let (matches, paths, _) = collect(exe, &o, &s.index_path, None, token).await?;
        if matches != 3 || paths.iter().any(|p| {
            dunce::canonicalize(p).ok().map(|c| c.to_string_lossy().into_owned()) != Some(file_s.clone())
        }) {
            return Err("Engine returned incompatible search counts or paths.".into());
        }
        s.engine_path = exe.into();
        let mut preview = o.clone();
        preview.use_index = false;
        let (_, _, lines) = collect(exe, &preview, "", Some(&file_s), token).await?;
        if lines.len() != 2
            || lines[0].number != 1
            || lines[0].count != 2
            || lines[0].spans.len() != 2
            || lines[0].spans[0] != (4, 10)
        {
            return Err("Engine returned incompatible preview JSON.".into());
        }
        let missing = SearchOptions {
            pattern: "missing_probe_token".into(),
            ..o
        };
        let (n, _, _) = collect(exe, &missing, &s.index_path, None, token).await?;
        if n != 0 {
            return Err("Engine returned false matches.".into());
        }
    }
    if engine.servers.lock().await.is_empty() {
        return Err("Engine server did not report a ready index.".into());
    }
    engine.shutdown().await;
    Ok(())
}
async fn probe_index_upgrade(previous: &str, candidate: &str, token: &CancellationToken) -> Result<()> {
    let fixture = tempfile::tempdir().map_err(|e| e.to_string())?;
    fs::write(fixture.path().join("probe.txt"), "index_compatibility_token\n")
        .map_err(|e| e.to_string())?;
    let folder = fixture.path().to_string_lossy().into_owned();
    let engine = Engine::default();
    let o = SearchOptions {
        folder,
        pattern: "index_compatibility_token".into(),
        include: String::new(),
        exclude: String::new(),
        ignore_case: true,
        literal: true,
        whole_word: false,
        use_index: true,
    };
    for exe in [previous, candidate, previous] {
        let s = crate::models::Settings {
            engine_path: exe.into(),
            ..Default::default()
        };
        engine::prepare(&engine, exe, &o, &s, token, |_| {}).await?;
        let (n, _, _) = collect(exe, &o, &s.index_path, None, token).await?;
        if n != 1 {
            return Err("Engine index upgrade/fallback compatibility check failed.".into());
        }
    }
    engine.shutdown().await;
    Ok(())
}
async fn collect(
    exe: &str,
    o: &SearchOptions,
    index: &str,
    file: Option<&str>,
    token: &CancellationToken,
) -> Result<(usize, Vec<PathBuf>, Vec<crate::models::MatchLine>)> {
    let mut matches = 0usize;
    let mut paths = Vec::new();
    let mut lines = Vec::new();
    let args = protocol::search_args(o, index, file)?;
    engine::run(
        exe,
        &args,
        &o.folder,
        token,
        Arc::new(Mutex::new(vec![])),
        |line| {
            if let Some((path, m)) = protocol::parse_match(&line, Path::new(&o.folder))? {
                matches += m.count;
                paths.push(path);
                lines.push(m);
            }
            Ok(())
        },
    )
    .await?;
    Ok((matches, paths, lines))
}

fn is_hash(h: &str) -> bool {
    h.len() == 64 && h.bytes().all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
}
fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn hash_file(path: &Path) -> Result<String> {
    let mut file = File::open(path).map_err(|e| e.to_string())?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 65536];
    loop {
        let n = file.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}
fn extract_tgrep(archive: &Path, dest: &Path) -> Result<()> {
    let file = File::open(archive).map_err(|e| e.to_string())?;
    let mut zip = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    let mut found = None;
    for i in 0..zip.len() {
        let name = zip
            .by_index(i)
            .map_err(|e| e.to_string())?
            .name()
            .to_string();
        if name == "tgrep.exe" {
            if found.is_some() {
                return Err("Release must contain exactly one root tgrep.exe.".into());
            }
            found = Some(i);
        }
    }
    let i = found.ok_or("Release must contain exactly one root tgrep.exe.")?;
    let mut entry = zip.by_index(i).map_err(|e| e.to_string())?;
    if entry.size() > MAX_EXECUTABLE {
        return Err("Release must contain exactly one root tgrep.exe.".into());
    }
    let mut out = File::create(dest).map_err(|e| e.to_string())?;
    let copied = copy(&mut entry, &mut out).map_err(|e| e.to_string())?;
    if copied > MAX_EXECUTABLE {
        return Err("Download exceeds the size limit.".into());
    }
    Ok(())
}
async fn with_token<T>(
    token: &CancellationToken,
    fut: impl std::future::Future<Output = Result<T>>,
) -> Result<T> {
    tokio::select! {
        _ = token.cancelled() => Err("Cancelled".into()),
        r = fut => r,
    }
}
fn lock_update(path: &Path) -> Result<File> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    if !path.exists() {
        File::create(path).map_err(|e| e.to_string())?;
    }
    let mut opts = OpenOptions::new();
    opts.read(true).write(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        opts.share_mode(0);
    }
    opts.open(path).map_err(|e| {
        #[cfg(windows)]
        if e.raw_os_error() == Some(32) {
            return "Another app instance is checking for engine updates.".into();
        }
        format!("Cannot take the engine update lock: {e}")
    })
}
fn cancel(token: &CancellationToken) -> Result<()> {
    if token.is_cancelled() {
        Err("Cancelled".into())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Cursor, Write};
    use zip::write::SimpleFileOptions;

    #[tokio::test]
    async fn oversized_downloads_stop_before_waiting_for_the_remaining_body() {
        for reply in [
            "HTTP/1.1 200 OK\r\nContent-Length: 65\r\n\r\n".to_string(),
            format!("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n41\r\n{}\r\n", "x".repeat(65)),
        ] {
            let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
            let address = listener.local_addr().unwrap();
            let server = std::thread::spawn(move || {
                let (mut socket, _) = listener.accept().unwrap();
                let mut request = [0u8; 2048];
                socket.read(&mut request).unwrap();
                socket.write_all(reply.as_bytes()).unwrap();
            });
            let response = reqwest::Client::new().get(format!("http://{address}")).send().await.unwrap();
            let result = read_download(response, 64).await;
            server.join().unwrap();
            assert_eq!(result.unwrap_err(), "Download exceeds the size limit.");
        }
    }

    #[test]
    fn manifest_is_always_present_during_replacement() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("installed.json");
        write_state(&path, &State::default()).unwrap();
        let running = Arc::new(std::sync::atomic::AtomicBool::new(true));
        let reader_running = running.clone();
        let reader_path = path.clone();
        let reader = std::thread::spawn(move || {
            let mut missing = false;
            while reader_running.load(std::sync::atomic::Ordering::Relaxed) {
                match fs::read(&reader_path) {
                    Ok(bytes) => { serde_json::from_slice::<State>(&bytes).unwrap(); }
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => missing = true,
                    Err(_) => {}
                }
            }
            missing
        });
        let mut replacements = 0;
        for _ in 0..500 {
            // Windows may reject a replacement while another reader holds the
            // target open. Even then the previous metadata must remain intact.
            if write_state(&path, &State::default()).is_ok() {
                replacements += 1;
            }
        }
        running.store(false, std::sync::atomic::Ordering::Relaxed);
        assert!(!reader.join().unwrap(), "engine metadata disappeared during replacement");
        assert!(replacements > 0);
        write_state(&path, &State::default()).unwrap();
    }

    struct Mock {
        version: Mutex<String>,
        arch: String,
        bad_hash: Mutex<bool>,
        offline: Mutex<bool>,
        prerelease: Mutex<bool>,
        malicious_url: Mutex<bool>,
        missing_digest: Mutex<bool>,
        wrong_entry: Mutex<bool>,
        downloads: Mutex<u32>,
        feed_pages: Mutex<Option<Vec<String>>>,
        reject: Mutex<bool>,
        reject_migration: Mutex<bool>,
        probes: Mutex<u32>,
    }
    impl Mock {
        fn new(arch: &str) -> Arc<Self> {
            Arc::new(Self {
                version: Mutex::new("1.0.5".into()),
                arch: arch.into(),
                bad_hash: Mutex::new(false),
                offline: Mutex::new(false),
                prerelease: Mutex::new(false),
                malicious_url: Mutex::new(false),
                missing_digest: Mutex::new(false),
                wrong_entry: Mutex::new(false),
                downloads: Mutex::new(0),
                feed_pages: Mutex::new(None),
                reject: Mutex::new(false),
                reject_migration: Mutex::new(false),
                probes: Mutex::new(0),
            })
        }
        fn zip(&self) -> Vec<u8> {
            let entry = if *self.wrong_entry.lock().unwrap() {
                "../tgrep.exe"
            } else {
                "tgrep.exe"
            };
            zip_bytes(self.version.lock().unwrap().as_bytes(), entry)
        }
        fn digest_for(&self, zip: &[u8]) -> String {
            if *self.bad_hash.lock().unwrap() {
                "0".repeat(64)
            } else {
                hash_bytes(zip)
            }
        }
    }
    impl Hooks for Arc<Mock> {
        async fn get(&self, url: &str) -> Result<Vec<u8>> {
            if *self.offline.lock().unwrap() {
                return Err("offline test".into());
            }
            if url.contains("api.github.com") {
                if let Some(pages) = self.feed_pages.lock().unwrap().clone() {
                    let page = url
                        .rsplit("&page=")
                        .next()
                        .and_then(|s| s.parse::<usize>().ok())
                        .unwrap_or(1);
                    return Ok(pages[page - 1].as_bytes().to_vec());
                }
                let version = self.version.lock().unwrap().clone();
                let zip = self.zip();
                let digest = if *self.missing_digest.lock().unwrap() {
                    None
                } else {
                    Some(format!("sha256:{}", self.digest_for(&zip)))
                };
                let url = if *self.malicious_url.lock().unwrap() {
                    "https://example.com/evil.exe".into()
                } else {
                    format!(
                        "https://github.com/microsoft/tgrep/releases/download/v{version}/tgrep-v{version}-{}-pc-windows-msvc.zip",
                        self.arch
                    )
                };
                let body = serde_json::json!([{
                    "tag_name": format!("v{version}"),
                    "draft": false,
                    "prerelease": *self.prerelease.lock().unwrap(),
                    "assets": [{
                        "name": format!("tgrep-v{version}-{}-pc-windows-msvc.zip", self.arch),
                        "digest": digest,
                        "size": zip.len(),
                        "browser_download_url": url
                    }]
                }]);
                return Ok(serde_json::to_vec(&body).unwrap());
            }
            *self.downloads.lock().unwrap() += 1;
            Ok(self.zip())
        }
        async fn version(&self, exe: &str) -> Result<Version> {
            Version::parse(&fs::read_to_string(exe).map_err(|e| e.to_string())?)
                .ok_or_else(|| "bad version".into())
        }
        async fn probe(&self, exe: &str, expected: Version) -> Result<()> {
            *self.probes.lock().unwrap() += 1;
            if *self.reject.lock().unwrap() {
                return Err("incompatible protocol".into());
            }
            let text = fs::read_to_string(exe).map_err(|e| e.to_string())?;
            if text != expected.to_string() {
                return Err("updater probes the extracted release".into());
            }
            Ok(())
        }
        async fn migrate(&self, _previous: &str, _candidate: &str) -> Result<()> {
            if *self.reject_migration.lock().unwrap() {
                Err("incompatible index".into())
            } else {
                Ok(())
            }
        }
    }

    fn zip_bytes(payload: &[u8], name: &str) -> Vec<u8> {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut writer = zip::ZipWriter::new(&mut cursor);
            writer
                .start_file(name, SimpleFileOptions::default())
                .unwrap();
            writer.write_all(payload).unwrap();
            writer.finish().unwrap();
        }
        cursor.into_inner()
    }
    fn release_json(version: &str, arch: &str, windows: bool, prerelease: bool, draft: bool) -> Value {
        let zip = zip_bytes(version.as_bytes(), "tgrep.exe");
        let name = format!("tgrep-v{version}-{arch}-pc-windows-msvc.zip");
        serde_json::json!({
            "tag_name": format!("v{version}"),
            "draft": draft,
            "prerelease": prerelease,
            "assets": if windows { serde_json::json!([{
                "name": name,
                "digest": format!("sha256:{}", hash_bytes(&zip)),
                "size": zip.len(),
                "browser_download_url": format!("https://github.com/microsoft/tgrep/releases/download/v{version}/{name}")
            }]) } else { serde_json::json!([]) }
        })
    }

    #[tokio::test]
    async fn managed_updates_match_winui_invariants() {
        let root = tempfile::tempdir().unwrap();
        let mock = Mock::new("x86_64");
        let updater = Updater::new(
            root.path().join("updater").join("x86_64"),
            "x86_64".into(),
            mock.clone(),
            |_| {},
        );
        let token = CancellationToken::new();
        assert!(updater.resolve(None).await.is_none());
        let result = updater.check(None, &token).await.unwrap();
        let first = updater.resolve(None).await;
        assert!(result.restart_required);
        assert!(result.message.contains("Restart the app") && first.is_some());
        assert_eq!(*mock.probes.lock().unwrap(), 2);
        let again = updater.check(None, &token).await.unwrap();
        assert!(again.restart_required);
        assert!(again.message.contains("already installed"));
        assert_eq!(*mock.downloads.lock().unwrap(), 1);
        let first_path = first.clone().unwrap();
        let manifest_path = updater.state_path();
        let manifest = fs::read_to_string(&manifest_path).unwrap();
        let current = updater.check(Some(&first_path), &token).await.unwrap();
        assert!(!current.restart_required);
        assert!(current.message.contains("No newer Windows engine"));
        assert_eq!(*mock.downloads.lock().unwrap(), 1);
        *mock.version.lock().unwrap() = "1.0.4".into();
        assert!(updater
            .check(Some(&first_path), &token)
            .await
            .unwrap()
            .message
            .contains("No newer Windows engine"));
        for arch in ["x86_64", "aarch64"] {
            let feed = serde_json::to_vec(&vec![
                release_json("1.0.6", arch, false, false, false),
                release_json("1.0.5", arch, true, false, false),
            ])
            .unwrap();
            let selection = select_release(&feed, arch, Version::parse("1.0.5").unwrap()).unwrap();
            assert!(selection.candidate.is_none());
            assert_eq!(selection.latest_without_windows, Version::parse("1.0.6"));
            let other = if arch == "x86_64" { "aarch64" } else { "x86_64" };
            let feed = serde_json::to_vec(&vec![
                release_json("1.0.8", arch, false, false, false),
                release_json("1.0.5", arch, true, false, false),
                release_json("1.0.9", arch, true, true, false),
                release_json("1.0.10", arch, true, false, true),
                release_json("1.0.7", arch, true, false, false),
                release_json("1.0.11", other, true, false, false),
            ])
            .unwrap();
            assert_eq!(
                select_release(&feed, arch, Version::parse("1.0.5").unwrap())
                    .unwrap()
                    .candidate
                    .unwrap()
                    .version,
                Version::parse("1.0.7").unwrap()
            );
        }
        *mock.feed_pages.lock().unwrap() = Some(vec![serde_json::to_string(&vec![
            release_json("1.0.6", "x86_64", false, false, false),
            release_json("1.0.5", "x86_64", true, false, false),
        ])
        .unwrap()]);
        let msg = updater.check(Some(&first_path), &token).await.unwrap();
        assert!(!msg.restart_required);
        assert!(msg.message.contains("No newer Windows engine")
            && msg.message.contains("keeping tgrep 1.0.5"));
        assert!(msg.message.contains("1.0.6 has no Windows package")
            && !msg.message.contains("unavailable"));
        assert_eq!(*mock.downloads.lock().unwrap(), 1);
        assert_eq!(fs::read_to_string(&manifest_path).unwrap(), manifest);
        *mock.version.lock().unwrap() = "1.0.7".into();
        let page1: Vec<Value> = (0..30)
            .map(|_| release_json("1.0.8", "x86_64", false, false, false))
            .collect();
        *mock.feed_pages.lock().unwrap() = Some(vec![
            serde_json::to_string(&page1).unwrap(),
            serde_json::to_string(&vec![
                release_json("1.0.7", "x86_64", true, false, false),
                release_json("1.0.5", "x86_64", true, false, false),
            ])
            .unwrap(),
        ]);
        let paged_dir = root.path().join("paged").join("x86_64");
        let paged = Updater::new(paged_dir, "x86_64".into(), mock.clone(), |_| {});
        let msg = paged.check(Some(&first_path), &token).await.unwrap();
        assert!(msg.restart_required);
        assert!(msg.message.contains("1.0.7 verified and ready")
            && msg.message.contains("Restart the app"));
        assert_eq!(fs::read_to_string(&first_path).unwrap(), "1.0.5");
        *mock.feed_pages.lock().unwrap() = None;
        *mock.version.lock().unwrap() = "1.0.6".into();
        *mock.bad_hash.lock().unwrap() = true;
        let before = *mock.probes.lock().unwrap();
        let msg = updater.check(Some(&first_path), &token).await.unwrap();
        assert!(!msg.restart_required);
        assert!(msg.message.contains("checksum mismatch"));
        assert_eq!(*mock.probes.lock().unwrap(), before);
        assert_eq!(fs::read_to_string(&manifest_path).unwrap(), manifest);
        *mock.bad_hash.lock().unwrap() = false;
        *mock.reject.lock().unwrap() = true;
        let msg = updater.check(Some(&first_path), &token).await.unwrap();
        assert!(!msg.restart_required);
        assert!(msg.message.contains("incompatible protocol"));
        assert_eq!(fs::read_to_string(&manifest_path).unwrap(), manifest);
        *mock.reject.lock().unwrap() = false;
        *mock.reject_migration.lock().unwrap() = true;
        let msg = updater.check(Some(&first_path), &token).await.unwrap();
        assert!(!msg.restart_required);
        assert!(msg.message.contains("incompatible index"));
        assert_eq!(fs::read_to_string(&manifest_path).unwrap(), manifest);
        *mock.reject_migration.lock().unwrap() = false;
        *mock.offline.lock().unwrap() = true;
        let msg = updater.check(Some(&first_path), &token).await.unwrap();
        assert!(!msg.restart_required);
        assert!(msg.message.contains("keeping the current engine"));
        assert_eq!(updater.resolve(None).await.as_deref(), Some(first_path.as_str()));
        *mock.offline.lock().unwrap() = false;
        *mock.prerelease.lock().unwrap() = true;
        let before = *mock.probes.lock().unwrap();
        assert!(updater
            .check(Some(&first_path), &token)
            .await
            .unwrap()
            .message
            .contains("No newer Windows engine"));
        assert_eq!(*mock.probes.lock().unwrap(), before);
        *mock.prerelease.lock().unwrap() = false;
        for (flag, needle) in [
            ("url", "invalid"),
            ("digest", "invalid"),
            ("entry", "exactly one root"),
        ] {
            *mock.malicious_url.lock().unwrap() = flag == "url";
            *mock.missing_digest.lock().unwrap() = flag == "digest";
            *mock.wrong_entry.lock().unwrap() = flag == "entry";
            let before = *mock.probes.lock().unwrap();
            let msg = updater.check(Some(&first_path), &token).await.unwrap();
            assert!(!msg.restart_required);
            assert!(
                msg.message.contains("keeping the current engine") || msg.message.contains(needle),
                "{flag}: {msg:?}"
            );
            assert_eq!(*mock.probes.lock().unwrap(), before, "{flag}");
            assert_eq!(fs::read_to_string(&manifest_path).unwrap(), manifest);
        }
        *mock.malicious_url.lock().unwrap() = false;
        *mock.missing_digest.lock().unwrap() = false;
        *mock.wrong_entry.lock().unwrap() = false;
        {
            let _gate = lock_update(&updater.root.join("update.lock")).unwrap();
            assert!(updater
                .check(Some(&first_path), &token)
                .await
                .unwrap()
                .message
                .contains("Another app instance"));
        }
        let cancelled = CancellationToken::new();
        cancelled.cancel();
        assert_eq!(
            updater.check(Some(&first_path), &cancelled).await.unwrap_err(),
            "Cancelled"
        );
        assert_eq!(fs::read_to_string(&manifest_path).unwrap(), manifest);
        updater.check(Some(&first_path), &token).await.unwrap();
        let second = updater.resolve(None).await.unwrap();
        assert_ne!(second, first_path);
        assert_eq!(fs::read_to_string(&first_path).unwrap(), "1.0.5");
        assert!(updater
            .resolve(Some(Version::parse("1.0.7").unwrap()))
            .await
            .is_none());
        fs::write(&second, "corrupted").unwrap();
        assert_eq!(
            updater.resolve(None).await.as_deref(),
            Some(first_path.as_str())
        );
        fs::write(&manifest_path, "{not json").unwrap();
        assert!(updater.resolve(None).await.is_none());
        assert!(!fs::read_dir(updater.root.parent().unwrap_or(&updater.root))
            .ok()
            .into_iter()
            .flatten()
            .filter_map(|e| e.ok())
            .any(|e| e
                .file_name()
                .to_string_lossy()
                .starts_with("stage-")));
    }

    #[test]
    fn version_parser_rejects_prerelease_tags() {
        assert!(Version::parse("1.0.5").is_some());
        assert!(Version::parse("1.0.5-rc.1").is_none());
        assert!(Version::parse("v1.0.5").is_none());
    }
}
