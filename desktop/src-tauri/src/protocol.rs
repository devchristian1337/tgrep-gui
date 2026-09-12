use crate::models::{MatchLine, SearchOptions};
use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::Value;
use std::path::{Path, PathBuf};

pub fn split_globs(input: &str) -> Result<Vec<String>, String> {
    let (mut out, mut token, mut quote, mut braces, mut brackets) =
        (vec![], String::new(), None, 0usize, 0usize);
    for c in input.chars() {
        if let Some(q) = quote {
            if c == q {
                quote = None;
            } else {
                token.push(c);
            }
            continue;
        }
        match c {
            '\'' | '"' => {
                quote = Some(c);
                continue;
            }
            '{' => braces += 1,
            '}' => braces = braces.saturating_sub(1),
            '[' => brackets += 1,
            ']' => brackets = brackets.saturating_sub(1),
            _ => {}
        }
        if (c.is_whitespace() || c == ',' || c == ';') && braces == 0 && brackets == 0 {
            if !token.is_empty() {
                out.push(std::mem::take(&mut token));
            }
        } else {
            token.push(c);
        }
    }
    if quote.is_some() {
        return Err("Unclosed quotes in file filters.".into());
    }
    if !token.is_empty() {
        out.push(token);
    }
    Ok(out)
}
pub fn search_args(
    o: &SearchOptions,
    index: &str,
    file: Option<&str>,
) -> Result<Vec<String>, String> {
    let mut a: Vec<String> = ["--json", "--line-buffered", "--color", "never", "-n"]
        .map(str::to_string)
        .into();
    for (yes, flag) in [
        (o.ignore_case, "-i"),
        (o.literal, "-F"),
        (o.whole_word, "-w"),
        (!o.use_index, "--no-index"),
    ] {
        if yes {
            a.push(flag.into());
        }
    }
    if file.is_some() {
        a.extend(["-m".into(), "10001".into()]);
    } else {
        for g in split_globs(&o.include)? {
            a.extend(["-g".into(), g]);
        }
        for g in split_globs(&o.exclude)? {
            let mut g = g.trim_start_matches('!').replace('\\', "/");
            if g.ends_with('/') {
                // A bare directory name applies at every depth. Explicit paths
                // keep their existing scope.
                if !g[..g.len() - 1].contains('/') && g.len() > 1 {
                    g = format!("**/{g}");
                }
                g.push_str("**");
            }
            a.extend(["-g".into(), format!("!{g}")]);
        }
    }
    if !index.is_empty() {
        a.extend(["--index-path".into(), index.into()]);
    }
    a.extend([
        "--".into(),
        o.pattern.clone(),
        file.unwrap_or(&o.folder).into(),
    ]);
    Ok(a)
}
fn decode(v: &Value) -> Result<String, String> {
    if let Some(s) = v["text"].as_str() {
        return Ok(s.into());
    }
    if let Some(s) = v["bytes"].as_str() {
        return STANDARD
            .decode(s)
            .map(|b| String::from_utf8_lossy(&b).into_owned())
            .map_err(|e| e.to_string());
    }
    Err("Engine JSON field has neither text nor bytes.".into())
}
pub fn parse_match(json: &str, root: &Path) -> Result<Option<(PathBuf, MatchLine)>, String> {
    let v: Value = serde_json::from_str(json).map_err(|e| format!("Invalid engine JSON: {e}"))?;
    if v["type"] != "match" {
        return Ok(None);
    }
    let d = &v["data"];
    let raw = PathBuf::from(decode(&d["path"])?);
    let path = if raw.is_absolute() {
        raw
    } else {
        root.join(raw)
    };
    let original = decode(&d["lines"])?;
    let path = dunce::simplified(&path).to_path_buf();
    let text = original.trim_end_matches(['\r', '\n']).to_string();
    let mut spans = vec![];
    let subs = d["submatches"].as_array();
    if let Some(subs) = subs {
        // tgrep emits ordered spans. Advance through the text once instead of
        // recounting every prefix; still accept overlapping/unordered spans.
        let (mut byte_offset, mut utf16_offset) = (0, 0);
        let mut offset = |byte: usize| {
            if byte < byte_offset {
                byte_offset = 0;
                utf16_offset = 0;
            }
            utf16_offset += text[byte_offset..byte].encode_utf16().count();
            byte_offset = byte;
            utf16_offset
        };
        for s in subs {
            let start = s["start"].as_u64().unwrap_or(0) as usize;
            let end = s["end"].as_u64().unwrap_or(0) as usize;
            if start < end
                && end <= text.len()
                && text.is_char_boundary(start)
                && text.is_char_boundary(end)
            {
                spans.push((offset(start), offset(end)));
            }
        }
    }
    Ok(Some((
        path,
        MatchLine {
            number: d["line_number"].as_u64().unwrap_or(1),
            text,
            spans,
            count: subs.map_or(1, |s| s.len().max(1)),
        },
    )))
}
#[derive(Debug)]
pub struct Status {
    pub has_index: bool,
    pub running: bool,
    pub indexing: bool,
    pub description: String,
}
pub fn parse_status(text: &str) -> Result<Status, String> {
    let lower = text.to_lowercase();
    let running = lower.contains("server status for");
    let has_index = running || lower.contains("index status for");
    if !has_index && !lower.contains("no index found") {
        return Err(format!("Unrecognized engine status: {text}"));
    }
    let indexing = lower.lines().any(|l| {
        l.trim()
            .strip_prefix("indexing:")
            .is_some_and(|v| !v.trim().starts_with("complete"))
    });
    Ok(Status {
        has_index,
        running,
        indexing,
        description: text.trim().into(),
    })
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "manual performance measurement"]
    fn benchmark_dense_unicode_matches() {
        let json = serde_json::json!({"type":"match","data":{
            "path":{"text":"dense.rs"}, "lines":{"text":"é😀x".repeat(10000)},
            "submatches":(0..10000).map(|i| serde_json::json!({"start":i*7,"end":i*7+7})).collect::<Vec<_>>()
        }}).to_string();
        let start = std::time::Instant::now();
        for _ in 0..5 {
            let (_, line) = parse_match(std::hint::black_box(&json), Path::new(".")).unwrap().unwrap();
            assert_eq!(line.spans.len(), 10000);
            assert_eq!(line.spans[9999], (39996, 40000));
            std::hint::black_box(line);
        }
        println!("dense Unicode parse: {:.3} ms/record", start.elapsed().as_secs_f64() * 1000.0 / 5.0);
    }
    #[test]
    fn query_is_not_parsed_as_an_option_and_directory_exclusions_expand() {
        let o = SearchOptions {
            folder: "/tmp/project".into(),
            pattern: "--help".into(),
            include: "*.{rs,ts}".into(),
            exclude: "target/".into(),
            ignore_case: true,
            literal: true,
            whole_word: false,
            use_index: false,
        };
        let args = search_args(&o, "", None).unwrap();
        let end = args.iter().position(|a| a == "--").unwrap();
        assert_eq!(&args[end + 1..], &["--help", "/tmp/project"]);
        assert!(args.iter().any(|a| a == "!**/target/**"));
        assert!(args.iter().any(|a| a == "*.{rs,ts}"));
        assert!(args.iter().any(|a| a == "--no-index"));
    }
    #[test]
    fn filters_preserve_braces_and_spaces() {
        assert_eq!(
            split_globs("*.{rs,ts}; 'my files/**', [a,b]*").unwrap(),
            vec!["*.{rs,ts}", "my files/**", "[a,b]*"]
        );
        assert!(split_globs("'broken").is_err());
    }
    #[test]
    fn unicode_offsets_are_utf16() {
        let json = r#"{"type":"match","data":{"path":{"text":"file.rs"},"lines":{"text":"é😀hello\n"},"line_number":8,"submatches":[{"start":6,"end":11}]}}"#;
        let (_, m) = parse_match(json, Path::new("/tmp")).unwrap().unwrap();
        assert_eq!(m.spans, vec![(3, 8)]);
        assert_eq!(m.text, "é😀hello");
    }
    #[test]
    fn unicode_offsets_preserve_overlaps_order_and_invalid_span_handling() {
        let text = "é😀hello";
        let ranges = [(6, 11), (2, 6), (0, 6), (1, 2), (6, 99), (6, 6), (0, 2)];
        let json = serde_json::json!({"type":"match","data":{
            "path":{"text":"file.rs"}, "lines":{"text":format!("{text}\r\n")},
            "submatches":ranges.iter().map(|(start,end)| serde_json::json!({"start":start,"end":end})).collect::<Vec<_>>()
        }}).to_string();
        let (_, line) = parse_match(&json, Path::new(".")).unwrap().unwrap();
        assert_eq!(line.spans, vec![(3, 8), (1, 3), (0, 3), (0, 1)]);
        assert_eq!(line.text, text);
        assert_eq!(line.count, ranges.len());
    }
    #[test]
    fn base64_and_non_match() {
        let json = r#"{"type":"match","data":{"path":{"bytes":"YS5ycw=="},"lines":{"bytes":"aGkK"},"submatches":[]}}"#;
        assert_eq!(
            parse_match(json, Path::new("/tmp"))
                .unwrap()
                .unwrap()
                .1
                .text,
            "hi"
        );
        assert!(parse_match(r#"{"type":"summary"}"#, Path::new("/tmp"))
            .unwrap()
            .is_none());
    }
    #[test]
    fn unknown_status_is_not_missing() {
        assert!(parse_status("garbage").is_err());
        assert!(!parse_status("No index found").unwrap().has_index);
        assert!(
            parse_status("Server status for x\nIndexing: complete")
                .unwrap()
                .running
        );
    }
}
