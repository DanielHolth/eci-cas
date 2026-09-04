"use client";

import { useCallback, useEffect, useRef, useState } from "react";

/**
 * A drawer pinned to one edge of the screen, its width dragged from the
 * inner edge. Width lives here, not in the parent — resizing is this
 * panel's own concern, and its sibling (a `flex-1` main column) reflows for
 * free because this panel sizes itself with an explicit `style.width`
 * rather than a Tailwind class.
 *
 * The panel unmounts when it is closed, so the dragged width is kept in
 * localStorage rather than in state: a drawer that forgot how wide you made
 * it every time you collapsed it is barely resizable at all.
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
  const storageKey = `eci.aside.${side}.width`;

  const [width, setWidth] = useState(() => {
    if (typeof window === "undefined") return defaultWidth;
    const stored = Number(window.localStorage.getItem(storageKey));
    return Number.isFinite(stored) && stored > 0
      ? Math.min(maxWidth, Math.max(minWidth, stored))
      : defaultWidth;
  });

  // Written on every settled width rather than on every pointermove: the
  // drag itself is 60 writes a second, and only the last one matters.
  const dragging = useRef(false);
  const latestWidth = useRef(width);

  useEffect(() => {
    latestWidth.current = width;
    if (dragging.current) return;
    window.localStorage.setItem(storageKey, String(width));
  }, [storageKey, width]);

  // One stable pair of listeners for the life of the component. They live in
  // refs so `stopDrag` can remove the very handler that installed it without
  // either callback having to name the other before it exists.
  const onPointerMove = useRef<(e: PointerEvent) => void>(null);
  const onPointerUp = useRef<() => void>(null);

  const startDrag = useCallback(
    (e: React.PointerEvent) => {
      // Without this the browser starts a text selection at the handle and
      // paints the whole three-column layout blue instead of resizing.
      e.preventDefault();
      dragging.current = true;
      document.body.style.userSelect = "none";
      document.body.style.cursor = "col-resize";

      onPointerMove.current = (move: PointerEvent) => {
        const raw = side === "right" ? window.innerWidth - move.clientX : move.clientX;
        setWidth(Math.min(maxWidth, Math.max(minWidth, raw)));
      };
      onPointerUp.current = () => {
        dragging.current = false;
        document.body.style.userSelect = "";
        document.body.style.cursor = "";
        if (onPointerMove.current) window.removeEventListener("pointermove", onPointerMove.current);
        if (onPointerUp.current) window.removeEventListener("pointerup", onPointerUp.current);
        // The drag suppressed the persisting effect; settle it now.
        window.localStorage.setItem(storageKey, String(latestWidth.current));
      };

      window.addEventListener("pointermove", onPointerMove.current);
      window.addEventListener("pointerup", onPointerUp.current);
    },
    [side, minWidth, maxWidth, storageKey],
  );

  // A panel closed mid-drag must not leave listeners, or a frozen cursor, behind.
  useEffect(
    () => () => {
      if (onPointerMove.current) window.removeEventListener("pointermove", onPointerMove.current);
      if (onPointerUp.current) window.removeEventListener("pointerup", onPointerUp.current);
      document.body.style.userSelect = "";
      document.body.style.cursor = "";
    },
    [],
  );

  const border = side === "right" ? "border-l" : "border-r";
  // The handle straddles its own inner edge either way: pulled half its width
  // outward on the left panel, half its width inward on the right one.
  const handlePos = side === "right" ? "left-0 -translate-x-1/2" : "right-0 translate-x-1/2";
  const overlay = side === "right" ? "right-0" : "left-0";

  return (
    <aside
      style={{ width }}
      className={`fixed inset-y-0 ${overlay} z-20 flex h-full shrink-0 flex-col ${border} border-neutral-200 bg-white shadow-xl lg:static lg:z-auto lg:shadow-none dark:border-neutral-800 dark:bg-neutral-950`}
    >
      <div
        onPointerDown={startDrag}
        className={`absolute top-0 ${handlePos} z-10 h-full w-1.5 cursor-col-resize hover:bg-neutral-300 dark:hover:bg-neutral-700`}
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
