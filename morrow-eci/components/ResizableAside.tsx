"use client";

import { useCallback, useRef, useState } from "react";

/**
 * A drawer pinned to one edge of the screen, its width dragged from the
 * inner edge. Width lives here, not in the parent — resizing is this
 * panel's own concern, and its sibling (a `flex-1` main column) reflows for
 * free because this panel sizes itself with an explicit `style.width`
 * rather than a Tailwind class.
 */
export function ResizableAside({
  side,
  title,
  onClose,
  children,
  defaultWidth = 320,
  minWidth = 240,
  maxWidth = 720,
}: {
  side: "left" | "right";
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  defaultWidth?: number;
  minWidth?: number;
  maxWidth?: number;
}) {
  const [width, setWidth] = useState(defaultWidth);
  const dragging = useRef(false);

  const onPointerMove = useCallback(
    (e: PointerEvent) => {
      if (!dragging.current) return;
      const raw = side === "right" ? window.innerWidth - e.clientX : e.clientX;
      setWidth(Math.min(maxWidth, Math.max(minWidth, raw)));
    },
    [side, minWidth, maxWidth],
  );

  const stopDrag = useCallback(() => {
    dragging.current = false;
    window.removeEventListener("pointermove", onPointerMove);
    window.removeEventListener("pointerup", stopDrag);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [onPointerMove]);

  function startDrag() {
    dragging.current = true;
    window.addEventListener("pointermove", onPointerMove);
    window.addEventListener("pointerup", stopDrag);
  }

  const border = side === "right" ? "border-l" : "border-r";
  const handlePos = side === "right" ? "left-0" : "right-0";
  const overlay = side === "right" ? "right-0" : "left-0";

  return (
    <aside
      style={{ width }}
      className={`fixed inset-y-0 ${overlay} z-20 flex h-full shrink-0 flex-col ${border} border-neutral-200 bg-white shadow-xl lg:static lg:z-auto lg:shadow-none dark:border-neutral-800 dark:bg-neutral-950`}
    >
      <div
        onPointerDown={startDrag}
        className={`absolute top-0 ${handlePos} z-10 h-full w-1.5 -translate-x-1/2 cursor-col-resize hover:bg-neutral-300 dark:hover:bg-neutral-700`}
      />
      <div className="flex items-center justify-between border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
        <h2 className="text-sm font-semibold text-neutral-800 dark:text-neutral-100">{title}</h2>
        <button
          type="button"
          onClick={onClose}
          aria-label={`Close ${title}`}
          className="rounded px-2 text-neutral-400 hover:text-neutral-800 dark:hover:text-neutral-100"
        >
          ×
        </button>
      </div>
      <div className="flex-1 overflow-y-auto">{children}</div>
    </aside>
  );
}
