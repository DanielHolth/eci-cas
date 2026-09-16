/**
 * Talks to the real EciCas.Host surface -- see src/EciCas.Host/Endpoints for
 * /api/perceive, /api/log and the rest.
 *
 * Empty by default, which means same origin. That is the shipped shape: the
 * host process serves the exported client at its own root, so every call
 * below is a relative one and there is no port, no CORS and no origin to get
 * wrong on someone else's machine. `next dev` is the case that needs an
 * absolute base, because there the client is on :3000 and the host is not --
 * see .env.development.
 */
export const API_BASE = process.env.NEXT_PUBLIC_ECI_API_BASE ?? "";

export async function sendPerceive(text: string): Promise<void> {
  const response = await fetch(`${API_BASE}/api/perceive`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text }),
  });

  if (!response.ok) {
    throw new Error(`perceive failed: ${response.status}`);
  }
}

/**
 * Asks the host to pick its newest passage back up and perceive it as its own
 * thought — the persona resuming a train of thought rather than answering
 * anybody. 204 when it has not written one yet, which is an ordinary early
 * state and not an error: nothing to resume, nothing to say.
 */
export async function sendNudge(): Promise<void> {
  const response = await fetch(`${API_BASE}/api/nudge`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({}),
  });

  if (!response.ok && response.status !== 204) {
    throw new Error(`nudge failed: ${response.status}`);
  }
}

export interface Tier {
  name: string;
  /** Env vars this tier's live classes need and that the host cannot see. */
  missingKeys: string[];
  /** provider/models whose last warm-up failed. Empty is unknown, not healthy. */
  unreachable: string[];
}

export interface Knobs {
  tier: string;
  tiers: Tier[];
  maxSentences: number;
  reflectionEvery: number;
  /** Characters of one person's input that reach the bus; the rest is not typed. */
  perceptionChars: number;
  contextTurns: number;
  recallDepth: number;
  // What the active tier's file on disk says, so the Save button can tell a
  // dragged value from a stored one instead of always offering to write.
  savedRecallDepth: number;
  savedMaxSentences: number;
  savedReflectionEvery: number;
  savedPerceptionChars: number;
  savedContextTurns: number;
  savedMood: string;
  mood: string;
  moods: string[];
  savedLanguage: string;
  language: string;
  languages: string[];
}

/**
 * The last knob payload anyone received, and who wants to hear about the
 * next one. Every call below funnels through `publish`, so a control that
 * moves a knob does not also have to know which parts of the surface care:
 * the Debug panel drags a slider, the input field's counter changes.
 *
 * Deliberately a module-level fan-out rather than a bus message or a
 * context provider — the knobs are already a single shared value fetched
 * from one place, and this is that place.
 */
let latest: Knobs | null = null;
const listeners = new Set<(knobs: Knobs) => void>();

function publish(knobs: Knobs): Knobs {
  latest = knobs;
  for (const listener of listeners) {
    listener(knobs);
  }
  return knobs;
}

/** What the host last said, for a subscriber mounting after the fetch. */
export const latestKnobs = (): Knobs | null => latest;

export function subscribeKnobs(listener: (knobs: Knobs) => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export async function fetchKnobs(): Promise<Knobs> {
  const response = await fetch(`${API_BASE}/api/knobs`);
  if (!response.ok) {
    throw new Error(`knobs fetch failed: ${response.status}`);
  }
  return publish(await response.json());
}

async function postKnobs(body: Partial<Record<"maxSentences" | "reflectionEvery" | "perceptionChars" | "contextTurns" | "recallDepth" | "mood" | "tier" | "language", number | string>>): Promise<Knobs> {
  const response = await fetch(`${API_BASE}/api/knobs`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`knobs update failed: ${response.status}`);
  }
  return publish(await response.json());
}

export const setMaxSentences = (maxSentences: number) => postKnobs({ maxSentences });
export const setReflectionEvery = (reflectionEvery: number) => postKnobs({ reflectionEvery });
export const setPerceptionChars = (perceptionChars: number) => postKnobs({ perceptionChars });

export const setContextTurns = (contextTurns: number) => postKnobs({ contextTurns });
export const setRecallDepth = (recallDepth: number) => postKnobs({ recallDepth });
export const setMood = (mood: string) => postKnobs({ mood });
export const setTier = (tier: string) => postKnobs({ tier });
export const setLanguage = (language: string) => postKnobs({ language });

/** Writes every live knob into the active tier's appsettings file -- both
 * the source tree's copy and the one the binary loads -- so a setting
 * arrived at by dragging outlives the process that found it. */
export async function saveKnobs(): Promise<Knobs> {
  const response = await fetch(`${API_BASE}/api/knobs/save`, { method: "POST" });
  if (!response.ok) {
    throw new Error(`knobs save failed: ${response.status}`);
  }
  return publish(await response.json());
}

/**
 * Correcting the index. The extractor is a small model and it will
 * sometimes keep a sentence nobody said; everything downstream treats facts
 * as recomputable, so a person is allowed to fix one directly. The utterance
 * behind it is ground truth and is never touched from here.
 */
export async function reviseFact(id: string, text: string): Promise<void> {
  const response = await fetch(`${API_BASE}/api/facts/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ text }),
  });
  if (!response.ok) {
    throw new Error(`fact revise failed: ${response.status}`);
  }
}

export async function deleteFact(id: string): Promise<void> {
  const response = await fetch(`${API_BASE}/api/facts/${encodeURIComponent(id)}`, { method: "DELETE" });
  // 404 is success as far as the panel is concerned: the row is not there,
  // which is what was asked for. Anything else is worth surfacing.
  if (!response.ok && response.status !== 404) {
    throw new Error(`fact delete failed: ${response.status}`);
  }
}
