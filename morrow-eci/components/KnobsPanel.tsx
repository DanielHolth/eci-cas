"use client";

import { useEffect, useState } from "react";
import { Knobs, fetchKnobs, setMaxSentences, setRecallDepth, setReflectionEvery, setTone } from "@/lib/api";

/**
 * Live session experiments, not configuration: every value here resets to
 * its default on a host restart (see EciCas.Core.RuntimeKnobs). A drag
 * takes effect on the very next turn — no restart, no redeploy.
 */
export function KnobsPanel() {
  const [knobs, setKnobs] = useState<Knobs | null>(null);

  useEffect(() => {
    fetchKnobs()
      .then(setKnobs)
      .catch(() =>
        setKnobs({ maxSentences: 2, reflectionEvery: 5, recallDepth: 5, tone: "Neutral", tones: ["Maleficent", "Sarcastic", "Neutral", "Helpful", "Ecstatic"] }),
      );
  }, []);

  async function apply<K extends keyof Knobs>(key: K, value: Knobs[K], write: (v: never) => Promise<Knobs>) {
    setKnobs((prev) => (prev ? { ...prev, [key]: value } : prev));
    try {
      await write(value as never);
    } catch {
      // Host unreachable — the slider still reflects the attempted value;
      // the next successful fetch will correct it if the write never landed.
    }
  }

  const toneIndex = knobs ? Math.max(0, knobs.tones.indexOf(knobs.tone)) : 2;
  const toneMax = (knobs?.tones.length ?? 5) - 1;

  return (
    <div className="border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">
        Knobs
      </h3>

      <label className="flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span>Reply length</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">
            {knobs === null ? "…" : `${knobs.maxSentences} sentence${knobs.maxSentences === 1 ? "" : "s"}`}
          </span>
        </span>
        <input
          type="range"
          min={1}
          max={20}
          step={1}
          value={knobs?.maxSentences ?? 2}
          disabled={knobs === null}
          onChange={(e) => apply("maxSentences", Number(e.target.value), setMaxSentences)}
          className="accent-neutral-700 dark:accent-neutral-300"
        />
      </label>

      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span>Tone</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">{knobs?.tone ?? "…"}</span>
        </span>
        <input
          type="range"
          min={0}
          max={toneMax}
          step={1}
          value={toneIndex}
          disabled={knobs === null}
          onChange={(e) => apply("tone", knobs!.tones[Number(e.target.value)], setTone)}
          className="accent-neutral-700 dark:accent-neutral-300"
        />
      </label>

      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span>Reflection every</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">
            {knobs === null ? "…" : `${knobs.reflectionEvery} turn${knobs.reflectionEvery === 1 ? "" : "s"}`}
          </span>
        </span>
        <input
          type="range"
          min={1}
          max={20}
          step={1}
          value={knobs?.reflectionEvery ?? 5}
          disabled={knobs === null}
          onChange={(e) => apply("reflectionEvery", Number(e.target.value), setReflectionEvery)}
          className="accent-neutral-700 dark:accent-neutral-300"
        />
      </label>

      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span>Recall depth</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">
            {knobs === null ? "…" : `${knobs.recallDepth} rows`}
          </span>
        </span>
        <input
          type="range"
          min={1}
          max={20}
          step={1}
          value={knobs?.recallDepth ?? 5}
          disabled={knobs === null}
          onChange={(e) => apply("recallDepth", Number(e.target.value), setRecallDepth)}
          className="accent-neutral-700 dark:accent-neutral-300"
        />
      </label>
    </div>
  );
}
