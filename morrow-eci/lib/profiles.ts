"use client";

import { API_BASE } from "@/lib/api";

/** Mirrors Host's Profile record — see src/EciCas.Host/ProfileStore.cs. */
export interface Profile {
  id: string;
  displayName: string;
  avatar: string;
  createdAt: string;
}

/**
 * Emoji rather than illustrated art: no assets, legible at any size, and a
 * child can pick one in a second. The README's open question about the
 * persona's own art direction is untouched by this — these identify the
 * *person talking*, not the persona.
 */
export const AVATAR_CHOICES = [
  "🦊", "🐼", "🦉", "🐙", "🦕", "🐝",
  "🚀", "🌵", "🍄", "⚡", "🎸", "🧩",
] as const;

const ACTIVE_PROFILE_STORAGE_KEY = "eci.activeProfileId";

export async function fetchProfiles(): Promise<Profile[]> {
  const response = await fetch(`${API_BASE}/api/profiles`);
  if (!response.ok) {
    throw new Error(`profiles failed: ${response.status}`);
  }
  return (await response.json()) as Profile[];
}

export async function createProfile(displayName: string, avatar: string): Promise<Profile> {
  const response = await fetch(`${API_BASE}/api/profiles`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ displayName, avatar }),
  });

  if (!response.ok) {
    throw new Error(`create profile failed: ${response.status}`);
  }

  // 200 rather than 201 means the name was already taken and the host handed
  // back the existing profile — the picker treats that as "that's you".
  return (await response.json()) as Profile;
}

/** The active choice is per browser, not per device — it never reaches the host. */
export function readActiveProfileId(): string | null {
  try {
    return window.localStorage.getItem(ACTIVE_PROFILE_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function writeActiveProfileId(profileId: string | null): void {
  try {
    if (profileId === null) {
      window.localStorage.removeItem(ACTIVE_PROFILE_STORAGE_KEY);
    } else {
      window.localStorage.setItem(ACTIVE_PROFILE_STORAGE_KEY, profileId);
    }
  } catch {
    // Private browsing or storage disabled — the picker just reappears on
    // reload, which is a fair fallback and not worth surfacing.
  }
}
