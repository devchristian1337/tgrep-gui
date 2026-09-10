import { invoke, isTauri, Channel } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import {
  defaults,
  type Settings,
  type SearchOptions,
  type SearchEvent,
  type Outcome,
  type Preview,
} from "./types";
export const native = isTauri();
export const api = {
  settings: () =>
    native
      ? invoke<Settings>("load_settings")
      : Promise.resolve({
          ...defaults,
          ...JSON.parse(localStorage.getItem("tgrep-preview-settings") || "{}"),
        }),
  save: (settings: Settings) =>
    native
      ? invoke<void>("save_settings", { settings })
      : Promise.resolve(
          localStorage.setItem(
            "tgrep-preview-settings",
            JSON.stringify(settings),
          ),
        ),
  prefill: (): Promise<Partial<SearchOptions>> =>
    native
      ? invoke<Partial<SearchOptions>>("get_prefill")
      : Promise.resolve({}),
  version: (settings: Settings) =>
    native
      ? invoke<string>("engine_version", { settings })
      : Promise.resolve("Browser preview"),
  browse: (directory: boolean) => open({ directory, multiple: false }),
  search: (
    id: number,
    options: SearchOptions,
    settings: Settings,
    receive: (e: SearchEvent) => void,
  ) => {
    const onEvent = new Channel<SearchEvent>();
    onEvent.onmessage = receive;
    return invoke<Outcome>("search", { id, options, settings, onEvent });
  },
  cancel: () => invoke<void>("cancel_search"),
  preview: (path: string) => invoke<Preview>("preview", { path }),
  logs: () => (native ? invoke<string[]>("get_logs") : Promise.resolve([])),
  restart: () => invoke<void>("restart_server"),
  open: (
    path: string,
    line: number,
    settings: Settings,
    containingFolder = false,
  ) => invoke<void>("open_result", { path, line, settings, containingFolder }),
};
