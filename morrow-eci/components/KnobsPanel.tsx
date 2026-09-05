"use client";

import { useEffect, useState } from "react";
import { Knobs, fetchKnobs, setMaxSentences, setRecallDepth, setReflectionEvery, setMood, setTier } from "@/lib/api";

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
        setKnobs({ tier: "Mock", tiers: [{ name: "Mock", missingKeys: [] }], maxSentences: 2, reflectionEvery: 5, recallDepth: 5, mood: "Neutral", moods: ["Maleficent", "Sarcastic", "Neutral", "Helpful", "Ecstatic"] }),
      );
  }, []);

  async function apply<K extends keyof Knobs>(key: K, value: Knobs[K], write: (v: never) => Promise<Knobs>) {
    setKnobs((prev) => (prev ? { ...prev, [key]: value } : prev));
    try {
      // Adopt the host's answer rather than only the optimistic value: a
      // tier switch re-seeds recallDepth, so the write that moved one
      // control is what tells us the others moved too.
      setKnobs(await write(value as never));
    } catch {
      // Host unreachable — the slider still reflects the attempted value;
      // the next successful fetch will correct it if the write never landed.
    }
  }

  // Mood, not tone: the slider sets how the persona feels this turn, where
  // Identity's profile says who it standingly is. Both used to say "tone".
  const moodIndex = knobs ? Math.max(0, knobs.moods.indexOf(knobs.mood)) : 2;
  const moodMax = (knobs?.moods.length ?? 5) - 1;

  return (
    <div className="border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">
        Knobs
      </h3>

      {/* A tier is a preset over everything below it -- which models back
          which class, how wide Recall fans out, whether Reflection runs at
          all -- so it sits above them rather than among them. A tier whose
          keys the host cannot see is listed and disabled: knowing Default
          exists and why it is unavailable beats it being absent. */}
      <label className="flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span>Tier</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">{knobs?.tier ?? "…"}</span>
        </span>
        <select
          value={knobs?.tier ?? ""}
          disabled={knobs === null}
          onChange={(e) => apply("tier", e.target.value, setTier)}
          // The popup list is drawn by the browser, not by this panel, and a
          // transparent select left it lit by the platform default beside a
          // near-black aside. color-scheme is what actually moves the popup's
          // own chrome -- its scrollbar and its selection highlight -- while
          // the background on select and option covers the rows themselves.
          className="rounded border border-neutral-300 bg-white px-1 py-0.5 font-mono text-xs text-neutral-900 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:[color-scheme:dark]"
        >
          {(knobs?.tiers ?? []).map((t) => (
            <option
              key={t.name}
              value={t.name}
              disabled={t.missingKeys.length > 0}
              className="bg-white text-neutral-900 dark:bg-neutral-900 dark:text-neutral-100"
            >
              {t.name}
              {t.missingKeys.length > 0 ? ` — needs ${t.missingKeys.join(", ")}` : ""}
            </option>
          ))}
        </select>
      </label>

      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
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
          <span>Mood</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">{knobs?.mood ?? "…"}</span>
        </span>
        <input
          type="range"
          min={0}
          max={moodMax}
          step={1}
          value={moodIndex}
          disabled={knobs === null}
          onChange={(e) => apply("mood", knobs!.moods[Number(e.target.value)], setMood)}
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
