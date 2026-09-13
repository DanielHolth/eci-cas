"use client";

import { useEffect, useRef, useState } from "react";

/**
 * What is left of the face when the local model is answering.
 *
 * An outer ring and a pupil that still tracks the pointer. No iris, no
 * blades, no telemetry ring, no cog teeth, no mood colour — because none of
 * the faculties those stand for are running. Impulse's expression is the
 * only thing colour ever meant on this component, so keeping the colour
 * while the expression is gone would be the avatar lying.
 *
 * Deliberately not a mode inside the shader. The aperture face is a
 * fragment shader with every uniform slot spoken for, and threading a
 * "soul" term through both the WGSL and GLSL bodies would buy a drained
 * version of a thing that should not be drawn at all. Not mounting it is
 * simpler, is identical on a machine with no WebGPU, and gives the GPU back
 * to the model that is now doing the thinking.
 *
 * The pupil keeps moving on purpose. A still circle reads as a crash; a
 * tracking one reads as someone at home with the lights off, which is
 * exactly the state being reported.
 */
export function ShellFace({ label }: { label: string }) {
  const box = useRef<SVGSVGElement>(null);
  const [gaze, setGaze] = useState({ x: 0, y: 0 });

  useEffect(() => {
    // Reduced motion keeps the pupil centred: the shell is fully legible
    // from the static pose, which is the point of drawing it as geometry.
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

    const move = (event: PointerEvent) => {
      const rect = box.current?.getBoundingClientRect();
      if (!rect) return;

      // Offset from the centre in half-widths, then squashed by tanh so the
      // pupil leans toward the pointer and never leaves the socket.
      const dx = (event.clientX - (rect.left + rect.width / 2)) / (rect.width / 2);
      const dy = (event.clientY - (rect.top + rect.height / 2)) / (rect.height / 2);
      setGaze({ x: Math.tanh(dx) * 26, y: Math.tanh(dy) * 26 });
    };

    window.addEventListener("pointermove", move);
    return () => window.removeEventListener("pointermove", move);
  }, []);

  return (
    <svg
      ref={box}
      viewBox="0 0 120 120"
      className="h-56 w-56 rounded-full bg-[#060810]"
      role="img"
      aria-label={label}
    >
      {/* The housing, and nothing inside it. */}
      <circle cx="60" cy="60" r="46" fill="none" stroke="#1e293b" strokeWidth="2" />
      <circle cx="60" cy="60" r="41" fill="none" stroke="#0f172a" strokeWidth="6" />

      <circle
        cx={60 + gaze.x}
        cy={60 + gaze.y}
        r="9"
        fill="#e2e8f0"
        className="eci-shell-pupil"
        style={{ transition: "cx 380ms ease-out, cy 380ms ease-out" }}
      />
    </svg>
  );
}
