"use client";

import { useEffect, useRef, useState } from "react";
import { Avatar } from "@/components/Avatar";
import { SecurityIcon } from "@/components/SecurityIcon";
import { Transcript } from "@/components/Transcript";
import { EventLog } from "@/components/EventLog";
import { ThoughtsPanel, reflectionCount } from "@/components/ThoughtsPanel";
import { ProfileChip } from "@/components/ProfileChip";
import { ThemeToggle } from "@/components/ThemeToggle";
import { useEciStream } from "@/lib/useEciStream";
import { usePersona } from "@/lib/usePersona";
import { useSpeech } from "@/lib/useSpeech";
import { greeting } from "@/lib/greeting";
import { useTurnLog } from "@/lib/useTurnLog";
import { usePerceptionLimit } from "@/lib/usePerceptionLimit";
import { fetchKnobs, latestKnobs, sendNudge, sendPerceive } from "@/lib/api";
import type { Profile } from "@/lib/profiles";

/**
 * One person's live view of the persona. Mount this with `key={profile.id}`:
 * the stream subscription and the accumulated turns both belong to the
 * profile, so switching people should discard them wholesale rather than
 * clear them in place.
 */
export function Conversation({ profile, onSwitch }: { profile: Profile; onSwitch: () => void }) {
  const { turns, connected } = useEciStream(profile.id);
  const log = useTurnLog(profile.id);

  // A rename is an ordinary archive write, so nothing pushes it. Re-reading
  // once a turn has settled is the cheapest correct trigger: settling is
  // exactly the point at which Archivist has finished writing.
  const persona = usePersona(profile.id, log.length);

  // What the host will actually accept. Null until the first fetch answers,
  // which leaves the field unbounded for that instant rather than guessing a
  // number and contradicting the host.
  const limit = usePerceptionLimit(log.length);

  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [logOpen, setLogOpen] = useState(false);
  const [thoughtsOpen, setThoughtsOpen] = useState(false);

  // Ideas seen the last time the Thoughts panel was open — the badge counts
  // only what arrived since, and opening it again clears the count back to
  // what is currently on screen rather than to zero forever. While the
  // panel is open the badge stays at zero outright: the user is already
  // looking at the content, so there is nothing "unseen" to flag.
  const [seenReflections, setSeenReflections] = useState(0);
  const unseenReflections = thoughtsOpen ? 0 : Math.max(0, reflectionCount(log) - seenReflections);

  function toggleThoughts() {
    // Both writes happen on the click, not inside an updater: a state updater
    // has to stay pure, and StrictMode runs it twice to prove it.
    setSeenReflections(reflectionCount(log));
    setThoughtsOpen((v) => !v);
  }

  // Opening the drawer at a specific event is a signal, not a selection: a
  // second click on the same bubble should reopen it after it was collapsed.
  const [opened, setOpened] = useState<{ correlationId: string; signal: number }>();

  const turn = turns[turns.length - 1];

  // Every reply is said aloud, one at a time. Not just the newest: a
  // self-triggered turn can conclude while the previous reply is still being
  // spoken, and the mouth has to run on the utterance that is actually in
  // flight rather than on whichever turn happens to be last in the array.
  const { speaking, say, unlock, voices, voiceURI, setVoiceURI } = useSpeech(turns);

  // Composed on the client, never during render: the greeting reads the
  // clock, and a server render three hours off would hydrate into a
  // different sentence than the one it sent.
  const [hello, setHello] = useState("");
  useEffect(() => {
    const opener = greeting(profile.displayName, profile.id);
    setHello(opener.text);

    // A rare opening earns a real one. The greeting itself is canned, so on
    // the one morning in forty that it is a quip, the persona follows it by
    // picking its newest passage back up and perceiving it as its own
    // thought -- which is also what wakes Hindsight (HindsightAgent's first
    // trigger is a self-flagged perception).
    //
    // Not on the two cheap tiers. Mock has no model behind it and Minimal is
    // the one people leave running all day; spending a turn on a flourish is
    // a Budget-and-up indulgence. Unknown tier means the knobs have not been
    // fetched yet, so ask -- and if that fails, say nothing and stop, since
    // a failed fetch is not a reason to spend a turn.
    if (opener.egg) {
      const spend = (tier: string) => {
        if (["mock", "minimal"].includes(tier.toLowerCase())) return;
        sendNudge(profile.id).catch(() => {});
      };
      const known = latestKnobs()?.tier;
      if (known) spend(known);
      else fetchKnobs().then((knobs) => spend(knobs.tier)).catch(() => {});
    }
    // Deliberately not in the dependency list: this is the opening line, and
    // it is said once per mount. Conversation is keyed by profile id, so a
    // different person is a different mount and gets their own.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Most browsers refuse this until the page has been interacted with, and
  // say nothing about it. handleSubmit retries it on the first gesture, but
  // waiting specifically for Send made the greeting feel mute on arrival --
  // people read it, then only heard it once they'd already typed a reply.
  // Any gesture anywhere on the page spends the same permission, so the
  // first click, key, or tap -- not necessarily a submit -- is what should
  // retry it.
  useEffect(() => {
    if (hello) say(hello);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hello]);

  const helloRef = useRef(hello);
  helloRef.current = hello;
  useEffect(() => {
    const prime = () => unlock(helloRef.current);
    window.addEventListener("pointerdown", prime, { once: true });
    window.addEventListener("keydown", prime, { once: true });
    return () => {
      window.removeEventListener("pointerdown", prime);
      window.removeEventListener("keydown", prime);
    };
    // Bound once per mount: `once: true` already retires the listener after
    // the first gesture, so there is nothing here that needs to re-run.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function openInLog(correlationId: string) {
    setLogOpen(true);
    setOpened((current) => ({ correlationId, signal: (current?.signal ?? 0) + 1 }));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!text.trim() || sending) return;
    // Inside the submit handler, where the gesture still counts.
    unlock(hello);
    setSending(true);
    try {
      await sendPerceive(text.trim(), profile.id);
      setText("");
    } catch {
      // Surface layer is down or unreachable — the connection indicator
      // already reflects that; nothing else to do client-side here.
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="flex h-screen">
      {thoughtsOpen && (
        <ThoughtsPanel records={log} onClose={() => setThoughtsOpen(false)} onOpen={openInLog} />
      )}

      <main className="flex flex-1 min-w-0 flex-col items-center overflow-hidden bg-neutral-50 p-4 dark:bg-neutral-950">
        {/* Full width of main, not of the reading column: "far left" means
            beside the drawer that opens there, and inside a centred max-w-3xl
            it would have meant a couple of hundred pixels short of it.

            Three grid tracks rather than absolute positioning. The side
            tracks are equal fractions, so the auto-sized middle one lands on
            main's centre -- the same centre the avatar below it uses -- and
            the title cannot overlap a cluster however wide the profile name
            grows, because a grid track will not let it. */}
        <div className="grid w-full shrink-0 grid-cols-[1fr_auto_1fr] items-start gap-4 pb-2">
          <div className="flex items-center justify-self-start">
            <button
              type="button"
              onClick={toggleThoughts}
              aria-pressed={thoughtsOpen}
              className="relative rounded-full border border-neutral-300 px-3 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-900"
            >
              Thoughts
              {unseenReflections > 0 && (
                <span className="absolute -right-1.5 -top-1.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-red-500 px-1 text-[10px] font-semibold text-white">
                  {unseenReflections}
                </span>
              )}
            </button>
          </div>

            <div className="text-center">
              <h1 className="text-base font-semibold text-neutral-800 dark:text-neutral-100">
                {persona.name || "ECI-CAS"}
              </h1>
              <p className="text-xs text-neutral-500 dark:text-neutral-400">
                ECI · {connected ? "Live" : "Disconnected"} ·{" "}
                {turn ? <span className="font-mono">{turn.stage}</span> : "waiting for a first thought"}
                {" · "}
                {turn?.impulse?.reflex ?? "At rest."}
              </p>
            </div>

          <div className="flex items-center gap-2 justify-self-end">
            {voices.length > 0 && (
              <select
                value={voiceURI}
                onChange={(e) => setVoiceURI(e.target.value)}
                aria-label="Voice"
                className="rounded-full border border-neutral-300 bg-white px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-300 dark:hover:bg-neutral-800"
              >
                <option value="">Default voice</option>
                {voices.map((v) => (
                  <option key={v.voiceURI} value={v.voiceURI}>
                    {v.name} ({v.lang})
                  </option>
                ))}
              </select>
            )}
            <ProfileChip profile={profile} onSwitch={onSwitch} />
            <ThemeToggle />
            {/* Rightmost of the cluster, deliberately: Debug opens EventLog,
                which docks at the true right edge of the screen, so the
                button that opens it should sit closest to that edge rather
                than buried behind the other header controls. */}
            <button
              type="button"
              onClick={() => setLogOpen((v) => !v)}
              aria-pressed={logOpen}
              className="rounded-full border border-neutral-300 px-3 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-900"
            >
              Debug
            </button>
          </div>
        </div>

        {/* The column stops widening past a readable measure; the drawers get
            the rest of a wide screen, and on a narrow one this is a no-op.
            5xl rather than 3xl: with both drawers open on a wide monitor the
            3xl cap left the conversation a narrow ribbon down the middle of
            its own region, with more empty gutter than text. */}
        {/* min-h-0 flex-1, not h-full: h-full asks for main's whole height
            regardless of the header above it, so a long transcript pushed the
            input off the bottom of the screen instead of scrolling. */}
        <div className="flex min-h-0 w-full max-w-5xl flex-1 flex-col items-center gap-2">
          <Avatar
            expression={turn?.impulse?.expression ?? "neutral"}
            speaking={speaking}
            identity={profile.avatar}
          />

          {turn && (turn.stage === "verdict" || turn.stage === "speaking") && turn.security.length > 0 && (
            <SecurityIcon outcomes={turn.security} />
          )}

          {turns.length === 0 && hello && (
            <p className="max-w-prose px-4 py-6 text-center text-sm leading-relaxed text-neutral-500 dark:text-neutral-400">
              {hello}
            </p>
          )}

          <Transcript turns={turns} />

          <form onSubmit={handleSubmit} className="w-full shrink-0">
            <div className="flex w-full gap-2">
            <input
              type="text"
              value={text}
              maxLength={limit ?? undefined}
              onChange={(e) => setText(e.target.value)}
              placeholder={`Say something to ${persona.name || "ECI-CAS"}, ${profile.displayName}…`}
              className="morrow-hand flex-1 rounded-full border border-neutral-300 bg-white px-4 py-2 text-sm text-neutral-900 placeholder:text-neutral-400 focus:outline-none focus:ring-2 focus:ring-neutral-400 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:placeholder:text-neutral-500 dark:focus:ring-neutral-500"
            />
            <button
              type="submit"
              disabled={sending || !text.trim()}
              className="rounded-full bg-neutral-800 px-5 py-2 text-sm text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-200 dark:text-neutral-900 dark:hover:bg-white"
            >
              Send
            </button>
            </div>
            {/* Standing, not conditional: the ceiling is a tier setting a
                person can move while typing, so the count has to be there
                before it matters — a number that only appears near the cap
                reads as a complaint, and one that appears only after the
                next turn is simply wrong. It turns amber in the last
                quarter and red at the cap, which is where the old sentence
                said what it said. */}
            {limit !== null && (
              <p
                className={`mt-1 px-4 text-right text-xs tabular-nums ${
                  text.length >= limit
                    ? "text-red-600 dark:text-red-400"
                    : text.length > limit * 0.75
                      ? "text-amber-600 dark:text-amber-400"
                      : "text-neutral-400 dark:text-neutral-500"
                }`}
              >
                {text.length} / {limit}
              </p>
            )}
          </form>
        </div>
      </main>

      {logOpen && (
        <EventLog
          records={log}
          openCorrelationId={opened?.correlationId}
          openSignal={opened?.signal}
          onClose={() => setLogOpen(false)}
        />
      )}
    </div>
  );
}
