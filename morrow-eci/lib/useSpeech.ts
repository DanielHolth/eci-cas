"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { TurnEvent } from "@/types/events";

export interface SpeechState {
  /** True while an utterance is actually in flight — what drives the mouth. */
  speaking: boolean;
  /** Queue a line the stream did not produce, such as the opening greeting. */
  say: (text: string) => void;
  /**
   * Call from inside a real click or submit handler. Browsers refuse speech
   * until a page has been interacted with, and refuse it silently, so the
   * first gesture spends a zero-volume utterance to open the permission and
   * re-queues `retry` if nothing has ever actually been heard.
   */
  unlock: (retry?: string) => void;
  /** Every voice the browser/OS currently offers. Empty until the platform reports them. */
  voices: SpeechSynthesisVoice[];
  /** voiceURI of the voice in use, or "" for the platform default. */
  voiceURI: string;
  /** Persisted across reloads; "" restores the platform default. */
  setVoiceURI: (uri: string) => void;
}

const VOICE_STORAGE_KEY = "morrow.voiceURI";

/**
 * Says every reply aloud, in the order the replies arrived.
 *
 * **Why a queue and not a call per turn.** Reflection pushes its own ideas
 * back onto perception (lib/useEciStream.ts), so the persona can conclude two
 * turns without a person typing between them. Speaking each one as it lands
 * would have the second cut the first off mid-sentence -- browsers cancel the
 * current utterance when a new one is spoken with `cancel`, and queue it only
 * if you let the queue do the queueing. Here the queue is explicit: one
 * utterance is in flight at a time, and the next starts on its `onend`.
 *
 * **What counts as new.** A turn is spoken once, keyed by its turnId. The
 * stream rebuilds the whole turns array on every envelope, so identity has to
 * come from the turn, not from the text -- two consecutive replies can be
 * the same string, and the same reply is re-delivered on every later envelope
 * of the same turn.
 *
 * **The backlog is never spoken.** Whatever is already on screen when this
 * mounts is marked as said. A remount of Conversation means a
 * remount that read twenty turns aloud would be a worse bug than silence.
 */
export function useSpeech(turns: TurnEvent[], enabled = true): SpeechState {
  const [speaking, setSpeaking] = useState(false);

  const said = useRef<Set<string>>(new Set());
  const queue = useRef<string[]>([]);
  const busy = useRef(false);
  const primed = useRef(false);
  // Whether a single syllable has ever left the speakers. Distinct from
  // `busy`: an utterance refused for want of a gesture still runs the whole
  // speak/onerror cycle, so only onstart is evidence of sound.
  const heard = useRef(false);
  // The text the browser refused for want of a gesture, if any. Retrying on
  // "not heard yet" instead doubled the greeting: a cold engine can take ten
  // seconds to start, and a click in that window queued it a second time.
  const refused = useRef<string | null>(null);

  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([]);
  const [voiceURI, setVoiceURIState] = useState("");
  const voiceRef = useRef<SpeechSynthesisVoice | null>(null);

  // Restore the saved choice once on mount. Not read during render: reading
  // localStorage during render would differ between server and client and
  // trip hydration.
  useEffect(() => {
    try {
      const saved = window.localStorage.getItem(VOICE_STORAGE_KEY);
      if (saved) setVoiceURIState(saved);
    } catch {
      // Private browsing or storage disabled — the platform default is fine.
    }
  }, []);

  // getVoices() returns nothing until the platform has loaded its voice
  // list, which on some browsers only fires the onvoiceschanged event once,
  // asynchronously, well after mount.
  useEffect(() => {
    const synth = typeof window === "undefined" ? undefined : window.speechSynthesis;
    if (!synth) return;
    // Some browsers (Chrome) list the same voiceURI more than once, which
    // would otherwise surface as duplicate <option> keys in any dropdown
    // built from this list.
    const load = () => {
      const seen = new Set<string>();
      setVoices(synth.getVoices().filter((v) => (seen.has(v.voiceURI) ? false : (seen.add(v.voiceURI), true))));
    };
    load();
    synth.addEventListener("voiceschanged", load);

    // The engine behind speechSynthesis (SAPI on Windows) loads lazily, and
    // its very first utterance in a session pays that cold-start cost --
    // measured up to ten seconds, independent of the gesture-permission wait
    // below. cancel() on an idle synth is a no-op except for one side effect:
    // it forces the browser to spin the engine up now, so the cold start
    // overlaps with the person reading the greeting instead of stacking
    // after the gesture that unlocks it.
    synth.cancel();

    return () => synth.removeEventListener("voiceschanged", load);
  }, []);

  useEffect(() => {
    voiceRef.current = voices.find((v) => v.voiceURI === voiceURI) ?? null;
  }, [voices, voiceURI]);

  const setVoiceURI = useCallback((uri: string) => {
    setVoiceURIState(uri);
    try {
      if (uri) window.localStorage.setItem(VOICE_STORAGE_KEY, uri);
      else window.localStorage.removeItem(VOICE_STORAGE_KEY);
    } catch {
      // Nothing to persist to — the choice still holds for this session.
    }
  }, []);

  // Marking the backlog has to happen before the first drain, and it has to
  // happen once -- doing it in the queueing effect would also swallow the
  // first real reply when it arrives in the same commit.
  if (!primed.current) {
    primed.current = true;
    for (const turn of turns) {
      if (turn.output?.text) {
        said.current.add(turn.turnId);
      }
    }
  }

  const drain = useCallback(() => {
    const synth = typeof window === "undefined" ? undefined : window.speechSynthesis;
    const next = queue.current.shift();
    if (!synth || next === undefined) {
      busy.current = false;
      setSpeaking(false);
      return;
    }

    busy.current = true;
    const utterance = new SpeechSynthesisUtterance(next);
    if (voiceRef.current) utterance.voice = voiceRef.current;
    utterance.onstart = () => {
      heard.current = true;
      setSpeaking(true);
    };
    // Both hands go to the same place: an utterance that errors (no voice
    // installed, autoplay refused) must not wedge the queue shut.
    utterance.onend = drain;
    utterance.onerror = (event) => {
      if (event.error === "not-allowed") refused.current = next;
      drain();
    };
    synth.speak(utterance);
  }, []);

  const say = useCallback(
    (text: string) => {
      if (!enabled || !text) return;
      queue.current.push(text);
      if (!busy.current) {
        drain();
      }
    },
    [drain, enabled],
  );

  const unlock = useCallback(
    (retry?: string) => {
      const synth = typeof window === "undefined" ? undefined : window.speechSynthesis;
      if (!synth || !enabled) return;

      // Silent, and outside the queue: it exists to spend the gesture, not to
      // be heard, and routing it through the queue would mark the queue busy
      // for something that says nothing.
      const opener = new SpeechSynthesisUtterance(" ");
      opener.volume = 0;
      synth.speak(opener);

      if (retry && !heard.current && refused.current === retry) {
        refused.current = null;
        say(retry);
      }
    },
    [enabled, say],
  );

  useEffect(() => {
    for (const turn of turns) {
      const text = turn.output?.text;
      if (!text || said.current.has(turn.turnId)) {
        continue;
      }
      said.current.add(turn.turnId);
      if (enabled) {
        queue.current.push(text);
      }
    }

    if (enabled && !busy.current && queue.current.length > 0) {
      drain();
    }
  }, [turns, enabled, drain]);

  // Leaving an utterance in flight would go on talking over the next view,
  // since speechSynthesis is a window-wide singleton and outlives this mount.
  useEffect(() => {
    return () => {
      window.speechSynthesis?.cancel();
    };
  }, []);

  return { speaking, say, unlock, voices, voiceURI, setVoiceURI };
}
