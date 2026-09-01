"use client";

import { useState } from "react";
import { AVATAR_CHOICES, type Profile } from "@/lib/profiles";

/**
 * Cold-start screen: pick who is talking, or make a new profile. Two fields
 * and no auth by design — iteration 1 is about telling people apart, not
 * about keeping them out of each other's things (docs/roadmap.md).
 */
export function ProfilePicker({
  profiles,
  loading,
  error,
  onSelect,
  onCreate,
  onCancel,
}: {
  profiles: Profile[];
  loading: boolean;
  error: string | null;
  onSelect: (profile: Profile) => void;
  onCreate: (displayName: string, avatar: string) => Promise<void>;
  onCancel?: () => void;
}) {
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [avatar, setAvatar] = useState<string>(AVATAR_CHOICES[0]);
  const [saving, setSaving] = useState(false);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!name.trim() || saving) return;
    setSaving(true);
    try {
      await onCreate(name.trim(), avatar);
      setName("");
      setCreating(false);
    } finally {
      setSaving(false);
    }
  }

  return (
    <main className="flex-1 flex flex-col items-center justify-center gap-8 p-10 bg-neutral-50 min-h-full">
      <div className="text-center">
        <h1 className="text-lg font-semibold text-neutral-800">Who&apos;s talking?</h1>
        <p className="text-sm text-neutral-500">
          Everyone gets their own memories — and their own Morrow.
        </p>
      </div>

      {error && <p className="text-sm text-red-600">{error}</p>}

      {creating ? (
        <form onSubmit={handleCreate} className="flex w-full max-w-sm flex-col gap-5">
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Your name"
            autoFocus
            className="rounded-full border border-neutral-300 px-4 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-neutral-400"
          />

          <div className="grid grid-cols-6 gap-2" role="radiogroup" aria-label="Pick an avatar">
            {AVATAR_CHOICES.map((choice) => (
              <button
                key={choice}
                type="button"
                role="radio"
                aria-checked={avatar === choice}
                aria-label={`Avatar ${choice}`}
                onClick={() => setAvatar(choice)}
                className={`aspect-square rounded-full text-2xl transition ${
                  avatar === choice
                    ? "bg-white ring-2 ring-neutral-800"
                    : "bg-white/60 ring-1 ring-neutral-200 hover:ring-neutral-400"
                }`}
              >
                {choice}
              </button>
            ))}
          </div>

          <div className="flex gap-2">
            <button
              type="submit"
              disabled={!name.trim() || saving}
              className="flex-1 rounded-full bg-neutral-800 px-5 py-2 text-sm text-white hover:bg-neutral-700 disabled:opacity-40"
            >
              {saving ? "Creating…" : "Create profile"}
            </button>
            <button
              type="button"
              onClick={() => setCreating(false)}
              className="rounded-full px-5 py-2 text-sm text-neutral-500 hover:text-neutral-800"
            >
              Back
            </button>
          </div>
        </form>
      ) : (
        <div className="flex w-full max-w-sm flex-col items-center gap-6">
          {loading ? (
            <p className="text-sm text-neutral-400">Looking for profiles…</p>
          ) : (
            <div className="flex flex-wrap justify-center gap-4">
              {profiles.map((profile) => (
                <button
                  key={profile.id}
                  onClick={() => onSelect(profile)}
                  aria-label={`Continue as ${profile.displayName}`}
                  className="flex w-24 flex-col items-center gap-2 rounded-2xl p-3 transition hover:bg-white"
                >
                  <span className="flex h-16 w-16 items-center justify-center rounded-full bg-white text-3xl ring-1 ring-neutral-200">
                    {profile.avatar}
                  </span>
                  <span className="truncate text-sm text-neutral-700">{profile.displayName}</span>
                </button>
              ))}
            </div>
          )}

          <button
            onClick={() => setCreating(true)}
            className="rounded-full border border-dashed border-neutral-300 px-5 py-2 text-sm text-neutral-600 hover:border-neutral-500 hover:text-neutral-900"
          >
            + New profile
          </button>

          {onCancel && (
            <button onClick={onCancel} className="text-sm text-neutral-400 hover:text-neutral-700">
              Never mind
            </button>
          )}
        </div>
      )}
    </main>
  );
}
