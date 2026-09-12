"use client";

import type { Account } from "@/lib/account";

/** Header affordance: who Morrow thinks it's talking to, and a way to change that. */
export function AccountChip({ account, onEdit }: { account: Account; onEdit: () => void }) {
  return (
    <button
      onClick={onEdit}
      title="Change your name or face"
      className="flex items-center gap-2 rounded-full bg-white px-3 py-1.5 text-sm text-neutral-700 ring-1 ring-neutral-200 transition hover:ring-neutral-400 dark:bg-neutral-900 dark:text-neutral-200 dark:ring-neutral-700 dark:hover:ring-neutral-500"
    >
      <span className="text-base leading-none">{account.avatar}</span>
      <span className="max-w-32 truncate">{account.displayName}</span>
      <span aria-hidden className="text-neutral-400 dark:text-neutral-500">⇄</span>
    </button>
  );
}
