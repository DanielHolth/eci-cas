import type { IntentOutput } from "@/types/events";

/** Renders Intent's output specifically — never a raw agent finding. */
export function SpeechBubble({ output }: { output: IntentOutput }) {
  const isRefuse = output.kind === "refuse";
  return (
    <div
      className={`max-w-md rounded-2xl rounded-bl-sm border px-4 py-3 text-sm shadow-sm ${
        isRefuse
          ? "border-red-200 bg-red-50 text-red-900"
          : "border-neutral-200 bg-white text-neutral-800"
      }`}
    >
      {output.text}
    </div>
  );
}
