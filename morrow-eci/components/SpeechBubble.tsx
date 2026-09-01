import type { IntentOutput } from "@/types/events";

/** Renders Intent's output specifically — never a raw agent finding. */
export function SpeechBubble({ output }: { output: IntentOutput }) {
  const isRefuse = output.kind === "refuse";
  return (
    <div
      className={`max-w-md rounded-2xl rounded-bl-sm border px-4 py-3 text-sm shadow-sm ${
        isRefuse
          ? "border-red-200 bg-red-50 text-red-900 dark:border-red-900 dark:bg-red-950 dark:text-red-100"
          : "border-neutral-200 bg-white text-neutral-800 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-100"
      }`}
    >
      {output.text}
    </div>
  );
}
