"use client";

import { useEffect, useState } from "react";
import {
  Knobs,
  fetchKnobs,
  saveKnobs,
  setMaxSentences,
  setPerceptionChars,
  setContextTurns,
  setRecallDepth,
  setReflectionEvery,
  setMood,
  setTier,
  setLanguage,
  setScreenCaptureEnabled,
} from "@/lib/api";
import { ResizableAside } from "@/components/ResizableAside";
import { AccountChip } from "@/components/AccountChip";
import { ThemeToggle } from "@/components/ThemeToggle";
import { FADE_MS_MIN, FADE_MS_MAX } from "@/lib/useFadeMs";
import type { Account } from "@/lib/account";

const LANGUAGE_LABELS: Record<string, string> = { en: "English", fr: "French", es: "Spanish", de: "German" };

/**
 * Everything a person can tune, in one place: live session experiments that
 * can become configuration (see EciCas.Core.RuntimeKnobs / KnobDefaults) and
 * the per-browser preferences (voice, bubble fade, theme, profile) that
 * already persist themselves to localStorage the instant they change.
 */
export function SettingsPanel({
  revision = 0,
  enablePowerShell,
  onTogglePowerShell,
  onClose,
  account,
  onEditAccount,
  voices,
  voiceURI,
  setVoiceURI,
  fadeMs,
  setFadeMs,
}: {
  revision?: number;
  enablePowerShell: boolean;
  onTogglePowerShell: (enabled: boolean) => void;
  onClose: () => void;
  account: Account;
  onEditAccount: () => void;
  voices: SpeechSynthesisVoice[];
  voiceURI: string;
  setVoiceURI: (uri: string) => void;
  fadeMs: number;
  setFadeMs: (ms: number) => void;
}) {
  const [knobs, setKnobs] = useState<Knobs | null>(null);
  const [saving, setSaving] = useState(false);

  // Retried rather than defaulted. A failed fetch used to install an
  // invented payload -- tier "Mock", one tier in the list, the sliders at
  // their compiled-in numbers -- which on a cold boot, where the surface is
  // simply up before the host is, read as a statement about the running host
  // and said Mock while Free was answering. The panel now shows nothing
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
    // Same revision as useVitals and usePersona, and for the same reason:
    // these change only as a consequence of a turn, so polling would be
    // asking a question whose answer cannot have moved.
  }, [revision]);

  async function apply<K extends keyof Knobs>(key: K, value: Knobs[K], write: (v: never) => Promise<Knobs>) {
    setKnobs((prev) => (prev ? { ...prev, [key]: value } : prev));
    try {
      // Adopt the host's answer rather than only the optimistic value: a
      // tier switch re-seeds recallDepth, so the write that moved one
      // control is what tells us the others moved too.
      setKnobs(await write(value as never));
    } catch {
      // Host unreachable — the control still reflects the attempted value;
      // the next successful fetch will correct it if the write never landed.
    }
  }

  // Every knob is written back now: each tier file carries a Knobs section
  // (see KnobDefaults) alongside Recall's, so a drag on any slider can
  // become the tier's own default, not just the two Recall ones. Language
  // has no per-tier opinion but is still worth flagging as unsaved -- see
  // KnobsEndpoints' separate, best-effort write to the base appsettings.json.
  const dirty =
    knobs !== null &&
    (knobs.recallDepth !== knobs.savedRecallDepth ||
      knobs.maxSentences !== knobs.savedMaxSentences ||
      knobs.reflectionEvery !== knobs.savedReflectionEvery ||
      knobs.perceptionChars !== knobs.savedPerceptionChars ||
      knobs.contextTurns !== knobs.savedContextTurns ||
      knobs.mood !== knobs.savedMood ||
      knobs.language !== knobs.savedLanguage ||
      knobs.screenCaptureEnabled !== knobs.savedScreenCaptureEnabled);

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

  return (
    <ResizableAside side="left" title="Settings" onClose={onClose}>
      <div className="border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
        <div className="mb-2 flex items-center justify-between">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">
            Profile & display
          </h3>
        </div>

        <div className="mb-2 flex items-center justify-between gap-2">
          <span className="text-xs text-neutral-600 dark:text-neutral-300">Profile</span>
          <AccountChip account={account} onEdit={onEditAccount} />
        </div>

        <div className="mb-2 flex items-center justify-between gap-2">
          <span className="text-xs text-neutral-600 dark:text-neutral-300">Theme</span>
          <ThemeToggle />
        </div>

        {voices.length > 0 && (
          <label className="mb-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
            <span>Voice</span>
            <select
              value={voiceURI}
              onChange={(e) => setVoiceURI(e.target.value)}
              className="rounded border border-neutral-300 bg-white px-1 py-0.5 text-xs text-neutral-900 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:[color-scheme:dark]"
            >
              <option value="">Default voice</option>
              {voices.map((v) => (
                <option key={v.voiceURI} value={v.voiceURI}>
                  {v.name} ({v.lang})
                </option>
              ))}
            </select>
          </label>
        )}

        <label className="flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
          <span className="flex items-center justify-between">
            <span>Bubble fade</span>
            <span className="tabular-nums">{(fadeMs / 1000).toFixed(1)}s</span>
          </span>
          <input
            type="range"
            min={FADE_MS_MIN}
            max={FADE_MS_MAX}
            step={500}
            value={fadeMs}
            onChange={(e) => setFadeMs(Number(e.target.value))}
            aria-label="How long the heard/idea/reply bubbles stay up"
            className="accent-neutral-700 dark:accent-neutral-300"
          />
        </label>

        <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
          <span title="Which language whisper is told to expect -- pinned rather than auto-detected, so a take never lands in the wrong language mid-conversation." className="cursor-help decoration-dotted underline-offset-2 hover:underline">
            Dictation language
          </span>
          <select
            value={knobs?.language ?? "en"}
            disabled={knobs === null}
            onChange={(e) => apply("language", e.target.value, setLanguage)}
            className="rounded border border-neutral-300 bg-white px-1 py-0.5 text-xs text-neutral-900 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:[color-scheme:dark]"
          >
            {(knobs?.languages ?? Object.keys(LANGUAGE_LABELS)).map((code) => (
              <option key={code} value={code}>
                {LANGUAGE_LABELS[code] ?? code}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div className="px-3 py-2">
        <div className="mb-2 flex items-center justify-between">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">
            Knobs
          </h3>
          {/* Grey until the live knobs differ from the tier file (or, for
              language, the base file), so the button doubles as the answer
              to "is what I am running what is written down". */}
          <button
            type="button"
            onClick={save}
            disabled={!dirty || saving}
            title={dirty ? `Write to appsettings.${knobs?.tier}.json` : "Nothing changed since the saved settings"}
            className="rounded border border-neutral-300 px-2 py-0.5 text-[11px] text-neutral-700 hover:bg-neutral-100 disabled:cursor-default disabled:border-neutral-200 disabled:text-neutral-400 disabled:hover:bg-transparent dark:border-neutral-700 dark:text-neutral-200 dark:hover:bg-neutral-800 dark:disabled:border-neutral-800 dark:disabled:text-neutral-600"
          >
            {saving ? "Saving…" : "Save"}
          </button>
        </div>

        <label className="mb-3 flex items-center justify-between gap-2 rounded border border-neutral-200 px-2 py-1.5 text-xs text-neutral-700 dark:border-neutral-800 dark:text-neutral-200">
          <span className="font-medium">Enable PowerShell [Preview]</span>
          <input
            type="checkbox"
            checked={enablePowerShell}
            onChange={(e) => onTogglePowerShell(e.target.checked)}
            className="h-4 w-4 accent-neutral-700 dark:accent-neutral-300"
          />
        </label>

        <label
          title="Off means no capture ever happens. On, the shell takes one screenshot each time the voice key arms, so a question can be about what's on screen. Nothing is sent to a model unless a tier's Sight is also enabled. Requires Save and a restart of the shell to take effect."
          className="mb-3 flex items-center justify-between gap-2 rounded border border-neutral-200 px-2 py-1.5 text-xs text-neutral-700 dark:border-neutral-800 dark:text-neutral-200"
        >
          <span className="font-medium">Enable screen capture [disclaimer]</span>
          <input
            type="checkbox"
            checked={knobs?.screenCaptureEnabled ?? false}
            disabled={knobs === null}
            onChange={(e) => apply("screenCaptureEnabled", e.target.checked, setScreenCaptureEnabled)}
            className="h-4 w-4 accent-neutral-700 dark:accent-neutral-300"
          />
        </label>

        {/* A tier is a preset over everything below it -- which models back
            which class, how wide Recall fans out, whether Reflection runs at
            all -- so it sits above them rather than among them. A tier whose
            keys the host cannot see is listed and disabled: knowing Pro
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
                {t.unreachable.length > 0 ? ` — unreachable: ${t.unreachable.join(", ")}` : ""}
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
            <span title="Concluded turns shown to Intent before the one it is answering. Zero is a setting, not an off switch: the smallest tier trades the transcript for instructions it can still obey." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Context window</span>
            <span className="font-mono text-neutral-800 dark:text-neutral-100">
              {knobs === null ? "…" : knobs.contextTurns === 0 ? "none" : `${knobs.contextTurns} turns`}
            </span>
          </span>
          <input
            type="range"
            min={0}
            max={8}
            step={1}
            value={knobs?.contextTurns ?? 5}
            disabled={knobs === null}
            onChange={(e) => apply("contextTurns", Number(e.target.value), setContextTurns)}
            className="accent-neutral-700 dark:accent-neutral-300"
          />
        </label>

        <label className="mt-2 flex flex-col gap-1 text-xs text-neutral-600 dark:text-neutral-300">
          <span title="How the persona feels this turn. Not tone: tone is the prose, mood is the state behind it." className="cursor-help decoration-dotted underline-offset-2 hover:underline">Mood</span>
          <select
            value={knobs?.mood ?? "Neutral"}
            disabled={knobs === null}
            onChange={(e) => apply("mood", e.target.value, setMood)}
            className="rounded border border-neutral-300 bg-white px-1 py-0.5 text-xs text-neutral-900 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:[color-scheme:dark]"
          >
            {(knobs?.moods ?? []).map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
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

        {/* One fan-out knob now. The lane count went with the Librarian:
            the inverted read is a single sweep over the whole log, so there is
            nothing left to open lanes of, and depth is how many rows that one
            sweep hands back. */}
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
    </ResizableAside>
  );
}
