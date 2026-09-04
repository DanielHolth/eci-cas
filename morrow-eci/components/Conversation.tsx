"use client";

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import { ThoughtBubbles } from "@/components/ThoughtBubbles";
import { SecurityIcon } from "@/components/SecurityIcon";
import { Transcript } from "@/components/Transcript";
import { ConsolidationDoodle } from "@/components/ConsolidationDoodle";
import { EventLog } from "@/components/EventLog";
import { IdeaStream } from "@/components/IdeaStream";
import { ProfileChip } from "@/components/ProfileChip";
import { ThemeToggle } from "@/components/ThemeToggle";
import { useEciStream } from "@/lib/useEciStream";
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
  const { turns, connected, acknowledge } = useEciStream(profile.id);
  const log = useTurnLog(profile.id);
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [logOpen, setLogOpen] = useState(false);

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
      <main className="flex-1 min-w-0 overflow-hidden flex flex-col items-center gap-6 p-10 bg-neutral-50 dark:bg-neutral-950">
        <div className="flex w-full max-w-md items-start justify-between gap-4">
          <div className="flex-1 text-center">
            <h1 className="text-lg font-semibold text-neutral-800 dark:text-neutral-100">ECI-CAS Avatar</h1>
            <p className="text-sm text-neutral-500 dark:text-neutral-400">
              {connected ? "Live" : "Disconnected"} ·{" "}
              {turn ? <span className="font-mono">{turn.stage}</span> : "waiting for a first thought"}
            </p>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setLogOpen((v) => !v)}
              aria-pressed={logOpen}
              className="rounded-full border border-neutral-300 px-3 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-900"
            >
              History
            </button>
            <ProfileChip profile={profile} onSwitch={onSwitch} />
            <ThemeToggle />
          </div>
        </div>

        <IdeaStream turns={turns} onOpen={openInLog} />

        <Avatar
          expression={turn?.impulse?.expression ?? "neutral"}
          reflex={turn?.impulse?.reflex ?? "At rest."}
          speaking={speaking}
          identity={profile.avatar}
        />

        {turn && turn.bundle.length > 0 && (
          <ThoughtBubbles findings={turn.bundle} faded={turn.stage === "speaking"} />
        )}

        {turn && (turn.stage === "verdict" || turn.stage === "speaking") && turn.security.length > 0 && (
          <SecurityIcon outcomes={turn.security} />
        )}

        {turn?.stage === "speaking" && turn.epoch && (
          <ConsolidationDoodle
            epoch={turn.epoch}
            onAcknowledge={(epochId) => acknowledge(turn.turnId, epochId)}
          />
        )}

        <Transcript turns={turns} />

        <form onSubmit={handleSubmit} className="flex w-full max-w-md shrink-0 gap-2">
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
