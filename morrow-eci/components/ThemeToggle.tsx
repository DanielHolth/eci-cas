"use client";

import { useLayoutEffect } from "react";

type Theme = "light" | "dark";

function apply(theme: Theme) {
  document.documentElement.dataset.theme = theme;
  try {
    localStorage.setItem("theme", theme);
  } catch {
    // Private mode or a blocked store — the theme still applies for this
    // session, it just won't be remembered. Not worth surfacing.
  }
}

/**
 * One button, two states. Which glyph shows is decided by CSS off the same
 * `data-theme` attribute the rest of the app themes from, not by React state:
 * the server can't know the answer, and rendering it from state would either
 * flash the wrong icon or force a hydration mismatch here.
 */
export function ThemeToggle() {
  useLayoutEffect(() => {
    // React doesn't own attributes on <html> that aren't in its JSX, so
    // Strict Mode's development remount can drop what the pre-paint script
    // set. Re-assert it from the same source of truth.
    let stored: string | null = null;
    try {
      stored = localStorage.getItem("theme");
    } catch {
      // ignore — fall through to the system preference
    }
    const theme: Theme =
      stored === "light" || stored === "dark"
        ? stored
        : window.matchMedia("(prefers-color-scheme: dark)").matches
          ? "dark"
          : "light";
    document.documentElement.dataset.theme = theme;
  }, []);

  return (
    <button
      type="button"
      title="Switch between light and dark"
      aria-label="Switch between light and dark"
      onClick={() => apply(document.documentElement.dataset.theme === "dark" ? "light" : "dark")}
      className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-white text-sm ring-1 ring-neutral-200 transition hover:ring-neutral-400 dark:bg-neutral-900 dark:ring-neutral-700 dark:hover:ring-neutral-500"
    >
      <span aria-hidden className="dark:hidden">
        ☾
      </span>
      <span aria-hidden className="hidden dark:inline">
        ☀
      </span>
    </button>
  );
}
