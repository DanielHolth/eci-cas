"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Avatar } from "@/components/Avatar";
import { useTurnLog } from "@/lib/useTurnLog";
import { turnsFromRecords } from "@/lib/turns";
import { useSpeech } from "@/lib/useSpeech";
import { useVitals } from "@/lib/useVitals";
import { useMood } from "@/lib/useMood";
import { onShellState, postToShell } from "@/lib/shell";

/** How long the last thing said stays on screen after the mouth stops. Long
 * enough to finish reading, short enough that the watermark goes back to
 * being a watermark. */
const LINGER_MS = 6000;

/** Pointer travel, in CSS pixels, that turns a click into a drag. Small enough
 * that dragging feels immediate, large enough that a shaky click still opens
 * the window. */
const DRAG_SLOP = 4;

/** The p-2 on the outer box, both edges, in CSS pixels. Part of the height the
 * window needs and not part of what a ResizeObserver measures. */
const PADDING_PX = 16;

/** How long a transcript stays up. Shorter than a reply lingers: it is a
 * receipt, and the reply arriving behind it is the real answer. */
const HEARD_MS = 4000;

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

  // The same dial the conversation window reads. Without it this face sat on
  // Impulse's expression while the other one wore the pinned mood, and the
  // two surfaces looked like two personas.
  const face = useMood();

  // Unmuted, unlike the window: this surface is the one with the mouth.
  const { speaking, unlock } = useSpeech(turns, { ready: replayed });

  const [state, setState] = useState({ listening: false, interactable: false, heard: "", heardAt: 0 });

  // Where a gesture started, while it is still undecided between a click and
  // a drag. Undefined means no button is down.
  //
  // A ref rather than state: nothing on screen depends on it, and a gesture
  // that has to wait for a render before the next pointermove can read it is
  // a gesture that misses the move it was waiting for.
  const drag = useRef<{ x: number; y: number }>(undefined);

  useEffect(() => onShellState((next) => setState((current) => ({ ...current, ...next }))), []);

  // What the shell heard, until it goes stale. Keyed on the counter rather than
  // the text so saying the same thing twice shows twice, and cleared on a new
  // take so an old transcript never sits under a new answer.
  const [heard, setHeard] = useState("");
  useEffect(() => {
    if (!state.heardAt) return;
    setHeard(state.heard);
    const timer = setTimeout(() => setHeard(""), HEARD_MS);
    return () => clearTimeout(timer);
  }, [state.heardAt, state.heard]);

  const turn = turns[turns.length - 1];
  const said = turn?.output?.text;

  // Fresh enough to be worth the pixels. Driven by the text rather than by
  // the turn: a turn is edited into existence over several records, and the
  // countdown should start when there is finally something to read.
  const [fresh, setFresh] = useState(false);
  const first = useRef(true);

  // The idea bubble: shown the instant Reflection's own idea lands on
  // perception, long before it has been thought through into a reply, so the
  // person sees her have the thought while she is still finishing the last
  // one aloud. Keyed to a turnId rather than just "on/off" so it can be
  // cleared at the right moment -- see below -- instead of on a timer of its
  // own, since there is no fixed number of seconds a reply takes to land.
  const [ideaTurnId, setIdeaTurnId] = useState<string | null>(null);
  const [ideaText, setIdeaText] = useState("");
  const seenIdeas = useRef<Set<string>>(new Set());
  const primedIdeas = useRef(false);

  useEffect(() => {
    if (!replayed) return;

    // The replay's backlog of ideas is history, same reasoning as `first`
    // above for replies -- a window opened mid-session should not announce
    // every idea Reflection has ever had.
    if (!primedIdeas.current) {
      primedIdeas.current = true;
      for (const t of turns) {
        if (t.selfTriggered) seenIdeas.current.add(t.turnId);
      }
      return;
    }

    for (const t of turns) {
      if (t.selfTriggered && t.ideaText && !seenIdeas.current.has(t.turnId)) {
        seenIdeas.current.add(t.turnId);
        setIdeaTurnId(t.turnId);
        setIdeaText(t.ideaText);
      }
    }
  }, [turns, replayed]);

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
    const timer = setTimeout(() => {
      setFresh(false);
      // The idea bubble outlives its own settle wait and the vocal that
      // follows it, and only clears here, in step with the reply it was
      // about fading -- not the moment speech for it starts, and not on a
      // timer counted from when the idea itself arrived. All three bubbles
      // (heard, idea, reply) go together.
      setIdeaTurnId((id) => (id === turn?.turnId ? null : id));
    }, LINGER_MS);
    return () => clearTimeout(timer);
  }, [said, turn?.turnId]);

  // How tall and wide she needs to be, reported to the shell so the window can
  // grow to fit. The window is a fixed rectangle otherwise: a long reply was
  // being cut off by its bottom edge, and a long "heard" pill -- centered in a
  // window sized for the face alone -- was getting clipped equally on both its
  // left and right by the window's own edge, since a centered flex child with
  // no explicit width sizes to its content rather than the container.
  //
  // Measured off an inner box rather than the page: the outer one is h-screen
  // and would only ever report the size it already has. A ResizeObserver
  // rather than an effect on the text, because wrapping happens after layout
  // and the size is not knowable from the string.
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = box.current;
    if (!element) return;

    // The last size sent, so a reflow that changes nothing stays silent.
    let sentHeight = 0;
    let sentWidth = 0;
    const observer = new ResizeObserver(() => {
      const rect = element.getBoundingClientRect();
      const height = Math.ceil(rect.height) + PADDING_PX;
      const width = Math.ceil(rect.width) + PADDING_PX;
      if (Math.abs(height - sentHeight) < 2 && Math.abs(width - sentWidth) < 2) return;
      sentHeight = height;
      sentWidth = width;
      postToShell({ type: "resize", height, width });
    });

    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return (
    <>
      {/* In the markup rather than in an effect, so it is in the prerendered
          HTML and the window does not flash opaque before React runs. The
          shell's WebView2 is transparent underneath this; a browser tab shows
          whatever is behind it, which is nothing. */}
      <style>{`
        html, body { background: transparent !important; }

        /* A cloud, not a rounded rectangle: a body plus a handful of
           overlapping circles bumped out along the top and bottom edges, the
           classic thought-bubble silhouette. Built from box-shadow rather
           than a bitmap or an SVG path so it still respects the text inside
           sizing the box, and one color (the fill) drives the whole shape
           since every bump is a shadow of the same element rather than a
           separately colored layer. */
        .cloud-bubble {
          position: relative;
          background: rgba(49, 46, 129, 0.85);
          border: 1px solid rgba(165, 180, 252, 0.35);
          border-radius: 42% 40% 46% 44% / 60% 55% 58% 52%;
        }
        .cloud-bubble::before,
        .cloud-bubble::after {
          content: "";
          position: absolute;
          background: rgba(49, 46, 129, 0.85);
          border: 1px solid rgba(165, 180, 252, 0.35);
          border-radius: 50%;
        }
        .cloud-bubble::before {
          width: 42%;
          height: 46%;
          top: -18%;
          left: 14%;
        }
        .cloud-bubble::after {
          width: 30%;
          height: 34%;
          bottom: -14%;
          right: 18%;
        }
      `}</style>

      <div className="flex h-screen w-screen select-none items-end justify-end overflow-hidden p-2">
        {/* A cross around the avatar, not a stack: what the person said goes
            above (with an arrow pointing down at her, the direction it was
            heard from), what she says back goes below (a speech bubble with
            a tail, the direction a voice comes from), and a thought she is
            having on her own goes to the left (a cloud, disconnected from
            both directions of the exchange). Grid rather than three absolute
            positions: the avatar's own cell size still drives row/column
            sizing, so `box` below keeps measuring one element that encloses
            whatever combination of the three is currently showing. */}
        <div
          ref={box}
          className="grid items-center justify-items-center gap-2"
          style={{ gridTemplateAreas: `"idea prompt prompt" "idea avatar avatar" ". reply reply"`, gridTemplateColumns: "auto auto auto" }}
        >
          {heard && (
            /* What she heard, above her, with an arrow pointing down at the
               avatar: that is the direction a prompt arrives from. Reuses the
               "heard" transcript rather than turn.input -- turn.input only
               exists once the record round-trips, which is well after the
               shell already has the raw dictation. */
            <div style={{ gridArea: "prompt" }} className="flex flex-col items-center">
              <p className="max-w-xs rounded-2xl bg-neutral-100/90 px-3 py-2 text-center text-sm leading-snug text-neutral-900 shadow-lg backdrop-blur-sm">
                {heard}
              </p>
              <svg width="16" height="10" viewBox="0 0 16 10" className="text-neutral-100/90">
                <path d="M0 0 L8 10 L16 0 Z" fill="currentColor" />
              </svg>
            </div>
          )}

          {ideaTurnId && (
            /* A thought of her own, to the side rather than in line with the
               exchange, and drawn as a cloud with a trail of shrinking dots
               back to the avatar -- the classic thought-bubble grammar --
               rather than the same rounded rectangle as the other two, so it
               reads as a different kind of bubble on sight, not just a
               different position. */
            <div style={{ gridArea: "idea" }} className="flex items-center gap-1.5">
              <p
                role="status"
                className="cloud-bubble max-w-[13rem] px-4 py-3 text-center text-sm italic leading-snug text-indigo-100 shadow-lg"
              >
                {ideaText}
              </p>
              <span className="flex flex-col items-center gap-1">
                <span className="h-2.5 w-2.5 rounded-full bg-indigo-200/70" />
                <span className="h-1.5 w-1.5 rounded-full bg-indigo-200/60" />
                <span className="h-1 w-1 rounded-full bg-indigo-200/50" />
              </span>
            </div>
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
            style={{ gridArea: "avatar" }}
            onPointerDown={(e) => {
              drag.current = { x: e.clientX, y: e.clientY };
              // Without this the pointer leaves the face on the first quick
              // movement and the moves that would have become a drag are
              // delivered somewhere else.
              e.currentTarget.setPointerCapture(e.pointerId);
            }}
            onPointerMove={(e) => {
              const from = drag.current;
              if (!from) return;
              if (Math.abs(e.clientX - from.x) < DRAG_SLOP && Math.abs(e.clientY - from.y) < DRAG_SLOP) return;

              // Handed over: let go of the pointer before the shell asks the
              // OS for it, and do not ask twice for one gesture.
              drag.current = undefined;
              e.currentTarget.releasePointerCapture(e.pointerId);
              postToShell({ type: "drag" });
            }}
            onPointerUp={() => (drag.current = undefined)}
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
              expression={face || (turn?.impulse?.expression ?? "neutral")}
              speaking={speaking}
              level={vitals.level.level}
              vigor={0.5 + vitals.energy.fraction}
              levelledUp={levelledUp}
              shell={vitals.tier.isLocal}
            />
          </button>

          {(fresh || speaking) && said && (
            /* What she says back, below her, styled as a speech bubble with a
               tail pointing up into the avatar -- the direction a voice comes
               from. No max height and nothing hidden: the window is what
               grows now, and a bubble that clipped itself first would make
               that pointless. */
            <div style={{ gridArea: "reply" }} className="flex flex-col items-center">
              <svg width="16" height="10" viewBox="0 0 16 10" className="text-neutral-900/80">
                <path d="M0 10 L8 0 L16 10 Z" fill="currentColor" />
              </svg>
              <p className="max-w-xs rounded-2xl bg-neutral-900/80 px-3 py-2 text-center text-sm leading-snug text-neutral-50 shadow-lg backdrop-blur-sm">
                {said}
              </p>
            </div>
          )}

          <div style={{ gridArea: "avatar" }} className="pointer-events-none mt-1 flex translate-y-full justify-center pt-1">
            {state.listening ? (
              <p className="pointer-events-auto rounded-full bg-red-600/90 px-3 py-1 text-xs font-semibold text-white shadow" role="status">
                Listening…
              </p>
            ) : state.interactable ? (
              <p className="pointer-events-auto rounded-full bg-sky-500/90 px-3 py-1 text-xs font-medium text-white shadow" role="status">
                Click to open · drag to move
              </p>
            ) : null}
          </div>
        </div>
      </div>
    </>
  );
}
