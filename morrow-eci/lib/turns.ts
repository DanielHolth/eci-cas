import type { Expression, SecurityOutcome, TurnEvent, TurnRecord, Verdict } from "@/types/events";

/** Impulse's own closed vocabulary, mirrored so an unknown word cannot blank
 * the face. Anything unrecognised is neutral rather than a throw: a new word
 * on the backend must not take the avatar down with it. */
const EXPRESSIONS: readonly string[] = ["angry", "scared", "sad", "warm", "alert", "neutral"];

const VERDICTS: readonly string[] = ["green", "yellow", "red"];

/**
 * One turn as the conversation draws it, from one turn as the host recorded
 * it. Pure and total: every field below is read off the record, nothing is
 * accumulated, and the same records in the same order always give the same
 * turns.
 *
 * That totality is the whole point. This used to be a reducer over raw bus
 * envelopes, which meant the transcript existed only in the memory of a
 * client that had been listening from the beginning -- a window opened five
 * turns in showed a full debug drawer and an empty conversation. The host
 * replays records, so a mapping over records is a conversation any window can
 * arrive late to.
 */
export function turnsFromRecords(records: TurnRecord[]): TurnEvent[] {
  return records.map((r) => ({
    turnId: r.correlationId,

    // No "speaking": the record knows the reply landed, not whether a mouth
    // is still moving. Whoever is doing the talking knows that -- see
    // useSpeech -- and says so over the top of this.
    stage: r.concluded || r.intent ? "done" : r.verdict ? "verdict" : "thinking",

    // Reflection pushes its own ideas back onto perception. That is the
    // persona thinking, not the person speaking, so it is not an utterance
    // and gets no bubble on the person's side of the transcript.
    input: r.selfTriggered ? undefined : (r.perception ?? undefined),
    selfTriggered: r.selfTriggered,
    ideaText: r.selfTriggered && !r.toolkitTriggered ? (r.perception ?? undefined) : undefined,

    impulse: r.impulse || r.expression ? { reflex: r.impulse ?? "", expression: face(r.expression) } : undefined,

    // One outcome, not a list. The host keeps the last verdict for the turn
    // rather than a pass-by-pass history, and the icon only draws the ones
    // that are not green.
    security: outcome(r.verdict, r.concern),

    output: r.intent ? { kind: r.verdict === "red" ? "refuse" : "advise", text: r.intent, degraded: r.degraded } : undefined,
  }));
}

function face(word: string | null): Expression {
  return word && EXPRESSIONS.includes(word) ? (word as Expression) : "neutral";
}

function outcome(verdict: string | null, concern: string | null): SecurityOutcome[] {
  if (!verdict || !VERDICTS.includes(verdict) || verdict === "green") return [];
  return [concern ? { verdict: verdict as Verdict, detail: concern } : { verdict: verdict as Verdict }];
}
