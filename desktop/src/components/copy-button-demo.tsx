"use client";

import { CopyButton } from "@/components/ui/copy-button";

// Standalone preview of the copy button; it is not mounted in the desktop app.
export default function CopyButtonDemo() {
  return (
    <div className="flex flex-col items-center gap-12">
      <div className="flex flex-wrap items-center justify-center gap-4">
        <CopyButton value="Thank you for using Shadcn Studio!" />
        <CopyButton
          value="Thank you for using Shadcn Studio!"
          variant="secondary"
          size="sm"
          label="Copy line"
          copiedLabel="Copied"
        />
        <CopyButton
          value="Thank you for using Shadcn Studio!"
          variant="ghost"
          label="Copy path"
        />
      </div>
      <p className="text-xs text-muted-foreground">
        Click to copy. The clipboard icon crossfades into a check and the label
        swaps for 1.5s.
      </p>
    </div>
  );
}
