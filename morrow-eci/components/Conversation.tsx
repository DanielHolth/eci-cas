"use client";

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import { ThoughtBubbles } from "@/components/ThoughtBubbles";
import { SecurityIcon } from "@/components/SecurityIcon";
import { SpeechBubble } from "@/components/SpeechBubble";
import { ConsolidationDoodle } from "@/components/ConsolidationDoodle";
import { ProfileChip } from "@/components/ProfileChip";
import { ThemeToggle } from "@/components/ThemeToggle";
import { useEciStream } from "@/lib/useEciStream";
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
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);

  const turn = turns[turns.length - 1];

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
    <main className="flex-1 flex flex-col items-center gap-8 p-10 bg-neutral-50 dark:bg-neutral-950 min-h-full">
      <div className="flex w-full max-w-md items-start justify-between gap-4">
        <div className="flex-1 text-center">
          <h1 className="text-lg font-semibold text-neutral-800 dark:text-neutral-100">ECI-CAS Avatar</h1>
          <p className="text-sm text-neutral-500 dark:text-neutral-400">
            {connected ? "Live" : "Disconnected"} ·{" "}
            {turn ? <span className="font-mono">{turn.stage}</span> : "waiting for a first thought"}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <ProfileChip profile={profile} onSwitch={onSwitch} />
          <ThemeToggle />
        </div>
      </div>

      <Avatar
        expression={turn?.impulse?.expression ?? "neutral"}
        reflex={turn?.impulse?.reflex ?? "At rest."}
        speaking={turn?.stage === "speaking" && !!turn.output}
        identity={profile.avatar}
      />

      {turn && turn.bundle.length > 0 && (
        <ThoughtBubbles findings={turn.bundle} faded={turn.stage === "speaking"} />
      )}

      {turn && (turn.stage === "verdict" || turn.stage === "speaking") && turn.security.length > 0 && (
        <SecurityIcon outcomes={turn.security} />
      )}

      {turn?.stage === "speaking" && turn.output && <SpeechBubble output={turn.output} />}

      {turn?.stage === "speaking" && turn.epoch && (
        <ConsolidationDoodle
          epoch={turn.epoch}
          onAcknowledge={(epochId) => acknowledge(turn.turnId, epochId)}
        />
      )}

      <form onSubmit={handleSubmit} className="mt-4 flex w-full max-w-md gap-2">
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
  );
}
