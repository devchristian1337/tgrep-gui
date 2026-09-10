use crate::{models::*, protocol::*};
use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    process::Stdio,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
use tauri::ipc::Channel;
use tokio::{
    io::{AsyncBufReadExt, BufReader},
    process::{Child, Command},
    sync::Mutex as AsyncMutex,
};
use tokio_util::sync::CancellationToken;

pub type Result<T> = std::result::Result<T, String>;
#[derive(Clone)]
pub struct Context {
    pub options: SearchOptions,
    pub settings: Settings,
    pub allowed: HashSet<String>,
}
pub struct Server {
    child: Child,
    exe: String,
    index: String,
}
#[derive(Default)]
pub struct Engine {
    pub active: Mutex<Option<(u64, CancellationToken)>>,
    pub preview: Mutex<Option<CancellationToken>>,
    pub context: Mutex<Option<Context>>,
    pub servers: AsyncMutex<HashMap<String, Server>>,
    pub logs: Arc<Mutex<Vec<String>>>,
}
impl Engine {
    pub fn log(&self, text: impl Into<String>) {
        let mut logs = self.logs.lock().unwrap();
        logs.push(text.into());
        if logs.len() > 1000 {
            logs.remove(0);
        }
    }
    pub fn cancel(&self) {
        if let Some((_, t)) = &*self.active.lock().unwrap() {
            t.cancel();
        }
    }
    pub fn cancel_preview(&self) {
        if let Some(t) = &*self.preview.lock().unwrap() {
            t.cancel();
        }
    }
    pub async fn shutdown(&self) {
        self.cancel();
        self.cancel_preview();
        for (_, mut server) in self.servers.lock().await.drain() {
            let _ = server.child.kill().await;
        }
    }
}
pub fn command(exe: &str) -> Command {
    let mut c = Command::new(exe);
    c.kill_on_drop(true).stdin(Stdio::null());
    #[cfg(windows)]
    c.creation_flags(0x08000000);
    c
}
pub fn discover(configured: &str) -> Result<String> {
    if !configured.trim().is_empty() {
        let p = PathBuf::from(configured.trim().trim_matches('"'));
        return if p.is_file() {
            Ok(dunce::canonicalize(p)
                .map_err(|e| e.to_string())?
                .to_string_lossy()
                .into())
        } else {
            Err("Configured tgrep executable does not exist. Choose it in Settings.".into())
        };
    }
    let name = if cfg!(windows) { "tgrep.exe" } else { "tgrep" };
    let mut candidates = vec![];
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join(name));
        }
    }
    #[cfg(debug_assertions)]
    candidates.push(
        Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../.tools/tgrep")
            .join(name),
    );
    if let Some(paths) = std::env::var_os("PATH") {
        candidates.extend(std::env::split_paths(&paths).map(|p| p.join(name)));
    }
    candidates
        .into_iter()
        .find(|p| p.is_file())
        .map(|p| p.to_string_lossy().into())
        .ok_or("tgrep was not found. Select the executable in Settings or put it on PATH.".into())
}
pub fn validate(o: &mut SearchOptions, s: &Settings) -> Result<()> {
    let root = dunce::canonicalize(o.folder.trim().trim_matches('"'))
        .map_err(|e| format!("Cannot open project folder: {e}"))?;
    if !root.is_dir() {
        return Err("Select a directory to search.".into());
    }
    if o.pattern.is_empty() {
        return Err("Enter a search pattern.".into());
    }
    o.folder = root.to_string_lossy().into();
    search_args(o, &s.index_path, None)?;
    if !o.use_index {
        return Ok(());
    }
    let index = if s.index_path.is_empty() {
        root.join(".tgrep")
    } else {
        PathBuf::from(&s.index_path)
    };
    if !index.is_absolute() {
        return Err("Index path must be absolute.".into());
    }
    if index == root || dunce::canonicalize(&index).ok().as_ref() == Some(&root) {
        return Err("Index must be in a dedicated directory.".into());
    }
    let meta = index.join("meta.json");
    if meta.exists() {
        let bytes = std::fs::read(meta).map_err(|e| e.to_string())?;
        let v: serde_json::Value = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        if let Some(path) = v["root_path"].as_str() {
            if dunce::canonicalize(path).map_err(|e| e.to_string())? != root {
                return Err(
                    "This index belongs to another project. Choose a dedicated index directory."
                        .into(),
                );
            }
        }
    }
    Ok(())
}

// Drain both pipes concurrently, kill only the child we started, and reap on every exit.
pub async fn run(
    exe: &str,
    args: &[String],
    root: &str,
    token: &CancellationToken,
    logs: Arc<Mutex<Vec<String>>>,
    mut on_line: impl FnMut(String) -> Result<()>,
) -> Result<i32> {
    if token.is_cancelled() {
        return Err("Cancelled".into());
    }
    let mut child = command(exe)
        .args(args)
        .current_dir(root)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("Cannot start tgrep: {e}"))?;
    let err = child.stderr.take().unwrap();
    let stderr = tokio::spawn(async move {
        let mut lines = BufReader::new(err).lines();
        let mut tail = String::new();
        while let Ok(Some(line)) = lines.next_line().await {
            tail.push_str(&line);
            tail.push('\n');
            if tail.len() > 8192 {
                tail = tail
                    .chars()
                    .rev()
                    .take(4096)
                    .collect::<String>()
                    .chars()
                    .rev()
                    .collect();
            }
            let mut l = logs.lock().unwrap();
            l.push(line);
            if l.len() > 1000 {
                l.remove(0);
            }
        }
        tail
    });
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();
    let result:Result<()> = async {loop{tokio::select! {biased; _=token.cancelled()=>return Err("Cancelled".into()), line=lines.next_line()=>match line.map_err(|e|e.to_string())? {Some(line)=>on_line(line)?,None=>return Ok(())}}}}.await;
    if result.is_err() {
        let _ = child.kill().await;
    }
    let status=tokio::select!{_ = token.cancelled()=>{let _=child.kill().await;child.wait().await},s=child.wait()=>s}.map_err(|e|e.to_string());
    let errors = stderr.await.map_err(|e| e.to_string())?;
    result?;
    if token.is_cancelled() {
        return Err("Cancelled".into());
    }
    let code = status?.code().unwrap_or(-1);
    if code > 1 || code < 0 {
        return Err(format!("tgrep exited with code {code}. {}", errors.trim()));
    }
    Ok(code)
}
fn verb_args(verb: &str, root: &str, index: &str) -> Vec<String> {
    let mut a = vec![verb.into(), root.into()];
    if !index.is_empty() {
        a.extend(["--index-path".into(), index.into()]);
    }
    a
}
async fn status(
    engine: &Engine,
    exe: &str,
    o: &SearchOptions,
    s: &Settings,
    token: &CancellationToken,
) -> Result<Status> {
    let mut text = String::new();
    let args = verb_args("status", &o.folder, &s.index_path);
    let call = run(exe, &args, &o.folder, token, engine.logs.clone(), |l| {
        text.push_str(&l);
        text.push('\n');
        Ok(())
    });
    tokio::time::timeout(Duration::from_secs(5), call)
        .await
        .map_err(|_| "Engine status timed out.".to_string())??;
    parse_status(&text)
}
pub async fn prepare(
    engine: &Engine,
    exe: &str,
    o: &SearchOptions,
    s: &Settings,
    token: &CancellationToken,
    progress: impl Fn(String),
) -> Result<()> {
    let mut servers = tokio::select! {_ = token.cancelled()=>return Err("Cancelled".into()),s=engine.servers.lock()=>s};
    if let Some(server) = servers.get_mut(&o.folder) {
        if server.exe != exe
            || server.index != s.index_path
            || server
                .child
                .try_wait()
                .map_err(|e| e.to_string())?
                .is_some()
        {
            let _ = server.child.kill().await;
            servers.remove(&o.folder);
        }
    }
    progress("Checking index…".into());
    let mut st = status(engine, exe, o, s, token).await?;
    if !st.running {
        if !st.has_index {
            progress("Building index…".into());
            let code = run(
                exe,
                &verb_args("index", &o.folder, &s.index_path),
                &o.folder,
                token,
                engine.logs.clone(),
                |l| {
                    progress(l);
                    Ok(())
                },
            )
            .await?;
            if code != 0 {
                return Err("Index creation failed. See Log.".into());
            }
        }
        if let Some(mut old) = servers.remove(&o.folder) {
            let _ = old.child.kill().await;
        }
        if servers.len() >= 2 {
            if let Some(key) = servers.keys().next().cloned() {
                if let Some(mut old) = servers.remove(&key) {
                    let _ = old.child.kill().await;
                }
            }
        }
        progress("Starting search server…".into());
        let mut child = command(exe)
            .args(verb_args("serve", &o.folder, &s.index_path))
            .current_dir(&o.folder)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|e| e.to_string())?;
        let out = child.stdout.take().unwrap();
        let err = child.stderr.take().unwrap();
        let logs = engine.logs.clone();
        let logs2 = logs.clone();
        tokio::spawn(async move {
            let mut lines = BufReader::new(out).lines();
            while let Ok(Some(l)) = lines.next_line().await {
                let mut log = logs.lock().unwrap();
                log.push(l);
                if log.len() > 1000 {
                    log.remove(0);
                }
            }
        });
        tokio::spawn(async move {
            let mut lines = BufReader::new(err).lines();
            while let Ok(Some(l)) = lines.next_line().await {
                let mut log = logs2.lock().unwrap();
                log.push(l);
                if log.len() > 1000 {
                    log.remove(0);
                }
            }
        });
        servers.insert(
            o.folder.clone(),
            Server {
                child,
                exe: exe.into(),
                index: s.index_path.clone(),
            },
        );
    }
    let started = Instant::now();
    while !st.running || st.indexing {
        if !st.running && started.elapsed() > Duration::from_secs(30) {
            return Err("Server did not become ready within 30 seconds.".into());
        }
        tokio::select! {_ = token.cancelled()=>return Err("Cancelled".into()),_ = tokio::time::sleep(Duration::from_millis(400))=>{}}
        st = status(engine, exe, o, s, token).await?;
        progress(if st.indexing {
            "Indexing project…".into()
        } else {
            "Connecting to index…".into()
        });
    }
    engine.log(st.description);
    Ok(())
}
pub async fn search(
    engine: &Engine,
    id: u64,
    mut o: SearchOptions,
    mut s: Settings,
    channel: Channel<SearchEvent>,
    token: CancellationToken,
) -> Result<Outcome> {
    let started = Instant::now();
    validate(&mut o, &s)?;
    s.engine_path = discover(&s.engine_path)?;
    let exe = s.engine_path.clone();
    *engine.context.lock().unwrap() = Some(Context {
        options: o.clone(),
        settings: s.clone(),
        allowed: HashSet::new(),
    });
    let progress = |message| {
        let _ = channel.send(SearchEvent::Progress { id, message });
    };
    let mut matches = 0usize;
    let mut counts: HashMap<String, Hit> = HashMap::new();
    let mut batch = Vec::new();
    let mut truncated = false;
    let mut tick = Instant::now();
    let result: Result<()> = async {
        if o.use_index {
            prepare(engine, &exe, &o, &s, &token, &progress).await?;
        }
        progress("Searching…".into());
        run(
            &exe,
            &search_args(&o, &s.index_path, None)?,
            &o.folder,
            &token,
            engine.logs.clone(),
            |line| {
                if let Some((path, m)) = parse_match(&line, Path::new(&o.folder))? {
                    let path = path.to_string_lossy().into_owned();
                    if counts.len() >= 100_000 && !counts.contains_key(&path) {
                        truncated = true;
                        return Err(
                            "Result limit reached (100,000 files). Narrow your search.".into()
                        );
                    }
                    matches += m.count;
                    let h = counts.entry(path.clone()).or_insert_with(|| Hit {
                        relative_path: Path::new(&path)
                            .strip_prefix(&o.folder)
                            .unwrap_or(Path::new(&path))
                            .to_string_lossy()
                            .into(),
                        path: path.clone(),
                        count: 0,
                    });
                    h.count += m.count;
                    batch.push(Hit {
                        path,
                        relative_path: h.relative_path.clone(),
                        count: m.count,
                    });
                    if batch.len() >= 256 || tick.elapsed() > Duration::from_millis(60) {
                        channel
                            .send(SearchEvent::Hits {
                                id,
                                hits: std::mem::take(&mut batch),
                            })
                            .map_err(|e| e.to_string())?;
                        tick = Instant::now();
                    }
                }
                Ok(())
            },
        )
        .await?;
        Ok(())
    }
    .await;
    if !batch.is_empty() {
        let _ = channel.send(SearchEvent::Hits { id, hits: batch });
    }
    if let Some(c) = engine.context.lock().unwrap().as_mut() {
        c.allowed = counts.keys().cloned().collect();
    }
    let cancelled = token.is_cancelled();
    if !cancelled && !truncated {
        result?;
    }
    Ok(Outcome {
        cancelled,
        elapsed_ms: started.elapsed().as_millis(),
        files: counts.len(),
        matches,
        truncated,
    })
}
pub async fn preview(engine: &Engine, path: &str, token: &CancellationToken) -> Result<Preview> {
    let c = engine
        .context
        .lock()
        .unwrap()
        .clone()
        .ok_or("Run a search first.")?;
    if !c.allowed.contains(path) {
        return Err("File is not part of the current search results.".into());
    }
    let mut lines = Vec::new();
    let mut options = c.options.clone();
    options.use_index = false;
    run(
        &c.settings.engine_path,
        &search_args(&options, "", Some(path))?,
        &options.folder,
        token,
        engine.logs.clone(),
        |line| {
            if let Some((_, m)) = parse_match(&line, Path::new(&options.folder))? {
                if lines.len() < 10001 {
                    lines.push(m);
                }
            }
            Ok(())
        },
    )
    .await?;
    let truncated = lines.len() > 10000;
    lines.truncate(10000);
    Ok(Preview { lines, truncated })
}
pub async fn restart(engine: &Engine, token: &CancellationToken) -> Result<()> {
    let c = engine
        .context
        .lock()
        .unwrap()
        .clone()
        .ok_or("Run an indexed search first.")?;
    let mut servers = engine.servers.lock().await;
    let mut server = servers
        .remove(&c.options.folder)
        .ok_or("This app does not own the server. External servers cannot be restarted here.")?;
    server.child.kill().await.map_err(|e| e.to_string())?;
    drop(servers);
    prepare(
        engine,
        &c.settings.engine_path,
        &c.options,
        &c.settings,
        token,
        |m| engine.log(m),
    )
    .await
}

#[cfg(test)]
mod tests {
    use super::*;
    fn options(folder: &Path) -> SearchOptions {
        SearchOptions {
            folder: folder.to_string_lossy().into(),
            pattern: "hello".into(),
            include: "*.rs".into(),
            exclude: String::new(),
            ignore_case: true,
            literal: true,
            whole_word: false,
            use_index: false,
        }
    }
    #[tokio::test]
    async fn cancelled_run_never_starts_a_process() {
        let token = CancellationToken::new();
        token.cancel();
        let err = run(
            "missing-executable",
            &[],
            ".",
            &token,
            Arc::default(),
            |_| Ok(()),
        )
        .await
        .unwrap_err();
        assert_eq!(err, "Cancelled");
    }
    #[tokio::test]
    async fn preview_rejects_files_outside_results() {
        let engine = Engine::default();
        *engine.context.lock().unwrap() = Some(Context {
            options: options(Path::new(".")),
            settings: Settings::default(),
            allowed: HashSet::new(),
        });
        assert!(
            preview(&engine, "unrelated-file", &CancellationToken::new())
                .await
                .is_err()
        );
    }
    #[test]
    fn custom_index_cannot_point_at_the_project() {
        let dir = tempfile::tempdir().unwrap();
        let mut o = options(dir.path());
        o.use_index = true;
        let s = Settings {
            index_path: dunce::canonicalize(dir.path())
                .unwrap()
                .to_string_lossy()
                .into(),
            ..Settings::default()
        };
        assert!(validate(&mut o, &s).unwrap_err().contains("dedicated"));
    }
    #[tokio::test]
    #[ignore = "requires TGREP_TEST_EXE pointing to an installed Microsoft tgrep"]
    async fn real_engine_search_preview_index_reuse_and_shutdown() {
        let exe = std::env::var("TGREP_TEST_EXE").expect("Set TGREP_TEST_EXE");
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("hello.rs"), "é😀hello\nHELLO\n").unwrap();
        std::fs::write(dir.path().join("excluded.txt"), "hello\n").unwrap();
        let engine = Engine::default();
        let s = Settings {
            engine_path: exe,
            ..Settings::default()
        };
        let mut o = options(dir.path());
        let outcome = search(
            &engine,
            1,
            o.clone(),
            s.clone(),
            Channel::new(|_| Ok(())),
            CancellationToken::new(),
        )
        .await
        .unwrap();
        assert_eq!((outcome.files, outcome.matches), (1, 2));
        let path = engine
            .context
            .lock()
            .unwrap()
            .as_ref()
            .unwrap()
            .allowed
            .iter()
            .next()
            .unwrap()
            .clone();
        let p = preview(&engine, &path, &CancellationToken::new())
            .await
            .unwrap();
        assert_eq!(p.lines[0].spans, vec![(3, 8)]);
        o.use_index = true;
        let indexed = search(
            &engine,
            2,
            o.clone(),
            s.clone(),
            Channel::new(|_| Ok(())),
            CancellationToken::new(),
        )
        .await
        .unwrap();
        assert_eq!(indexed.matches, 2);
        let pid = engine
            .servers
            .lock()
            .await
            .values()
            .next()
            .unwrap()
            .child
            .id();
        search(
            &engine,
            3,
            o,
            s,
            Channel::new(|_| Ok(())),
            CancellationToken::new(),
        )
        .await
        .unwrap();
        assert_eq!(
            engine
                .servers
                .lock()
                .await
                .values()
                .next()
                .unwrap()
                .child
                .id(),
            pid
        );
        engine.shutdown().await;
        assert!(engine.servers.lock().await.is_empty());
    }
}
