"use client";

import { useState } from "react";
import { AVATAR_CHOICES, type Account } from "@/lib/account";
import { ThemeToggle } from "@/components/ThemeToggle";
import { usePersona } from "@/lib/usePersona";

/**
 * Cold-start screen: a name and an emoji, kept in this browser. It used to
 * be a picker over several profiles; an account has one person, so what is
 * left is the one question worth asking before the first turn — what to call
 * you.
 */
export function AccountSetup({
  account,
  onSave,
  onCancel,
}: {
  account: Account | null;
  onSave: (account: Account) => void;
  onCancel?: () => void;
}) {
  const [name, setName] = useState(account?.displayName ?? "");
  const [avatar, setAvatar] = useState<string>(account?.avatar ?? AVATAR_CHOICES[0]);
  const persona = usePersona();

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!name.trim()) return;
    onSave({ displayName: name.trim(), avatar });
  }

  return (
    <main className="relative flex-1 flex flex-col items-center justify-center gap-8 p-10 bg-neutral-50 dark:bg-neutral-950 min-h-full">
      <div className="absolute right-6 top-6">
        <ThemeToggle />
      </div>

      <div className="text-center">
        <h1 className="text-lg font-semibold text-neutral-800 dark:text-neutral-100">Who&apos;s talking?</h1>
        <p className="text-sm text-neutral-500 dark:text-neutral-400">
          A name and a face, so Morrow knows who it is answering.
        </p>
        {persona.text && (
          <p className="mx-auto mt-3 max-w-sm text-sm italic text-neutral-400 dark:text-neutral-500">
            {persona.text}
          </p>
        )}
      </div>

      <form onSubmit={handleSubmit} className="flex w-full max-w-sm flex-col gap-5">
        <input
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Your name"
          autoFocus
          className="rounded-full border border-neutral-300 bg-white px-4 py-2 text-sm text-neutral-900 placeholder:text-neutral-400 focus:outline-none focus:ring-2 focus:ring-neutral-400 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100 dark:placeholder:text-neutral-500 dark:focus:ring-neutral-500"
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
                  ? "bg-white ring-2 ring-neutral-800 dark:bg-neutral-800 dark:ring-neutral-200"
                  : "bg-white/60 ring-1 ring-neutral-200 hover:ring-neutral-400 dark:bg-neutral-900/60 dark:ring-neutral-700 dark:hover:ring-neutral-500"
              }`}
            >
              {choice}
            </button>
          ))}
        </div>

        <div className="flex gap-2">
          <button
            type="submit"
            disabled={!name.trim()}
            className="flex-1 rounded-full bg-neutral-800 px-5 py-2 text-sm text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-200 dark:text-neutral-900 dark:hover:bg-white"
          >
            Start talking
          </button>
          {onCancel && (
            <button
              type="button"
              onClick={onCancel}
              className="rounded-full px-5 py-2 text-sm text-neutral-500 hover:text-neutral-800 dark:text-neutral-400 dark:hover:text-neutral-100"
            >
              Never mind
            </button>
          )}
        </div>
      </form>
    </main>
  );
}
