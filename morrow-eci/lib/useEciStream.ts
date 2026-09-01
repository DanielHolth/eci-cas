"use client";

import { useEffect, useRef, useState } from "react";
import { API_BASE } from "@/lib/api";
import type {
  BundleAgent,
  ConsolidationEpoch,
  Expression,
  RawEnvelope,
  SecurityOutcome,
  TurnEvent,
  Verdict,
} from "@/types/events";

const MAX_TURNS = 20;

/** Each bundle agent thinks in its own shape, so each gets its own reader
 * off the raw meta. Reasoning's thought is which archive pairs it chose;
 * Recall's is the rows it picked out of them; Self's is already a line of
 * text. All three collapse to one terse string for a bubble. */
const BUNDLE_THOUGHT: Record<BundleAgent, (meta: Record<string, unknown>) => string | undefined> = {
  reasoning: (meta) => {
    const pairs = meta["reasoning.selected_pairs"];
    return Array.isArray(pairs) && pairs.length > 0
      ? pairs.map((p) => `${p.category}/${p.topic}`).join(", ")
      : undefined;
  },
  recall: (meta) => {
    const facts = meta["recall.facts"];
    return Array.isArray(facts) && facts.length > 0
      ? facts.map((f) => `${f.subject} ${f.key} = ${f.value}`).join("; ")
      : undefined;
  },
  self: (meta) => (typeof meta["self.advice"] === "string" ? meta["self.advice"] : undefined),
};

function record(turn: TurnEvent, agent: BundleAgent, text: string): void {
  const existing = turn.bundle.find((f) => f.agent === agent);
  if (existing) {
    existing.text = text;
  } else {
    turn.bundle.push({ agent, text });
  }
}

/**
 * Impulse never emits an Expression — that vocabulary was invented for the
 * mock shell. This maps severity + the reflex text onto it as a placeholder
 * until real expression-selection logic exists on the backend (open question
 * carried over from the mock-era README, see M6/M7 "Open questions").
 */
function deriveExpression(severity: RawEnvelope["severity"], reflex: string): Expression {
  if (severity === "critical") return "scared";
  if (severity === "elevated") return reflex.includes("urgent") ? "alert" : "warm";
  if (severity === "restful") return "warm";
  return "neutral";
}

function emptyTurn(turnId: string): TurnEvent {
  return { turnId, stage: "thinking", bundle: [], security: [] };
}

function applyEnvelope(turns: Map<string, TurnEvent>, order: string[], raw: RawEnvelope): void {
  const turnId = raw.correlationId;
  let turn = turns.get(turnId);

  if (raw.topic === "events.perception") {
    if (!turn) {
      turn = emptyTurn(turnId);
      turns.set(turnId, turn);
      order.push(turnId);
      while (order.length > MAX_TURNS) {
        turns.delete(order.shift()!);
      }
    }
    turn.input = String(raw.meta["perception.text"] ?? "");
    return;
  }

  if (!turn) {
    // Advisory/verdict/action arriving before its perception has been seen
    // by this client — same GetOrAdd reasoning as Governance's own bundling
    // (src/EciCas.Agents/Governance/GovernanceAgent.cs). Open the turn now.
    turn = emptyTurn(turnId);
    turns.set(turnId, turn);
    order.push(turnId);
    while (order.length > MAX_TURNS) {
      turns.delete(order.shift()!);
    }
  }

  switch (raw.topic) {
    case "events.advisories": {
      if (raw.publishedBy === "Impulse") {
        const reflex = String(raw.meta["impulse.advice"] ?? "");
        turn.impulse = { reflex, expression: deriveExpression(raw.severity, reflex) };
        break;
      }
      const agent = raw.publishedBy.toLowerCase() as BundleAgent;
      const thought = BUNDLE_THOUGHT[agent]?.(raw.meta);
      if (thought) {
        record(turn, agent, thought);
      }
      break;
    }
    // Reasoning's advisory is the pair selection itself, published on its
    // own topic rather than events.advisories — so it needs its own case,
    // not an entry in the advisory switch above.
    case "events.selected-pairs": {
      const thought = BUNDLE_THOUGHT.reasoning(raw.meta);
      if (thought) {
        record(turn, "reasoning", thought);
      }
      break;
    }
    case "events.verdict": {
      const verdict = String(raw.meta["security.verdict"] ?? "green").toLowerCase() as Verdict;
      const detail = raw.meta["security.concern"] as string | undefined;
      const outcome: SecurityOutcome = detail ? { verdict, detail } : { verdict };
      turn.security.push(outcome);
      turn.stage = "verdict";
      break;
    }
    case "events.action": {
      const reply = String(raw.meta["intent.reply"] ?? "");
      const verdict = String(raw.meta["security.verdict"] ?? "green").toLowerCase();
      turn.output = {
        kind: verdict === "red" ? "refuse" : "advise",
        text: reply,
        degraded: raw.meta["governance.degraded"] === true,
      };
      turn.stage = "speaking";
      break;
    }
    case "system.control": {
      if (raw.meta["control.kind"] === "Written") {
        const epoch: ConsolidationEpoch = {
          epochId: raw.eventId,
          summary: "Consolidator wrote this turn to the archive.",
          acknowledged: false,
        };
        turn.epoch = epoch;
      }
      break;
    }
  }
}

export interface EciStreamState {
  turns: TurnEvent[];
  connected: boolean;
  acknowledge: (turnId: string, epochId: string) => void;
}

/** Subscribes to GET /api/stream, optionally scoped to one profile, and
 * assembles the raw envelope feed into TurnEvent-shaped state, keyed by
 * CorrelationId — the same grouping Governance itself uses to bundle a turn's
 * advisories.
 *
 * Switching profiles is a remount, not a reset: Conversation is keyed by
 * profile id, so the accumulated turns go with the component rather than
 * being cleared in place. One person's conversation never bleeds into the
 * next person's window. */
export function useEciStream(profileId?: string): EciStreamState {
  const [turns, setTurns] = useState<TurnEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const turnsRef = useRef(new Map<string, TurnEvent>());
  const orderRef = useRef<string[]>([]);

  useEffect(() => {
    const query = profileId ? `?profileId=${encodeURIComponent(profileId)}` : "";
    const source = new EventSource(`${API_BASE}/api/stream${query}`);

    source.onopen = () => setConnected(true);
    source.onerror = () => setConnected(false);
    source.onmessage = (event) => {
      const raw = JSON.parse(event.data) as RawEnvelope;
      applyEnvelope(turnsRef.current, orderRef.current, raw);
      setTurns(orderRef.current.map((id) => turnsRef.current.get(id)!));
    };

    return () => source.close();
  }, [profileId]);

  function acknowledge(turnId: string, epochId: string) {
    const turn = turnsRef.current.get(turnId);
    if (turn?.epoch?.epochId === epochId) {
      turn.epoch = { ...turn.epoch, acknowledged: true };
      setTurns(orderRef.current.map((id) => turnsRef.current.get(id)!));
    }
  }

  return { turns, connected, acknowledge };
}
