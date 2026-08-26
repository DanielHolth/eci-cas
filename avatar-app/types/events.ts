/**
 * Typed mirror of the ECI-CAS bus event shapes this app cares about.
 * Kept close to the real backend contracts (agents/impulse/agent.py,
 * agents/security/agent.py, docs/archive/v0-35-parallel-fanout-draft.md)
 * so that swapping the mock feed (lib/mockTurn.ts) for a real
 * `system.control` / `events.*` subscription is a drop-in later, not a
 * rewrite. See avatar-app/README.md — "what this app is allowed to do".
 */

/** Impulse's fixed, closed expression vocabulary (agents/impulse/agent.py EXPRESSIONS). */
export type Expression = "angry" | "scared" | "sad" | "warm" | "alert" | "neutral";

/** One of the three fan-out agents whose keyword findings feed Intent's bundle. */
export type BundleAgent = "analytics" | "personality" | "knowledge";

/** Security's verdict vocabulary (agents/security/agent.py). */
export type Verdict = "green" | "yellow" | "red";

/** One knowledge-swarm node's findings (Phase 0.8: agents/governance/
 * knowledge_swarm.py). Analytics proposes (category, topic) paths;
 * Governance queries the Parquet-backed structured store per path and
 * folds the merged results into the Knowledge bundle slot. */
export interface SwarmNode {
  category: string;
  topic: string;
  count: number;
  sample: string[];
}

/** A single terse keyword-style finding, in the shared format Analytics/
 * Personality/Knowledge all produce (§2 of the parallel-fanout draft) —
 * this is what types the three thought bubbles. As of Phase 0.8, only
 * Analytics and Personality are live fan-out workers (agents/governance/
 * buffer.py DEFAULT_WORKERS); Knowledge's slot is synthesized by
 * Governance from the swarm rather than being its own subscriber, but
 * it still lands in the same recommendations shape, so it keeps its own
 * bubble here. */
export interface BundleFinding {
  agent: BundleAgent;
  /** Terse keyword-style text, same format contract across all three agents. */
  text: string;
  /** Knowledge only: the swarm nodes behind this bubble's synthesized text. */
  swarmNodes?: SwarmNode[];
}

/** Impulse's live reflex line + the expression it maps to, carried on
 * meta.reflex / Impulse.expression() in the real system. */
export interface ImpulseState {
  reflex: string;
  expression: Expression;
}

/** Security's clearance outcome for one revision pass. On yellow/red,
 * `detail` is what Intent tried and why it was stopped (its Revise /
 * refusal content) — surfaced only on click (docs/ideas §"security-fail icon"). */
export interface SecurityOutcome {
  verdict: Verdict;
  detail?: string;
}

/** Intent's concluded output — the thing that reaches the speech bubble. */
export interface IntentOutput {
  kind: "advise" | "refuse";
  text: string;
}

/** A consolidation epoch surfaced as the clickable "+" doodle
 * (docs/ideas/consolidation-doodle.md). `acknowledged` tracks the dedup
 * rule client-side for this mock — the real dedup lives in Consolidator. */
export interface ConsolidationEpoch {
  epochId: string;
  summary: string;
  acknowledged: boolean;
}

/** One full conversational turn, staged the way the mock sequences it
 * on screen: thinking -> (optional security loop) -> speaking -> doodle. */
export interface TurnEvent {
  turnId: string;
  impulse: ImpulseState;
  bundle: BundleFinding[];
  security: SecurityOutcome[];
  output: IntentOutput;
  epoch?: ConsolidationEpoch;
}
