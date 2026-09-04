"use client";

/**
 * The persona had a thought of its own. Reflection pushes ideas back onto
 * perception, and until now they arrived indistinguishable from something a
 * person said. This draws one as what it is, and clicking opens its entry in
 * the history log — the same clickable-affordance pattern as
 * ConsolidationDoodle, applied to a different kind of interiority.
 */
export function IdeaBubble({ idea, onOpen }: { idea: string; onOpen: () => void }) {
  return (
    <button
      type="button"
      onClick={onOpen}
      className="max-w-md rounded-2xl rounded-bl-sm border border-dashed border-indigo-300 bg-indigo-50 px-4 py-2 text-left text-sm italic text-indigo-900 hover:bg-indigo-100 dark:border-indigo-800 dark:bg-indigo-950 dark:text-indigo-100 dark:hover:bg-indigo-900"
      aria-label="A thought of its own — click to see where it came from"
    >
      <span className="mr-1 not-italic text-indigo-400">◌</span>
      {idea}
    </button>
  );
}
