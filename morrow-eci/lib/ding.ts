"use client";

/**
 * The level-up ding, synthesised rather than shipped as a file.
 *
 * Two sine partials an octave and a fifth apart with a fast attack and a
 * half-second exponential tail — a struck bell, quiet enough to be a
 * confirmation rather than an achievement fanfare. Synthesising it means no
 * asset to load, no format to pick, and a volume that is tuned in one line.
 *
 * Best-effort by design: a browser that has not had a gesture yet refuses to
 * start an AudioContext, and a level-up that arrives silently is a far
 * smaller problem than one that throws.
 */
export function ding(volume = 0.06): void {
  try {
    const Ctx =
      window.AudioContext ??
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;

    const ctx = new Ctx();
    const now = ctx.currentTime;

    for (const [hz, gain, seconds] of [
      [1318.5, 1, 0.55],
      [1975.5, 0.45, 0.4],
    ] as const) {
      const osc = ctx.createOscillator();
      const amp = ctx.createGain();
      osc.type = "sine";
      osc.frequency.value = hz;
      amp.gain.setValueAtTime(0.0001, now);
      amp.gain.exponentialRampToValueAtTime(volume * gain, now + 0.012);
      amp.gain.exponentialRampToValueAtTime(0.0001, now + seconds);
      osc.connect(amp).connect(ctx.destination);
      osc.start(now);
      osc.stop(now + seconds + 0.05);
    }

    // The context is a scarce resource in some browsers, so it is closed
    // once the tail has rung out rather than left open for the session.
    setTimeout(() => void ctx.close().catch(() => {}), 900);
  } catch {
    // No audio. The floating +1 still says what happened.
  }
}
