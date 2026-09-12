"use client";

/**
 * Who is using this install. There used to be a registry of profiles on the
 * host, one directory of memories each; an account has one person, so the
 * registry had one row and the host has no opinion about it any more. What
 * is left is a name and an emoji for the greeting and the header — browser
 * state, never sent anywhere.
 */
export interface Account {
  displayName: string;
  avatar: string;
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

const ACCOUNT_STORAGE_KEY = "eci.account";

export function readAccount(): Account | null {
  try {
    const raw = window.localStorage.getItem(ACCOUNT_STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<Account>;
    return parsed.displayName ? { displayName: parsed.displayName, avatar: parsed.avatar || AVATAR_CHOICES[0] } : null;
  } catch {
    return null;
  }
}

export function writeAccount(account: Account | null): void {
  try {
    if (account === null) {
      window.localStorage.removeItem(ACCOUNT_STORAGE_KEY);
    } else {
      window.localStorage.setItem(ACCOUNT_STORAGE_KEY, JSON.stringify(account));
    }
  } catch {
    // Private browsing or storage disabled — the setup screen just reappears
    // on reload, which is a fair fallback and not worth surfacing.
  }
}
