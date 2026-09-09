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
  recallDepth: number;
  recallThreads: number;
  // What the active tier's file on disk says, so the Save button can tell a
  // dragged value from a stored one instead of always offering to write.
  savedRecallDepth: number;
  savedRecallThreads: number;
  savedMaxSentences: number;
  savedReflectionEvery: number;
  savedPerceptionChars: number;
  savedMood: string;
  mood: string;
  moods: string[];
}

export async function fetchKnobs(): Promise<Knobs> {
  const response = await fetch(`${API_BASE}/api/knobs`);
  if (!response.ok) {
    throw new Error(`knobs fetch failed: ${response.status}`);
  }
  return response.json();
}

async function postKnobs(body: Partial<Record<"maxSentences" | "reflectionEvery" | "perceptionChars" | "recallDepth" | "recallThreads" | "mood" | "tier", number | string>>): Promise<Knobs> {
  const response = await fetch(`${API_BASE}/api/knobs`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`knobs update failed: ${response.status}`);
  }
  return response.json();
}

export const setMaxSentences = (maxSentences: number) => postKnobs({ maxSentences });
export const setReflectionEvery = (reflectionEvery: number) => postKnobs({ reflectionEvery });
export const setPerceptionChars = (perceptionChars: number) => postKnobs({ perceptionChars });
export const setRecallDepth = (recallDepth: number) => postKnobs({ recallDepth });
export const setRecallThreads = (recallThreads: number) => postKnobs({ recallThreads });
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
  return response.json();
}
