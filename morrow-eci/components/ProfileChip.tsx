"use client";

import type { Profile } from "@/lib/profiles";

/** Header affordance: who Morrow thinks it's talking to, and a way to change that. */
export function ProfileChip({ profile, onSwitch }: { profile: Profile; onSwitch: () => void }) {
  return (
    <button
      onClick={onSwitch}
      title="Switch profile"
      className="flex items-center gap-2 rounded-full bg-white px-3 py-1.5 text-sm text-neutral-700 ring-1 ring-neutral-200 transition hover:ring-neutral-400"
    >
      <span className="text-base leading-none">{profile.avatar}</span>
      <span className="max-w-32 truncate">{profile.displayName}</span>
      <span aria-hidden className="text-neutral-400">⇄</span>
    </button>
  );
}
