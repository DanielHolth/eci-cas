/**
 * Talks to the real EciCas.Host surface (M5) — see src/EciCas.Host/Program.cs
 * for /api/perceive and /api/stream. Base URL is overridable via
 * NEXT_PUBLIC_ECI_API_BASE for anyone not running the host on its
 * appsettings.json default (http://localhost:5179).
 */
export const API_BASE = process.env.NEXT_PUBLIC_ECI_API_BASE ?? "http://localhost:5179";

/** `profileId` names who is talking; the host keys drive state on it, so an
 * omitted profile lands on the device-wide state rather than anyone's own. */
export async function sendPerceive(text: string, profileId?: string): Promise<void> {
  const response = await fetch(`${API_BASE}/api/perceive`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(profileId ? { text, profileId } : { text }),
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
export async function sendNudge(profileId?: string): Promise<void> {
  const response = await fetch(`${API_BASE}/api/nudge`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(profileId ? { profileId } : {}),
  });

  if (!response.ok && response.status !== 204) {
    throw new Error(`nudge failed: ${response.status}`);
  }
}

export interface Tier {
  name: string;
  /** Env vars this tier's live classes need and that the host cannot see. */
  missingKeys: string[];
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

async function postKnobs(body: Partial<Record<"maxSentences" | "reflectionEvery" | "perceptionChars" | "contextTurns" | "recallDepth" | "mood" | "tier", number | string>>): Promise<Knobs> {
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
