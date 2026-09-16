"use client";

/**
 * The two-way seam between a surface and the desktop shell that hosts it.
 *
 * WebView2 puts `window.chrome.webview` into the page when, and only when,
 * the page is running inside the shell. Everything here is therefore
 * feature-detected and a no-op in a plain browser tab, which is what keeps
 * /overlay/ openable in a browser for development.
 *
 * Deliberately the only channel. The shell could reach into the page with
 * injected script and the page could reach the shell over HTTP, and both
 * would make the window a second API surface. A handful of named messages is
 * the whole contract, and the shell ignores anything it does not know.
 */

/** Shell to page. */
export interface ShellState {
  /** The voice hotkey is held down right now. */
  listening?: boolean;
  /** Clicks reach the page rather than passing through to the desktop. */
  interactable?: boolean;
  /**
   * What the shell's transcriber made of the last thing said into the
   * microphone, or a sentence about why it made nothing of it. Shown and then
   * dropped: this is not conversation, it is the person checking that they
   * were heard correctly.
   */
  heard?: string;
  /** Bumped on every `heard`, so the same sentence twice running still reads
   * as a new one. */
  heardAt?: number;
}

/**
 * Page to shell. `session` asks for the full window to be brought up; `drag`
 * hands the rest of a gesture already in progress to the OS move loop, which
 * is the only way a window whose whole surface is a browser control can be
 * dragged by its content.
 *
 * `resize` is the page telling the shell how tall and wide it has become. Only
 * the page can know: the reply is however long the reply is, and CSS is what
 * wraps it. The shell keeps her bottom edge where it is and grows upward, and
 * keeps her horizontal center where it is and grows outward -- a bubble
 * centered in a window narrower than it is otherwise gets clipped by the
 * window's own edges on both sides, not just the far one.
 */
export type PageMessage =
  | { type: "session" }
  | { type: "drag" }
  | { type: "resize"; height: number; width: number };

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}

function webview(): WebView | undefined {
  return typeof window === "undefined" ? undefined : window.chrome?.webview;
}

/** True inside the desktop shell. Call from an effect — it reads `window`. */
export function inShell(): boolean {
  return webview() !== undefined;
}

/** Subscribe to shell state. Returns the unsubscribe, or a no-op in a browser. */
export function onShellState(handler: (state: ShellState) => void): () => void {
  const view = webview();
  if (!view) return () => {};

  const listener = (event: MessageEvent) => {
    // Whatever arrives is whatever the shell sent; a shape that is not a
    // state object is dropped rather than thrown, because a page that dies
    // on an unknown message is a page that cannot be updated independently.
    if (event.data && typeof event.data === "object") handler(event.data as ShellState);
  };
  view.addEventListener("message", listener);
  return () => view.removeEventListener("message", listener);
}

/** Tell the shell something. False when there is no shell listening. */
export function postToShell(message: PageMessage): boolean {
  const view = webview();
  view?.postMessage(message);
  return view !== undefined;
}
