"use client";

import { ResizableAside } from "@/components/ResizableAside";
import { useEffect, useRef, useState } from "react";
import { deleteFact, reviseFact } from "@/lib/api";
import type { TurnRecord } from "@/types/events";

const KIND_STYLE = {
  learned: { dot: "bg-emerald-500", label: "Learned" },
  // Its own row rather than a shade of Learned. Hindsight is the persona
  // second-guessing a turn it has already had, which is a thought about the
  // conversation -- not a fact taken out of it and kept.
  hindsight: { dot: "bg-amber-500", label: "Hindsight" },
  reflection: { dot: "bg-indigo-500", label: "Reflection" },
} as const;

type Kind = keyof typeof KIND_STYLE;

interface Thought {
  id: string;
  kind: Kind;
  text: string;
  correlationId: string;
  /** The archive row behind a "Learned" line, when the turn carried one.
   * Only these can be corrected — a reflection is a thought about the
   * conversation and has no row to edit. */
  factId?: string;
}

/** Newest first: what Archivist wrote ("Learned") and what Reflection
 * noticed and what Hindsight went back over, across the whole session.
 *
 * What Recall read is deliberately absent. Recall fires on every turn and
 * returns its depth whether or not the turn needed anything, so the reads
 * were most of the panel and most of them were beside the point -- the
 * thoughts a person wants to see are the ones the system arrived at, not
 * the rows it happened to touch getting there. The reads are still whole in
 * the event drawer, which is where you go when you want to know why a turn
 * answered the way it did. */
function thoughtsOf(records: TurnRecord[]): Thought[] {
  const out: Thought[] = [];
  for (const r of [...records].reverse()) {
    r.writes.forEach((t, i) =>
      out.push({
        id: `${r.correlationId}-learned-${i}`,
        kind: "learned",
        text: t,
        correlationId: r.correlationId,
        factId: r.writeIds?.[i],
      }),
    );
    r.hindsight.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-hindsight-${i}`, kind: "hindsight", text: t, correlationId: r.correlationId }),
    );
    if (r.idea) {
      out.push({ id: `${r.correlationId}-reflection-idea`, kind: "reflection", text: r.idea, correlationId: r.correlationId });
    }
    r.passages.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-reflection-${i}`, kind: "reflection", text: t, correlationId: r.correlationId }),
    );
  }
  return out;
}

/** How many ideas Reflection has pushed — the count the collapsed toggle's
 * red badge shows, so the panel doesn't have to be open to notice one. */
export function reflectionCount(records: TurnRecord[]): number {
  return records.filter((r) => r.idea).length;
}

export function ThoughtsPanel({
  records,
  onClose,
  onOpen,
}: {
  records: TurnRecord[];
  onClose: () => void;
  onOpen: (correlationId: string) => void;
}) {
  const thoughts = thoughtsOf(records);

  // The turn log is history and does not change when the archive does, so a
  // correction is held here as an override on top of it. Keyed by fact id:
  // the same row can be announced by only one turn, but the panel is rebuilt
  // from scratch on every record that arrives.
  const [revised, setRevised] = useState<Map<string, string>>(new Map());
  const [removed, setRemoved] = useState<Set<string>>(new Set());
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [failed, setFailed] = useState<string | null>(null);
  const input = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    if (editing) {
      input.current?.focus();
      input.current?.select();
    }
  }, [editing]);

  function beginEdit(t: Thought) {
    if (!t.factId) return;
    setDraft(revised.get(t.factId) ?? t.text);
    setEditing(t.factId);
  }

  async function commit(factId: string) {
    const text = draft.trim();
    setEditing(null);
    if (!text) return;

    // Optimistic: the row is what the person says it is, and a failed write
    // says so rather than silently reverting under them.
    setRevised((current) => new Map(current).set(factId, text));
    try {
      await reviseFact(factId, text);
      setFailed(null);
    } catch {
      setFailed(factId);
    }
  }

  async function remove(factId: string) {
    setRemoved((current) => new Set(current).add(factId));
    try {
      await deleteFact(factId);
      setFailed(null);
    } catch {
      setFailed(factId);
      setRemoved((current) => {
        const next = new Set(current);
        next.delete(factId);
        return next;
      });
    }
  }

  // Overrides only — anything not yet clicked falls back to its kind's
  // default. Learned is practically the utterance restated, one per turn,
  // and arriving open turned the panel into a wall of near-duplicate prose;
  // Hindsight and Reflection are rarer and worth seeing at a glance.
  const [overrides, setOverrides] = useState<Map<string, boolean>>(new Map());

  function defaultOpen(kind: Kind): boolean {
    return kind !== "learned";
  }

  function toggle(id: string, kind: Kind) {
    setOverrides((current) => {
      const next = new Map(current);
      const open = next.has(id) ? next.get(id)! : defaultOpen(kind);
      next.set(id, !open);
      return next;
    });
  }

  return (
    <ResizableAside side="left" title="Thoughts" onClose={onClose}>
      <ul className="flex flex-col gap-1.5 p-2">
        {thoughts.length === 0 && (
          <li className="px-2 py-4 text-xs text-neutral-400 dark:text-neutral-500">
            Nothing learned or reflected on yet.
          </li>
        )}
        {thoughts
          .filter((t) => !(t.factId && removed.has(t.factId)))
          .map((t) => {
          const style = KIND_STYLE[t.kind];
          const text = (t.factId && revised.get(t.factId)) || t.text;
          const isOpen = overrides.has(t.id) ? overrides.get(t.id)! : defaultOpen(t.kind);
          return (
            <li key={t.id} className="group rounded-2xl border border-neutral-200 bg-white shadow-sm dark:border-neutral-700 dark:bg-neutral-900">
              {editing && t.factId === editing ? (
                <div className="flex items-start gap-2 px-3 py-1.5 text-sm">
                  <span className={`mt-1 h-2.5 w-2.5 shrink-0 rounded-full ${style.dot}`} />
                  <textarea
                    ref={input}
                    value={draft}
                    rows={2}
                    onChange={(e) => setDraft(e.target.value)}
                    onBlur={() => commit(t.factId!)}
                    onKeyDown={(e) => {
                      if (e.key === "Escape") {
                        e.preventDefault();
                        setEditing(null);
                      } else if (e.key === "Enter" && !e.shiftKey) {
                        e.preventDefault();
                        e.currentTarget.blur();
                      }
                    }}
                    className="min-w-0 flex-1 resize-y rounded-lg border border-neutral-300 bg-transparent px-2 py-1 text-neutral-700 outline-none focus:border-emerald-500 dark:border-neutral-600 dark:text-neutral-100"
                  />
                </div>
              ) : (
                <div className="flex items-start">
                  <button
                    type="button"
                    onClick={() => toggle(t.id, t.kind)}
                    onDoubleClick={() => beginEdit(t)}
                    className="flex min-w-0 flex-1 items-start gap-2 px-3 py-1.5 text-left text-sm"
                  >
                    <span className={`mt-1 h-2.5 w-2.5 shrink-0 rounded-full ${style.dot}`} />
                    <span className="shrink-0 font-medium text-neutral-500 dark:text-neutral-400">{style.label}:</span>
                    <span className={`min-w-0 flex-1 text-neutral-700 dark:text-neutral-200 ${isOpen ? "whitespace-pre-wrap break-words" : "truncate"}`}>
                      {text}
                    </span>
                  </button>
                  {/* Only a row with an archive id behind it: a turn logged
                      before ids were carried, and every non-Learned thought,
                      can be read but not corrected. */}
                  {t.factId && (
                    <div className="flex shrink-0 items-center gap-1 py-1.5 pr-2 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
                      <button
                        type="button"
                        title="Edit (or double-click the line)"
                        aria-label="Edit"
                        onClick={() => beginEdit(t)}
                        className="rounded px-1 text-xs text-neutral-400 hover:text-neutral-700 dark:hover:text-neutral-200"
                      >
                        ✎
                      </button>
                      <button
                        type="button"
                        title="Delete this fact"
                        aria-label="Delete"
                        onClick={() => remove(t.factId!)}
                        className="rounded px-1 text-xs text-neutral-400 hover:text-rose-600"
                      >
                        ✕
                      </button>
                    </div>
                  )}
                </div>
              )}
              {t.factId && failed === t.factId && (
                <p className="px-3 pb-1.5 text-xs text-rose-500">Could not reach the archive — the change is not saved.</p>
              )}
              {isOpen && (
                <div className="flex justify-end px-3 pb-1.5">
                  <button
                    type="button"
                    onClick={() => onOpen(t.correlationId)}
                    className="text-xs text-neutral-400 underline decoration-dotted hover:text-neutral-700 dark:hover:text-neutral-200"
                  >
                    open turn
                  </button>
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </ResizableAside>
  );
}
