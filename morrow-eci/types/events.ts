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
export type BundleAgent = "reasoning" | "recall" | "self";

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
}

/** A consolidation epoch surfaced as the clickable "+" doodle. `acknowledged`
 * tracks the dedup rule client-side for this mock — the real dedup lives in
 * Consolidator (M4, not built yet). */
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
  impulse?: ImpulseState;
  bundle: BundleFinding[];
  security: SecurityOutcome[];
  output?: IntentOutput;
  epoch?: ConsolidationEpoch;
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
