import type { TurnEvent } from "@/types/events";

/**
 * Canned event feed standing in for the real SSE subscription until M5
 * lands. Two turns: one clean pass, one that trips a security yellow
 * (one revision pass) then a red on the revision itself, per Governance's
 * gating matrix (docs plan §3.3): yellow buys exactly one Intent revision
 * before proceeding regardless — this mock shows a yellow->red sequence
 * to also exercise the Blocked-notice path in the same turn.
 */
export const MOCK_TURNS: TurnEvent[] = [
  {
    turnId: "turn-1",
    impulse: {
      reflex: "Nothing urgent — taking the scenic route through Reasoning.",
      expression: "neutral",
    },
    bundle: [
      { agent: "reasoning", text: "Straightforward factual question, no ambiguity." },
      { agent: "recall", text: "No prior conversation on this topic." },
      { agent: "self", text: "Answer plainly, no persona flourish needed." },
    ],
    security: [{ verdict: "green" }],
    output: {
      kind: "advise",
      text: "The Channel-based bus dispatches every subscriber on its own queue, so a slow one never blocks a publisher.",
    },
    epoch: {
      epochId: "epoch-1",
      summary: "Learned: user is actively rebuilding the bus dispatch model in C#.",
      acknowledged: false,
    },
  },
  {
    turnId: "turn-2",
    impulse: {
      reflex: "Something here brushes against a rule — flag it for Security.",
      expression: "alert",
    },
    bundle: [
      { agent: "reasoning", text: "Request touches account credential handling." },
      { agent: "recall", text: "No stored precedent for this request." },
      { agent: "self", text: "Stay cautious, don't overpromise capability." },
    ],
    security: [
      { verdict: "yellow", detail: "Reply mentions disclosing credentials — not a violation, but ambiguous enough to revise." },
      { verdict: "red", detail: "Revision still names a concrete credential value. Rule 'disclose-credentials' fires — blocked." },
    ],
    output: {
      kind: "refuse",
      text: "I can't help with that: looks like it involves sharing a credential.",
    },
  },
];
