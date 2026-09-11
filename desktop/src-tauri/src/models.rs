use serde::{Deserialize, Serialize};

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub engine_path: String,
    pub index_path: String,
    pub editor_path: String,
    pub editor_arguments: String,
    pub theme: String,
    pub accent: String,
    pub scale: f64,
    pub density: String,
    pub ignore_case: bool,
    pub literal: bool,
    pub recent_folders: Vec<String>,
    #[serde(default = "default_auto_update")]
    pub auto_update_engine: bool,
}
fn default_auto_update() -> bool {
    true
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            engine_path: String::new(),
            index_path: String::new(),
            editor_path: String::new(),
            editor_arguments: "\"$FILE\"".into(),
            theme: "system".into(),
            accent: "cobalt".into(),
            scale: 1.0,
            density: "comfortable".into(),
            ignore_case: true,
            literal: false,
            recent_folders: vec![],
            auto_update_engine: true,
        }
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchOptions {
    pub folder: String,
    pub pattern: String,
    pub include: String,
    pub exclude: String,
    pub ignore_case: bool,
    pub literal: bool,
    pub whole_word: bool,
    pub use_index: bool,
}
#[derive(Clone, Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Hit {
    pub path: String,
    pub relative_path: String,
    pub count: usize,
}
#[derive(Clone, Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct MatchLine {
    pub number: u64,
    pub text: String,
    pub spans: Vec<(usize, usize)>,
    pub count: usize,
}
#[derive(Clone, Serialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum SearchEvent {
    Hits { id: u64, hits: Vec<Hit> },
    Progress { id: u64, message: String },
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Outcome {
    pub cancelled: bool,
    pub elapsed_ms: u128,
    pub files: usize,
    pub matches: usize,
    pub truncated: bool,
}
#[derive(Serialize)]
pub struct Preview {
    pub lines: Vec<MatchLine>,
    pub truncated: bool,
}
