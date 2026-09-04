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

export interface Knobs {
  maxSentences: number;
  reflectionEvery: number;
  recallDepth: number;
  tone: string;
  tones: string[];
}

export async function fetchKnobs(): Promise<Knobs> {
  const response = await fetch(`${API_BASE}/api/knobs`);
  if (!response.ok) {
    throw new Error(`knobs fetch failed: ${response.status}`);
  }
  return response.json();
}

async function postKnobs(body: Partial<Record<"maxSentences" | "reflectionEvery" | "recallDepth" | "tone", number | string>>): Promise<Knobs> {
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
export const setRecallDepth = (recallDepth: number) => postKnobs({ recallDepth });
export const setTone = (tone: string) => postKnobs({ tone });
