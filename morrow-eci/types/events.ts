/**
 * Typed mirror of the ECI-CAS bus event shapes this app cares about.
 * Kept close to the real backend contracts (src/EciCas.Agents/*,
 * docs plan §1 roster). See morrow-eci/README.md — "what this app is
 * allowed to do".
 */

/** Impulse's fixed, closed expression vocabulary. */
export type Expression = "angry" | "scared" | "sad" | "warm" | "alert" | "neutral";

/** The three advisory agents whose findings feed Governance's bundle
 * (Impulse's own advisory isn't shown here — it drives Avatar directly). */
export type BundleAgent = "librarian" | "recall" | "identity" | "hindsight";

/** Security's verdict vocabulary (src/EciCas.Agents/Security). */
export type Verdict = "green" | "yellow" | "red";

/** A single terse keyword-style finding from one of the bundle agents. */
export interface BundleFinding {
  agent: BundleAgent;
  text: string;
}

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

/** A consolidation epoch surfaced as the clickable "+" doodle. `acknowledged`
 * tracks the dedup rule client-side for this mock — the real dedup lives in
 * Archivist (M4, not built yet). */
export interface ConsolidationEpoch {
  epochId: string;
  summary: string;
  acknowledged: boolean;
}

/** One full conversational turn, staged the way the UI sequences it on
 * screen: thinking -> (optional security loop) -> speaking -> doodle.
 * `impulse`/`output` start undefined and fill in as envelopes arrive live —
 * see lib/useEciStream.ts, which is what actually produces these now. */
export interface TurnEvent {
  turnId: string;
  stage: "thinking" | "verdict" | "speaking";
  /** What the person actually said — echoed back so a turn on screen is a
   * exchange, not a reply with no question. */
  input?: string;
  impulse?: ImpulseState;
  bundle: BundleFinding[];
  security: SecurityOutcome[];
  output?: IntentOutput;
  epoch?: ConsolidationEpoch;
  /** The persona pushed one of its own ideas back onto perception. The text
   * is its thought, not something a person said, and must never be drawn as
   * an utterance. */
  selfTriggered?: boolean;
  idea?: string;
}

/**
 * Wire shape from GET /api/stream (src/EciCas.Host/EnvelopeDto.cs) — one SSE
 * `data:` line per bus envelope, every topic, unfiltered (Topics.All).
 */
export interface RawEnvelope {
  eventId: string;
  correlationId: string;
  topic: string;
  publishedBy: string;
  timestamp: string;
  severity: "restful" | "neutral" | "elevated" | "critical";
  generation: number;
  meta: Record<string, unknown>;
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
  profileId: string | null;
  startedAt: string;
  endedAt: string;
  perception: string | null;
  selfTriggered: boolean;
  impulse: string | null;
  reads: string[];
  hindsight: string[];
  intent: string | null;
  verdict: string | null;
  concern: string | null;
  writes: string[];
  passages: string[];
  idea: string | null;
  calls: SubstrateCall[];
  concluded: boolean;
  cost: number | null;
  wallClockMs: number;
}
