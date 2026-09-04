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
    setThoughtsOpen((v) => {
      const next = !v;
      setSeenReflections(reflectionCount(log));
      return next;
    });
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

      <main className="flex-1 min-w-0 overflow-hidden flex flex-col items-center gap-2 p-4 bg-neutral-50 dark:bg-neutral-950">
        <div className="flex w-full items-start justify-between gap-4">
          <div className="flex-1 text-center">
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
          <div className="flex items-center gap-2">
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

        <Avatar
          expression={turn?.impulse?.expression ?? "neutral"}
          speaking={speaking}
          identity={profile.avatar}
        />

        {turn && (turn.stage === "verdict" || turn.stage === "speaking") && turn.security.length > 0 && (
          <SecurityIcon outcomes={turn.security} />
        )}

        <Transcript turns={turns} />

        <form onSubmit={handleSubmit} className="flex w-full shrink-0 gap-2">
          <input
            type="text"
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder={`Say something to ECI-CAS, ${profile.displayName}…`}
            className="flex-1 rounded-full border border-neutral-300 bg-white px-4 py-2 text-sm text-neutral-900 placeholder:text-neutral-400 focus:outline-none focus:ring-2 focus:ring-neutral-400 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:placeholder:text-neutral-500 dark:focus:ring-neutral-500"
          />
          <button
            type="submit"
            disabled={sending || !text.trim()}
            className="rounded-full bg-neutral-800 px-5 py-2 text-sm text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-200 dark:text-neutral-900 dark:hover:bg-white"
          >
            Send
          </button>
        </form>
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
