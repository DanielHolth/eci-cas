"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Avatar } from "@/components/Avatar";
import { useTurnLog } from "@/lib/useTurnLog";
import { turnsFromRecords } from "@/lib/turns";
import { useSpeech } from "@/lib/useSpeech";
import { useVitals } from "@/lib/useVitals";
import { onShellState, postToShell } from "@/lib/shell";

/** How long the last thing said stays on screen after the mouth stops. Long
 * enough to finish reading, short enough that the watermark goes back to
 * being a watermark. */
const LINGER_MS = 6000;

/** Pointer travel, in CSS pixels, that turns a click into a drag. Small enough
 * that dragging feels immediate, large enough that a shaky click still opens
 * the window. */
const DRAG_SLOP = 4;

/**
 * Morrow as a desktop watermark: a face in a corner, the last thing she said,
 * and whether she is listening. Nothing else.
 *
 * This is not a smaller copy of the conversation window -- there is no
 * transcript, no drawer, no input, and no persona card. The shell opens the
 * real window on a click (see lib/shell.ts) precisely so this surface does
 * not have to grow into one. What it does own, alone, is the voice: one
 * process, one substrate, one feed, and exactly one mouth, which is why the
 * window the shell opens carries ?mute=1.
 *
 * Reuses the same hooks as the window because it is looking at the same
 * session over the same replaying feed: opened at turn forty it knows about
 * turns one through thirty-nine, and is quiet about all of them.
 */
export default function Overlay() {
  const { records, replayed } = useTurnLog();
  const turns = useMemo(() => turnsFromRecords(records), [records]);
  const { vitals, levelledUp } = useVitals(records.length);

  // Unmuted, unlike the window: this surface is the one with the mouth.
  const { speaking, unlock } = useSpeech(turns, { ready: replayed });

  const [state, setState] = useState({ listening: false, interactable: false });

  // Where a gesture started, while it is still undecided between a click and
  // a drag. Undefined means no button is down.
  const [drag, setDrag] = useState<{ x: number; y: number }>();

  useEffect(() => onShellState((next) => setState((current) => ({ ...current, ...next }))), []);

  const turn = turns[turns.length - 1];
  const said = turn?.output?.text;

  // Fresh enough to be worth the pixels. Driven by the text rather than by
  // the turn: a turn is edited into existence over several records, and the
  // countdown should start when there is finally something to read.
  const [fresh, setFresh] = useState(false);
  const first = useRef(true);
  useEffect(() => {
    if (!said) return;

    // The replay's last reply is history, not news. Without this the
    // watermark would come up shouting whatever it was saying before the
    // shell was last closed.
    if (first.current) {
      first.current = false;
      return;
    }

    setFresh(true);
    const timer = setTimeout(() => setFresh(false), LINGER_MS);
    return () => clearTimeout(timer);
  }, [said]);

  return (
    <>
      {/* In the markup rather than in an effect, so it is in the prerendered
          HTML and the window does not flash opaque before React runs. The
          shell's WebView2 is transparent underneath this; a browser tab shows
          whatever is behind it, which is nothing. */}
      <style>{`html, body { background: transparent !important; }`}</style>

      <div className="flex h-screen w-screen select-none flex-col items-center justify-end gap-2 overflow-hidden p-2">
        {(fresh || speaking) && said && (
          <p className="max-h-40 max-w-xs overflow-hidden rounded-2xl bg-neutral-900/80 px-3 py-2 text-center text-sm leading-snug text-neutral-50 shadow-lg backdrop-blur-sm">
            {said}
          </p>
        )}

        {/* The whole face is the handle: click to open the conversation, drag
            to move her. Pointer events only arrive when the shell has made the
            window interactable -- otherwise they pass through to whatever is
            behind her -- so there is no state to check here and no disabled
            styling to get wrong.

            The two gestures are told apart by distance rather than by button,
            because both are things a person expects the left button to do.
            Once the drag is handed over, the OS move loop owns the rest of the
            gesture and no click event follows it, which is exactly the
            either/or that is wanted.

            Interactable is drawn loudly. It is a mode with no other feedback,
            toggled by a key pressed somewhere else entirely, and a watermark
            that starts eating clicks without saying so is indistinguishable
            from a broken one -- so it gets a ring, a glow and a line of text
            rather than the two tenths of opacity it used to get. */}
        <button
          type="button"
          onPointerDown={(e) => setDrag({ x: e.clientX, y: e.clientY })}
          onPointerMove={(e) => {
            if (!drag) return;
            if (Math.abs(e.clientX - drag.x) < DRAG_SLOP && Math.abs(e.clientY - drag.y) < DRAG_SLOP) return;
            setDrag(undefined);
            postToShell({ type: "drag" });
          }}
          onPointerUp={() => setDrag(undefined)}
          onClick={() => {
            // The same gesture buys the speech permission, which a shell
            // launched with an autoplay override will already have.
            unlock();
            postToShell({ type: "session" });
          }}
          aria-label="Open the conversation"
          className={`rounded-full transition-all ${
            state.interactable
              ? "cursor-grab opacity-100 ring-2 ring-sky-400/80 shadow-[0_0_24px_rgba(56,189,248,0.45)]"
              : "opacity-80"
          }`}
        >
          <Avatar
            expression={turn?.impulse?.expression ?? "neutral"}
            speaking={speaking}
            level={vitals.level.level}
            vigor={0.5 + vitals.energy.fraction}
            levelledUp={levelledUp}
            shell={vitals.tier.isLocal}
          />
        </button>

        {state.listening ? (
          <p className="rounded-full bg-red-600/90 px-3 py-1 text-xs font-semibold text-white shadow" role="status">
            Listening…
          </p>
        ) : state.interactable ? (
          <p className="rounded-full bg-sky-500/90 px-3 py-1 text-xs font-medium text-white shadow" role="status">
            Click to open · drag to move
          </p>
        ) : null}
      </div>
    </>
  );
}
