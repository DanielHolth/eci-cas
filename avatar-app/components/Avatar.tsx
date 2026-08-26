import type { Expression } from "@/types/events";

/** Placeholder mapping — Impulse's six-word vocabulary rendered as color +
 * label rather than illustrated facial features. Swap for real art once
 * that direction is picked (see README "open questions"). */
const EXPRESSION_STYLE: Record<Expression, { bg: string; label: string }> = {
  angry: { bg: "bg-red-500", label: "Angry" },
  scared: { bg: "bg-purple-500", label: "Scared" },
  sad: { bg: "bg-blue-500", label: "Sad" },
  warm: { bg: "bg-amber-400", label: "Warm" },
  alert: { bg: "bg-orange-500", label: "Alert" },
  neutral: { bg: "bg-slate-400", label: "Neutral" },
};

export function Avatar({
  expression,
  reflex,
}: {
  expression: Expression;
  reflex: string;
}) {
  const style = EXPRESSION_STYLE[expression];
  return (
    <div className="flex flex-col items-center gap-3">
      <div
        className={`h-28 w-28 rounded-full ${style.bg} transition-colors duration-500 shadow-inner flex items-center justify-center text-white font-medium`}
        aria-label={`Avatar expression: ${style.label}`}
      >
        {style.label}
      </div>
      <p className="text-sm text-neutral-500 italic max-w-xs text-center">
        {reflex}
      </p>
    </div>
  );
}
