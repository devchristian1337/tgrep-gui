export interface Settings {
  enginePath: string;
  indexPath: string;
  editorPath: string;
  editorArguments: string;
  theme: string;
  accent: string;
  scale: number;
  density: string;
  ignoreCase: boolean;
  literal: boolean;
  recentFolders: string[];
  autoUpdateEngine: boolean;
}
export interface SearchOptions {
  folder: string;
  pattern: string;
  include: string;
  exclude: string;
  ignoreCase: boolean;
  literal: boolean;
  wholeWord: boolean;
  useIndex: boolean;
}
export interface Hit {
  path: string;
  relativePath: string;
  count: number;
}
export interface MatchLine {
  number: number;
  text: string;
  spans: [number, number][];
  count: number;
}
export interface Preview {
  lines: MatchLine[];
  truncated: boolean;
}
export interface Outcome {
  cancelled: boolean;
  elapsedMs: number;
  files: number;
  matches: number;
  truncated: boolean;
}
export type SearchEvent =
  | { type: "hits"; id: number; hits: Hit[] }
  | { type: "progress"; id: number; message: string };
export const defaults: Settings = {
  enginePath: "",
  indexPath: "",
  editorPath: "",
  editorArguments: '"$FILE"',
  theme: "system",
  accent: "cobalt",
  scale: 1,
  density: "comfortable",
  ignoreCase: true,
  literal: false,
  recentFolders: [],
  autoUpdateEngine: true,
};
export const emptyOptions: SearchOptions = {
  folder: "",
  pattern: "",
  include: "",
  exclude: "",
  ignoreCase: true,
  literal: false,
  wholeWord: false,
  useIndex: true,
};
