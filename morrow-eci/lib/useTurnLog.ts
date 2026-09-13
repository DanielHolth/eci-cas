"use client";

import { useEffect, useState } from "react";
import { API_BASE } from "@/lib/api";
import type { TurnRecord } from "@/types/events";

export interface TurnLogState {
  records: TurnRecord[];
  /** The live stream is open. False before it opens and after it drops. */
  connected: boolean;
  /**
   * The replay has landed, so what is in `records` is the session so far
   * rather than an empty session. The distinction only matters to whoever is
   * speaking: a window opened five turns in must not read those five replies
   * aloud, and "nothing has happened yet" and "I have not asked yet" look
   * identical without this.
   */
  replayed: boolean;
}

/**
 * The host's turn log, replayed and then followed. Two requests, one shape:
 * GET /api/log for what happened before this window opened, then
 * /api/log/stream for what happens next. A record arrives many times as its
 * event fills in, so an arrival replaces the one with the same
 * correlationId rather than appending.
 *
 * There is no reduction here on purpose. TurnProjection on the host is the
 * only thing that knows the meta-key table, which is what lets the disk sink,
 * this drawer and the transcript show the same event without any of them
 * being the source.
 */
export function useTurnLog(): TurnLogState {
  const [records, setRecords] = useState<TurnRecord[]>([]);
  const [connected, setConnected] = useState(false);
  const [replayed, setReplayed] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    let source: EventSource | undefined;

    function merge(incoming: TurnRecord) {
      setRecords((current) => {
        const next = current.filter((r) => r.correlationId !== incoming.correlationId);
        next.push(incoming);
        next.sort((a, b) => a.seq - b.seq);
        return next;
      });
    }

    fetch(`${API_BASE}/api/log`, { signal: abort.signal })
      .then((response) => (response.ok ? response.json() : []))
      .then((replay: TurnRecord[]) => setRecords(replay))
      .catch(() => {
        // Host down or replay refused — the live stream is still worth
        // opening, and the connection indicator already says so.
      })
      .finally(() => {
        if (abort.signal.aborted) return;

        // Set with the records, in the same commit, so nobody ever sees a
        // caught-up log that still claims it has not caught up.
        setReplayed(true);
        source = new EventSource(`${API_BASE}/api/log/stream`);
        source.onopen = () => setConnected(true);
        source.onerror = () => setConnected(false);
        source.onmessage = (event) => merge(JSON.parse(event.data) as TurnRecord);
      });

    return () => {
      abort.abort();
      source?.close();
    };
  }, []);

  return { records, connected, replayed };
}
