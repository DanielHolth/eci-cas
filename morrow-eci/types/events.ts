/**
 * Typed mirror of the ECI-CAS bus event shapes this app cares about.
 * Kept close to the real backend contracts (src/EciCas.Agents/*,
 * docs plan §1 roster). See morrow-eci/README.md — "what this app is
 * allowed to do".
 */

/** Impulse's fixed, closed expression vocabulary. */
export type Expression = "angry" | "scared" | "sad" | "warm" | "alert" | "neutral";

/** Security's verdict vocabulary (src/EciCas.Agents/Security). */
export type Verdict = "green" | "yellow" | "red";

/** Impulse's live reflex line + the expression it maps to. */
export interface ImpulseState {
  reflex: string;
  expression: Expression;
}

/** Security's clearance outcome for one revision pass. On yellow/red,
 * `detail` is the concern Security matched — surfaced only on click. */
export interface SecurityOutcome {
  verdict: Verdict;
  detail?: string;
}

/** Intent's concluded output — the thing that reaches the speech bubble. */
export interface IntentOutput {
  kind: "advise" | "refuse";
  text: string;
  /** Governance flagged this turn as thought with a substrate missing —
   * the reply already says so in words, this is the visual half. */
  degraded?: boolean;
}

/** One full conversational turn, staged the way the UI sequences it on
 * screen: thinking -> (optional security flag) -> done, the last set when the
 * reply lands. Everything here is read off one TurnRecord -- see
 * lib/turns.ts, which is the only thing that produces these. */
export interface TurnEvent {
  turnId: string;
  stage: "thinking" | "verdict" | "done";
  /** What the person actually said — echoed back so a turn on screen is an
   * exchange, not a reply with no question. Absent on a turn the persona
   * started itself, which is a thought and not something anyone said. */
  input?: string;
  impulse?: ImpulseState;
  security: SecurityOutcome[];
  output?: IntentOutput;
}

/** One substrate call as the host reports it. `tokens`/`cost` are null when
 * the provider does not report them — the mock tier reports neither. */
export interface SubstrateCall {
  agent: string;
  class: string;
  label: string | null;
  latencyMs: number;
  tokens: number | null;
  cost: number | null;
  degraded: string | null;
  /** Which endpoint served it ("local", "openai", "mistral", "mock") and the
   * model id sent on the wire. Null on a call logged before these were
   * carried. A tier mixes local and vendor agents freely, so whether a turn
   * ran on your own hardware is only answerable per call. */
  provider: string | null;
  model: string | null;
  /** The split behind `tokens`, when the provider reports one. */
  promptTokens: number | null;
  completionTokens: number | null;
}

/**
 * Mirror of src/EciCas.Host/TurnLog/TurnRecord.cs — the host's own reduction
 * of one event, served by GET /api/log and /api/log/stream. Holds strings, so
 * nothing here re-derives anything from meta keys: the drawer renders slots,
 * and a null or empty slot is one it skips.
 */
export interface TurnRecord {
  seq: number;
  correlationId: string;
  startedAt: string;
  endedAt: string;
  perception: string | null;
  selfTriggered: boolean;
  impulse: string | null;
  /** What Sight made of the screenshot, as Intent was handed it. */
  sight: string | null;
  /** Which of Impulse's six words this turn wore, re-read from Governance's
   * action when a block moved it. The one thing a surface cannot re-derive
   * from the text, which is why the host carries it. */
  expression: string | null;
  reads: string[];
  hindsight: string[];
  intent: string | null;
  verdict: string | null;
  concern: string | null;
  /** Governance answered with a substrate missing. The reply already says so
   * in words; this is the slot the visual half reads. */
  degraded: boolean;
  writes: string[];
  /** Archive id per entry in `writes`, same order. Empty on a turn logged
   * before ids were carried — that row can be read but not corrected. */
  writeIds: string[];
  passages: string[];
  idea: string | null;
  calls: SubstrateCall[];
  concluded: boolean;
  cost: number | null;
  /** Running totals as of this event: this host run, and every run ever.
   * Frozen into the record, so replaying an old event shows what was true
   * then rather than what is true now. */
  sessionCost: number;
  totalCost: number;
  wallClockMs: number;
}
