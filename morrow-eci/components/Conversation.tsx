"use client";

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import { SecurityIcon } from "@/components/SecurityIcon";
import { Transcript } from "@/components/Transcript";
import { EventLog } from "@/components/EventLog";
import { ThoughtsPanel, reflectionCount } from "@/components/ThoughtsPanel";
import { ProfileChip } from "@/components/ProfileChip";
import { ThemeToggle } from "@/components/ThemeToggle";
import { useEciStream } from "@/lib/useEciStream";
import { usePersona } from "@/lib/usePersona";
import { useSpeaking } from "@/lib/useSpeaking";
import { useTurnLog } from "@/lib/useTurnLog";
import { usePerceptionLimit } from "@/lib/usePerceptionLimit";
import { sendPerceive } from "@/lib/api";
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

  // The last thing actually said, which is not the last turn: Reflection's
  // ideas arrive as turns of their own and say nothing aloud.
  const spoken = turns.filter((t) => t.output);
  const speaking = useSpeaking(spoken[spoken.length - 1]?.output?.text);

  function openInLog(correlationId: string) {
    setLogOpen(true);
    setOpened((current) => ({ correlationId, signal: (current?.signal ?? 0) + 1 }));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!text.trim() || sending) return;
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
            <button
              type="button"
              onClick={() => setLogOpen((v) => !v)}
              aria-pressed={logOpen}
              className="rounded-full border border-neutral-300 px-3 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-900"
            >
              Debug
            </button>
            <ProfileChip profile={profile} onSwitch={onSwitch} />
            <ThemeToggle />
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

          <Transcript turns={turns} />

          <form onSubmit={handleSubmit} className="w-full shrink-0">
            <div className="flex w-full gap-2">
            <input
              type="text"
              value={text}
              maxLength={limit ?? undefined}
              onChange={(e) => setText(e.target.value)}
              placeholder={`Say something to ${persona.name || "ECI-CAS"}, ${profile.displayName}…`}
              className="flex-1 rounded-full border border-neutral-300 bg-white px-4 py-2 text-sm text-neutral-900 placeholder:text-neutral-400 focus:outline-none focus:ring-2 focus:ring-neutral-400 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:placeholder:text-neutral-500 dark:focus:ring-neutral-500"
            />
            <button
              type="submit"
              disabled={sending || !text.trim()}
              className="rounded-full bg-neutral-800 px-5 py-2 text-sm text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-200 dark:text-neutral-900 dark:hover:bg-white"
            >
              Send
            </button>
            </div>
            {/* The counter only appears in the last quarter of the budget.
                A number that sits under the field from the first keystroke
                reads as a demand for brevity; one that shows up as the room
                runs out reads as the fact it is. */}
            {limit !== null && text.length > limit * 0.75 && (
              <p className="mt-1 px-4 text-right text-xs text-neutral-500 dark:text-neutral-400">
                {text.length === limit
                  ? `${limit} characters — that is all this tier reads`
                  : `${text.length} / ${limit}`}
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
