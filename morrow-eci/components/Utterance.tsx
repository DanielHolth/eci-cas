/** What the person said, echoed back opposite the persona's own bubble.
 * The avatar view keeps one turn on screen, so without this a reply hangs
 * in the air with nothing to have answered. */
export function Utterance({ text }: { text: string }) {
  return (
    <div className="flex w-full max-w-md justify-end">
      <div className="rounded-2xl rounded-br-sm bg-neutral-800 px-4 py-2 text-sm text-neutral-50 shadow-sm dark:bg-neutral-200 dark:text-neutral-900">
        {text}
      </div>
    </div>
  );
}
