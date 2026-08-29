"use client";

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import { ThoughtBubbles } from "@/components/ThoughtBubbles";
import { SecurityIcon } from "@/components/SecurityIcon";
import { SpeechBubble } from "@/components/SpeechBubble";
import { ConsolidationDoodle } from "@/components/ConsolidationDoodle";
import { MOCK_TURNS } from "@/lib/mockTurn";
import type { ConsolidationEpoch } from "@/types/events";

type Stage = "idle" | "thinking" | "verdict" | "speaking";
const STAGES: Stage[] = ["idle", "thinking", "verdict", "speaking"];

export default function Home() {
  const [turnIndex, setTurnIndex] = useState(0);
  const [stageIndex, setStageIndex] = useState(0);
  const [epochs, setEpochs] = useState<Record<string, ConsolidationEpoch>>(
    () => Object.fromEntries(MOCK_TURNS.filter((t) => t.epoch).map((t) => [t.epoch!.epochId, t.epoch!])),
  );

  const turn = MOCK_TURNS[turnIndex];
  const stage = STAGES[stageIndex];
  const epoch = turn.epoch ? epochs[turn.epoch.epochId] : undefined;

  function advance() {
    if (stageIndex < STAGES.length - 1) {
      setStageIndex(stageIndex + 1);
    } else {
      const next = (turnIndex + 1) % MOCK_TURNS.length;
      setTurnIndex(next);
      setStageIndex(0);
    }
  }

  function acknowledge(epochId: string) {
    setEpochs((prev) => ({ ...prev, [epochId]: { ...prev[epochId], acknowledged: true } }));
  }

  return (
    <main className="flex-1 flex flex-col items-center gap-8 p-10 bg-neutral-50 min-h-full">
      <div className="text-center">
        <h1 className="text-lg font-semibold text-neutral-800">
          ECI-CAS Avatar — mock shell (M5/M6/M7 review)
        </h1>
        <p className="text-sm text-neutral-500">
          Turn {turnIndex + 1} of {MOCK_TURNS.length} · Stage:{" "}
          <span className="font-mono">{stage}</span>
        </p>
      </div>

      <Avatar
        expression={stage === "idle" ? "neutral" : turn.impulse.expression}
        reflex={stage === "idle" ? "At rest." : turn.impulse.reflex}
      />

      {stage !== "idle" && <ThoughtBubbles findings={turn.bundle} faded={stage === "speaking"} />}

      {(stage === "verdict" || stage === "speaking") && (
        <SecurityIcon outcomes={turn.security} />
      )}

      {stage === "speaking" && <SpeechBubble output={turn.output} />}

      {stage === "speaking" && epoch && (
        <ConsolidationDoodle epoch={epoch} onAcknowledge={acknowledge} />
      )}

      <button
        type="button"
        onClick={advance}
        className="mt-4 rounded-full bg-neutral-800 px-5 py-2 text-sm text-white hover:bg-neutral-700"
      >
        {stageIndex < STAGES.length - 1 ? "Next stage →" : "Next turn →"}
      </button>
    </main>
  );
}
