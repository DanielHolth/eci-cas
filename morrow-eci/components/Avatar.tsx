"use client";

import { useEffect, useRef, useState } from "react";
import { mountApertureFace, type Backend } from "@/lib/apertureFace";
import type { Expression } from "@/types/events";

/**
 * Impulse's six-word expression vocabulary rendered as a face rather than a
 * coloured disc with the word written on it.
 *
 * Deliberately hand-drawn SVG with CSS keyframes, not a sprite sheet, a
 * Lottie file or an animation library: six expressions is few enough that
 * the whole face is one file of geometry, it themes itself from the same
 * palette the rest of the app uses, and it costs nothing to load. Every
 * feature that differs between expressions is a single number or path in
 * FACE below — adding a seventh mood is a row, not a redraw.
 *
 * Motion has two layers, and they are separate on purpose. The *idle* layer
 * (breathing, blinking, pupil drift) never stops, so the persona is alive
 * even when nothing is happening. The *expression* layer is the animation
 * that belongs to this particular mood — a tremble for scared, a slow droop
 * for sad — and it swaps when Impulse's advisory does. Colour, brow angle,
 * eye openness and mouth all transition rather than cut, so a change of mood
 * reads as the face moving into it.
 *
 * All of it is suppressed under prefers-reduced-motion: the expression is
 * still fully legible from the static pose, which is the point of encoding
 * it in geometry instead of in movement.
 */
const FACE: Record<
  Expression,
  {
    label: string;
    /** Head gradient, light and dark stop. */
    from: string;
    to: string;
    /** Degrees applied to the left brow; the right brow mirrors it. Positive tilts the inner ends down (angry), negative lifts them (sad, worried). */
    brow: number;
    /** Vertical brow offset — raised for alert, lowered for a glower. */
    browY: number;
    /** Eye openness as a vertical scale: wide for alert, hooded for sad. */
    open: number;
    mouth: string;
    /** Which expression-layer keyframe animation this mood wears. */
    motion: string;
  }
> = {
  angry:   { label: "Angry",   from: "#f87171", to: "#b91c1c", brow:  14, browY:  2, open: 0.78, mouth: "M46 86 Q60 79 74 86", motion: "eci-fume" },
  scared:  { label: "Scared",  from: "#c084fc", to: "#6d28d9", brow: -14, browY: -4, open: 1.18, mouth: "M52 79 Q60 73 68 79 Q68 92 60 92 Q52 92 52 79", motion: "eci-tremble" },
  sad:     { label: "Sad",     from: "#93c5fd", to: "#1d4ed8", brow: -10, browY:  1, open: 0.68, mouth: "M46 87 Q60 76 74 87", motion: "eci-droop" },
  warm:    { label: "Warm",    from: "#fcd34d", to: "#d97706", brow:  -4, browY: -1, open: 0.86, mouth: "M44 78 Q60 94 76 78", motion: "eci-glow" },
  alert:   { label: "Alert",   from: "#fdba74", to: "#ea580c", brow:  -2, browY: -6, open: 1.2,  mouth: "M52 81 Q60 73 68 81 Q60 90 52 81", motion: "eci-perk" },
  neutral: { label: "Neutral", from: "#cbd5e1", to: "#64748b", brow:   0, browY:  0, open: 1,    mouth: "M47 83 Q60 87 73 83", motion: "eci-idle" },
};

/**
 * The drawn face, kept as the fallback for a browser with neither WebGPU nor
 * WebGL2. The prototype had no fallback drawing to show and said so on
 * screen; the app already had this one written, and a face is a better
 * answer than an apology.
 */
function DrawnFace({ expression, speaking }: { expression: Expression; speaking: boolean }) {
  const face = FACE[expression];
  return (
    <svg
        viewBox="0 0 120 120"
        className="h-56 w-56 overflow-visible"
        role="img"
        aria-label={`Avatar expression: ${face.label}`}
        style={{ ["--eci-motion" as string]: face.motion }}
      >
        <defs>
          <radialGradient id="eci-skin" cx="38%" cy="30%" r="80%">
            <stop offset="0%" stopColor={face.from} />
            <stop offset="100%" stopColor={face.to} />
          </radialGradient>
        </defs>

        {/* Aura: the mood spilling past the edge of the face, so a change
            of expression is visible from across the room. */}
        <circle className="eci-aura" cx="60" cy="60" r="50" fill={face.from} />

        <g className="eci-face">
          <circle cx="60" cy="60" r="46" fill="url(#eci-skin)" />

          <g className="eci-eyes" fill="#1e293b">
            {[44, 76].map((cx) => (
              <g key={cx} className="eci-eye" style={{ transformOrigin: `${cx}px 54px` }}>
                <ellipse cx={cx} cy="54" rx="7" ry={7 * face.open} fill="#fff" />
                <circle className="eci-pupil" cx={cx} cy="54" r="3.4" />
              </g>
            ))}
          </g>

          <g stroke="#1e293b" strokeWidth="3.5" strokeLinecap="round" fill="none">
            {/* Mirrored: the inner end of each brow is the one nearest the
                nose, so one angle drives both and never disagrees. */}
            <path className="eci-brow" d="M35 41 L53 41" style={{ transform: `translateY(${face.browY}px) rotate(${face.brow}deg)`, transformOrigin: "44px 41px" }} />
            <path className="eci-brow" d="M67 41 L85 41" style={{ transform: `translateY(${face.browY}px) rotate(${-face.brow}deg)`, transformOrigin: "76px 41px" }} />
          </g>

          <path
            className={`eci-mouth${speaking ? " eci-speaking" : ""}`}
            d={face.mouth}
            stroke="#1e293b"
            strokeWidth="3.5"
            strokeLinecap="round"
            fill="none"
          />
        </g>
      </svg>
  );
}

/**
 * The face, above the transcript.
 *
 * A canvas, not the SVG that used to be here: the iris, the telemetry ring
 * and the brow bars are one fragment shader (lib/apertureFace.ts), which is
 * what buys the machine read — nine blades that actually rotate, a ring
 * whose cells light against alertness, and a pupil that follows the pointer.
 * None of that is reachable with six hand-drawn paths.
 *
 * The renderer is mounted once and then *driven*, never re-created: React
 * owns the mood, the shader owns the frames. A remount on every expression
 * change would rebuild a GPU pipeline several times a turn and lose the
 * easing between moods, which is most of what makes it read as a face
 * moving rather than a sprite swapping.
 *
 * If neither WebGPU nor WebGL2 is available, the drawn face takes over. It
 * is the same six-word vocabulary and the same colour pairs, so nothing is
 * lost but the motion.
 */
export function Avatar({
  expression,
  speaking = false,
  identity,
}: {
  expression: Expression;
  /** Drives the standing ripples across the iris while a reply is voiced. */
  speaking?: boolean;
  /** The person's chosen emoji, worn as a badge beside the face —
   * whose conversation this is, kept strictly separate from the colour,
   * which is Impulse's alone. */
  identity?: string;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const handle = useRef<ReturnType<typeof mountApertureFace>>(null);
  const [backend, setBackend] = useState<Backend | null>(null);

  // Mount once. The mood props are read through refs on the first frame
  // rather than listed as dependencies, because a dependency on them is
  // exactly the remount this component exists to avoid.
  const start = useRef({ expression, speaking });
  useEffect(() => {
    if (!canvas.current) return;
    const face = mountApertureFace(canvas.current, start.current, setBackend);
    handle.current = face;
    return () => {
      face.destroy();
      handle.current = null;
    };
  }, []);

  useEffect(() => { handle.current?.setMood(expression); }, [expression]);
  useEffect(() => { handle.current?.setSpeaking(speaking); }, [speaking]);

  const drawn = backend === "none";

  return (
    <div className="relative shrink-0">
      {/* Kept mounted even when the drawn face is showing: unmounting the
          canvas would take the context with it, and "none" is decided by
          the canvas itself. Hidden rather than removed. */}
      <canvas
        ref={canvas}
        hidden={drawn}
        role="img"
        aria-label={`Avatar expression: ${FACE[expression].label}`}
        className="h-56 w-56 rounded-full bg-[#060810] shadow-inner"
      />
      {drawn && <DrawnFace expression={expression} speaking={speaking} />}

      {identity && (
        <span
          aria-hidden
          className="absolute -bottom-1 -right-1 flex h-6 w-6 items-center justify-center rounded-full bg-white text-sm shadow ring-1 ring-neutral-200 dark:bg-neutral-900 dark:ring-neutral-700"
        >
          {identity}
        </span>
      )}
    </div>
  );
}
