import { useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { Minus, Square, Copy, X } from "lucide-react";
import { Tooltip } from "@/components/ui/beui-tooltip";

export default function WindowControls({
  onError,
}: {
  onError: (message: string) => void;
}) {
  const [mainWindow] = useState(getCurrentWindow);
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    let live = true;
    const refresh = async () => {
      try {
        const value = await mainWindow.isMaximized();
        if (live) setMaximized(value);
      } catch (error) {
        if (live) onError(String(error));
      }
    };
    void refresh();
    const listener = mainWindow.onResized(() => void refresh());
    void listener.catch((error) => {
      if (live) onError(String(error));
    });
    return () => {
      live = false;
      void listener.then((unlisten) => unlisten()).catch(() => {});
    };
  }, [mainWindow, onError]);
  const run = async (action: () => Promise<void>) => {
    try {
      await action();
    } catch (error) {
      onError(String(error));
    }
  };
  return (
    <div className="window-controls" aria-label="Window controls">
      <Tooltip content="Minimize" side="bottom" wrapperClassName="h-full">
        <button
          aria-label="Minimize window"
          onClick={() => void run(() => mainWindow.minimize())}
        >
          <Minus size={14} />
        </button>
      </Tooltip>
      <Tooltip
        content={maximized ? "Restore" : "Maximize"}
        side="bottom"
        wrapperClassName="h-full"
      >
        <button
          aria-label={maximized ? "Restore window" : "Maximize window"}
          onClick={() =>
            void run(async () => {
              await mainWindow.toggleMaximize();
              setMaximized(await mainWindow.isMaximized());
            })
          }
        >
          {maximized ? <Copy size={13} /> : <Square size={13} />}
        </button>
      </Tooltip>
      <Tooltip content="Close" side="bottom" wrapperClassName="h-full">
        <button
          className="window-close"
          aria-label="Close window"
          onClick={() => void run(() => mainWindow.close())}
        >
          <X size={16} />
        </button>
      </Tooltip>
    </div>
  );
}
