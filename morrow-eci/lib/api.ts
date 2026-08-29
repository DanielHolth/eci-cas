/**
 * Talks to the real EciCas.Host surface (M5) — see src/EciCas.Host/Program.cs
 * for /api/perceive and /api/stream. Base URL is overridable via
 * NEXT_PUBLIC_ECI_API_BASE for anyone not running the host on its
 * appsettings.json default (http://localhost:5179).
 */
export const API_BASE = process.env.NEXT_PUBLIC_ECI_API_BASE ?? "http://localhost:5179";

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
