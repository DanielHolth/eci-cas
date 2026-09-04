"use client";

import { useEffect, useState } from "react";
import { API_BASE } from "@/lib/api";
import type { TurnRecord } from "@/types/events";

/**
 * The host's turn log, replayed and then followed. Two requests, one shape:
 * GET /api/log for what happened before this window opened, then
 * /api/log/stream for what happens next. A record arrives many times as its
 * event fills in, so an arrival replaces the one with the same
 * correlationId rather than appending.
 *
 * There is no reduction here on purpose. TurnProjection on the host is the
 * only thing that knows the meta-key table, which is what lets the disk sink
 * and this drawer show the same event without either one being the source.
 */
export function useTurnLog(profileId?: string): TurnRecord[] {
  const [records, setRecords] = useState<TurnRecord[]>([]);

  useEffect(() => {
    const query = profileId ? `?profileId=${encodeURIComponent(profileId)}` : "";
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

    fetch(`${API_BASE}/api/log${query}`, { signal: abort.signal })
      .then((response) => (response.ok ? response.json() : []))
      .then((replayed: TurnRecord[]) => setRecords(replayed))
      .catch(() => {
        // Host down or replay refused — the live stream is still worth
        // opening, and the connection indicator already says so.
      })
      .finally(() => {
        if (abort.signal.aborted) return;
        source = new EventSource(`${API_BASE}/api/log/stream${query}`);
        source.onmessage = (event) => merge(JSON.parse(event.data) as TurnRecord);
      });

    return () => {
      abort.abort();
      source?.close();
    };
  }, [profileId]);

  return records;
}
