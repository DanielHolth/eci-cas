"use client";

import { useEffect, useState } from "react";
import { ResizableAside } from "@/components/ResizableAside";

export function PowerShellPanel({ onClose }: { onClose: () => void }) {
  const [lines, setLines] = useState<string[]>([]);

  useEffect(() => {
    const output = [
      "PS> hello world",
      "hello world",
    ];
    setLines(output);
  }, []);

  return (
    <ResizableAside side="left" title="PowerShell" onClose={onClose}>
      <div className="flex h-full flex-col bg-neutral-950 text-xs text-neutral-100">
        <div className="flex items-center justify-between border-b border-neutral-800 px-3 py-2 text-[11px] uppercase tracking-wide text-neutral-400">
          <span>Windows PowerShell</span>
          <span className="font-mono text-emerald-400">Ready</span>
        </div>
        <pre className="flex-1 overflow-auto whitespace-pre-wrap break-words px-3 py-3 font-mono leading-6 text-neutral-100">
          {lines.join("\n")}
        </pre>
      </div>
    </ResizableAside>
  );
}
