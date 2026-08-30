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

const BUNDLE_ADVICE_KEY: Record<BundleAgent, string> = {
  reasoning: "reasoning.advice",
  recall: "recall.results",
  self: "self.advice",
};

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
      const key = BUNDLE_ADVICE_KEY[agent];
      if (key && key in raw.meta) {
        const text = String(raw.meta[key] ?? "");
        const existing = turn.bundle.find((f) => f.agent === agent);
        if (existing) {
          existing.text = text;
        } else {
          turn.bundle.push({ agent, text });
        }
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
      turn.output = { kind: verdict === "red" ? "refuse" : "advise", text: reply };
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

/** Subscribes to GET /api/stream and assembles the raw envelope feed into
 * TurnEvent-shaped state, keyed by CorrelationId — the same grouping
 * Governance itself uses to bundle a turn's advisories. */
export function useEciStream(): EciStreamState {
  const [turns, setTurns] = useState<TurnEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const turnsRef = useRef(new Map<string, TurnEvent>());
  const orderRef = useRef<string[]>([]);

  useEffect(() => {
    const source = new EventSource(`${API_BASE}/api/stream`);

    source.onopen = () => setConnected(true);
    source.onerror = () => setConnected(false);
    source.onmessage = (event) => {
      const raw = JSON.parse(event.data) as RawEnvelope;
      applyEnvelope(turnsRef.current, orderRef.current, raw);
      setTurns(orderRef.current.map((id) => turnsRef.current.get(id)!));
    };

    return () => source.close();
  }, []);

  function acknowledge(turnId: string, epochId: string) {
    const turn = turnsRef.current.get(turnId);
    if (turn?.epoch?.epochId === epochId) {
      turn.epoch = { ...turn.epoch, acknowledged: true };
      setTurns(orderRef.current.map((id) => turnsRef.current.get(id)!));
    }
  }

  return { turns, connected, acknowledge };
}
