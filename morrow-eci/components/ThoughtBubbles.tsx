import type { BundleAgent, BundleFinding } from "@/types/events";

/** Placeholder colors — no console/log surface exists yet in the C# rebuild
 * to anchor these to (the old tools/console.py COLORS table was Python-era
 * and is archived). Pick real ones once a console subscriber has its own
 * palette (see README "open questions"). */
const AGENT_STYLE: Record<BundleAgent, { dot: string; label: string }> = {
  librarian: { dot: "bg-blue-500", label: "Librarian" },
  recall: { dot: "bg-orange-500", label: "Recall" },
  identity: { dot: "bg-fuchsia-500", label: "Identity" },
};

export function ThoughtBubbles({
  findings,
  faded = false,
}: {
  findings: BundleFinding[];
  /** Bubbles persist faded after speaking, per §6's "faded, not gone". */
  faded?: boolean;
}) {
  return (
    <div
      className={`flex flex-col gap-2 transition-opacity duration-500 ${faded ? "opacity-40" : "opacity-100"}`}
    >
      {findings.map((f) => {
        const style = AGENT_STYLE[f.agent];
        return (
          <div
            key={f.agent}
            className="flex w-full items-center gap-2 rounded-full border border-neutral-200 bg-white px-3 py-1.5 text-sm shadow-sm text-left dark:border-neutral-700 dark:bg-neutral-900"
          >
            <span className={`h-2.5 w-2.5 rounded-full ${style.dot}`} />
            <span className="font-medium text-neutral-500 dark:text-neutral-400">{style.label}:</span>
            <span className="text-neutral-700 dark:text-neutral-200">{f.text}</span>
          </div>
        );
      })}
    </div>
  );
}
