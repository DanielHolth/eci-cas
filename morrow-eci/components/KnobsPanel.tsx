"use client";

import { useEffect, useState } from "react";
import {
  Knobs,
  fetchKnobs,
  saveKnobs,
  setMaxSentences,
  setPerceptionChars,
  setRecallDepth,
  setRecallThreads,
  setReflectionEvery,
  setMood,
  setTier,
} from "@/lib/api";

/**
 * Live session experiments that can become configuration: a drag takes
 * effect on the very next turn, no restart needed, and resets to the active
 * tier's file on the next restart unless Save writes it there first (see
 * EciCas.Core.RuntimeKnobs / KnobDefaults).
 */
export function KnobsPanel() {
  const [knobs, setKnobs] = useState<Knobs | null>(null);
  const [saving, setSaving] = useState(false);

  // Retried rather than defaulted. A failed fetch used to install an
  // invented payload -- tier "Mock", one tier in the list, the sliders at
  // their compiled-in numbers -- which on a cold boot, where the surface is
  // simply up before the host is, read as a statement about the running host
  // and said Mock while Minimal was answering. The panel now shows nothing
  // until it has been told something, and keeps asking.
  useEffect(() => {
    let live = true;
    let timer: ReturnType<typeof setTimeout>;

    const poll = () => {
      fetchKnobs()
        .then((k) => live && setKnobs(k))
        .catch(() => {
          if (live) {
            timer = setTimeout(poll, 2000);
          }
        });
    };

    poll();
    return () => {
      live = false;
      clearTimeout(timer);
    };
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

  // Every knob is written back now: each tier file carries a Knobs section
  // (see KnobDefaults) alongside Recall's, so a drag on any slider can
  // become the tier's own default, not just the two Recall ones.
  const dirty =
    knobs !== null &&
    (knobs.recallDepth !== knobs.savedRecallDepth ||
      knobs.recallThreads !== knobs.savedRecallThreads ||
      knobs.maxSentences !== knobs.savedMaxSentences ||
      knobs.reflectionEvery !== knobs.savedReflectionEvery ||
      knobs.perceptionChars !== knobs.savedPerceptionChars ||
      knobs.mood !== knobs.savedMood);

  async function save() {
    if (!dirty || saving) return;
    setSaving(true);
    try {
      setKnobs(await saveKnobs());
    } catch {
      // The button stays lit, which is the correct report: nothing was
      // written, and the values still differ from the file.
    } finally {
      setSaving(false);
    }
  }

  // Mood, not tone: the slider sets how the persona feels this turn, where
  // Identity's profile says who it standingly is. Both used to say "tone".
  const moodIndex = knobs ? Math.max(0, knobs.moods.indexOf(knobs.mood)) : 2;
  const moodMax = (knobs?.moods.length ?? 5) - 1;

  return (
    <div className="border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">
          Knobs
        </h3>
        {/* Grey until the live Recall knobs differ from the tier file, so the
            button doubles as the answer to "is what I am running what is
            written down". */}
        <button
          type="button"
          onClick={save}
          disabled={!dirty || saving}
          title={dirty ? `Write to appsettings.${knobs?.tier}.json` : "Nothing changed since the tier file"}
          className="rounded border border-neutral-300 px-2 py-0.5 text-[11px] text-neutral-700 hover:bg-neutral-100 disabled:cursor-default disabled:border-neutral-200 disabled:text-neutral-400 disabled:hover:bg-transparent dark:border-neutral-700 dark:text-neutral-200 dark:hover:bg-neutral-800 dark:disabled:border-neutral-800 dark:disabled:text-neutral-600"
        >
          {saving ? "Saving…" : "Save"}
        </button>
      </div>

      {/* A tier is a preset over everything below it -- which models back
          which class, how wide Recall fans out, whether Reflection runs at
          all -- so it sits above them rather than among them. A tier whose
          keys the host cannot see is listed and disabled: knowing Default
          exists and why it is unavailable beats it being absent. */}
      <label className="flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span title="Which appsettings.<Tier>.json is in force — models, fan-out, everything below. Switching re-seeds these sliders." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Tier</span>
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
          <span title="Sentence ceiling for Intent. It is told half this as a floor too, so the range is what governs, not the cap." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Reply length</span>
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
          <span title="Characters of one person's input that reach the bus. The input field counts against this rather than swallowing the overflow silently." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Input length</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">
            {knobs === null ? "…" : `${knobs.perceptionChars} chars`}
          </span>
        </span>
        <input
          type="range"
          min={64}
          max={2048}
          step={64}
          value={knobs?.perceptionChars ?? 512}
          disabled={knobs === null}
          onChange={(e) => apply("perceptionChars", Number(e.target.value), setPerceptionChars)}
          className="accent-neutral-700 dark:accent-neutral-300"
        />
      </label>

      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span title="How the persona feels this turn. Not tone: tone is the prose, mood is the state behind it." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Mood</span>
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
          <span title="Turns between Reflection passes. Each one is an unprompted thought written to the passage corpus." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Reflection every</span>
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

      {/* Two knobs, one fan-out. Threads says how many lanes open: 1 is the
          recency lane alone, and every pair after that alternates a vector
          lane with a selected-pair lane, so 3 is recent + 1 vector + 1
          selected. Depth says how many rows each of those lanes may hand
          back -- and, tripled, how many candidates cosine offers the pick
          call that cuts them down. */}
      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span title="Lanes opened per turn. 1 is recency alone; each pair after adds a vector-found pair then a selector-named one." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Recall threads</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">
            {knobs === null ? "…" : `${knobs.recallThreads} lane${knobs.recallThreads === 1 ? "" : "s"}`}
          </span>
        </span>
        <input
          type="range"
          min={1}
          max={12}
          step={1}
          value={knobs?.recallThreads ?? 3}
          disabled={knobs === null}
          onChange={(e) => apply("recallThreads", Number(e.target.value), setRecallThreads)}
          className="accent-neutral-700 dark:accent-neutral-300"
        />
      </label>

      <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
        <span className="flex items-center justify-between">
          <span title="Rows one lane may return, and the cosine cut itself. Woken notes get half of it plus one." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Recall depth</span>
          <span className="font-mono text-neutral-800 dark:text-neutral-100">
            {knobs === null ? "…" : `${knobs.recallDepth} rows/lane`}
          </span>
        </span>
        <input
          type="range"
          min={1}
          max={10}
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
