"""
Interactive console for ECI-CAS Phase 0 — bootstraps the ecosystem, then
lets you type prompts and watch every hop of the pipeline, in the order
it actually happened.

Design note: this does NOT print live from inside a bus subscriber.
The embedded bus (§3) dispatches synchronously and recursively — each
subscriber runs to full completion, including everything IT triggers
downstream, before the bus moves on to the next subscriber of the
original topic. A subscriber added after the real agents (as any
observer necessarily is, since it's attached post-bootstrap) would
therefore only get to print each hop *after* that hop's entire
downstream cascade had already printed — technically accurate data,
but a badly out-of-causal-order display.

Instead: the bus already records every publish, in true causal order,
into an in-memory trace (bus.trace()) BEFORE dispatching to handlers.
So we just ingest, then print the slice of the trace that appeared
since the last prompt — same "void observer" idea (never publishes,
only reads), just reading the bus's own ledger instead of racing its
dispatch order.

For after-the-fact inspection across sessions instead of live, the
Archive JSONL files give you that too (§13.2):
    cat data/archive/queue/events_*.jsonl | jq .

Usage:
    python -m tools.console --manifest manifests/ecosystem-manifest.yaml

Then type a prompt and press Enter. Type 'quit' or 'exit' to stop.
"""
from __future__ import annotations

import argparse
import sys

from bus.envelope import Envelope
from recovery.bootstrap import Recovery, BootstrapError

RESET = "\033[0m"
DIM = "\033[2m"
COLORS = {
    "Sensory": "\033[36m",     # cyan
    "Impulse": "\033[35m",     # magenta
    "Governance": "\033[33m",  # yellow
    "Analytics": "\033[34m",   # blue
    "Intent": "\033[32m",      # green
    "Security": "\033[31m",    # red
    "Action": "\033[37m",      # white
    "Recovery": DIM,
}


def _color(source: str) -> str:
    return COLORS.get(source, "")


def print_hop(envelope: Envelope) -> None:
    color = _color(envelope.source)
    arrow = f"{color}{envelope.source:<10}{RESET} -> {envelope.destination:<10}"
    tag = f"[{envelope.type}]"
    content = str(envelope.content)
    if len(content) > 100:
        content = content[:97] + "..."
    print(f"  {arrow} {tag:<14} {content}")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="ECI-CAS live queue console")
    parser.add_argument("--manifest", required=True)
    args = parser.parse_args(argv)

    try:
        eco = Recovery(args.manifest).bootstrap()
    except BootstrapError as e:
        print(f"BOOTSTRAP FAILED: {e}", file=sys.stderr)
        return 1

    print()
    print("=" * 70)
    print("Live queue console — every hop prints in the order it happened.")
    print("Type a prompt and press Enter. Type 'quit' or 'exit' to stop.")
    print("=" * 70)
    print()

    last_seen = len(eco.bus.trace())  # BootCheck already ran during bootstrap; skip it

    while True:
        try:
            line = input("> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            break

        if not line:
            continue
        if line.lower() in ("quit", "exit"):
            break

        print()
        eco.sensory.ingest(line, source_type="prompt")

        full_trace = eco.bus.trace()
        for envelope in full_trace[last_seen:]:
            print_hop(envelope)
        last_seen = len(full_trace)
        print()

    print("Session ended.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

