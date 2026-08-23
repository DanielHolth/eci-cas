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

Budget mode commands (Phase 0.2.1)
----------------------------------
Recognised HERE, before anything reaches Sensory, so they cost nothing
and never become events:

    switch to budget mode     stop calling the substrate; use fallbacks
    switch to live mode       resume real reasoning
    budget                    show mode, calls, tokens, estimated spend
    reset budget              zero the spend counters

They live in the console rather than in Sensory deliberately. §5.2 makes
Sensory "an input field plus source-tagging", and a mode switch is not
something the system PERCEIVES — it is control-plane state, like
Recovery's or Watchdog's. Putting a command parser in Sensory would also
hand one to every future modality (vision, audio, https) that has no use
for it. Diagnostic (§12) is the natural long-term owner: it is already
the human-facing meta layer that never injects into the live queue.

For the same reason, budget alerts print here rather than being prepended
to Action's speech. Action "executes exactly what Governance hands it"
(§5.7); having it author a status message of its own would break that the
same way a router writing dialogue would.

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


BUDGET_COMMANDS = {
    "switch to budget mode": "budget",
    "switch to live mode": "live",
    "budget mode": "budget",
    "live mode": "live",
}


def handle_command(line: str, eco) -> bool:
    """Handle a control-plane command. Returns True if `line` was one.

    Pre-queue by construction: nothing here publishes to the bus, so a
    command has no event_id, costs no tokens, and never reaches an agent."""
    command = " ".join(line.lower().split())
    budget = getattr(eco, "budget", None)
    if budget is None:
        return False

    if command in BUDGET_COMMANDS:
        print(f"  {budget.switch_manual(BUDGET_COMMANDS[command])}\n")
        return True

    if command in ("budget", "budget status", "status"):
        print("\n" + budget.summary() + "\n")
        return True

    if command in ("reset budget", "budget reset"):
        print(f"  {budget.reset_spend()}\n")
        return True

    return False


def show_alerts(eco) -> None:
    """Surface anything budget mode latched on since the last prompt."""
    budget = getattr(eco, "budget", None)
    if budget is None:
        return
    for alert in budget.drain_alerts():
        print(f"\n  {'!' * 3} {alert}\n")


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
    if getattr(eco, "budget", None) is not None and eco.budget.enabled:
        print("Budget commands: 'switch to budget mode' / 'switch to live mode'")
        print("                 'budget' for spend, 'reset budget' to zero it")
        print(f"Currently: {eco.budget.state.mode} mode, "
              f"${eco.budget.state.spend_usd:.4f} estimated spend")
    print("=" * 70)
    print()
    show_alerts(eco)

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

        if handle_command(line, eco):
            last_seen = len(eco.bus.trace())   # commands publish nothing
            continue

        print()
        eco.sensory.ingest(line, source_type="prompt")

        full_trace = eco.bus.trace()
        for envelope in full_trace[last_seen:]:
            print_hop(envelope)
        last_seen = len(full_trace)

        # Alerts AFTER the trace: the event already degraded gracefully,
        # so the explanation belongs with the result rather than ahead of it.
        show_alerts(eco)

        budget = getattr(eco, "budget", None)
        if budget is not None and budget.enabled and budget.state.calls:
            print(f"{DIM}  [{budget.state.mode}] {budget.state.calls} calls, "
                  f"${budget.state.spend_usd:.4f} est.{RESET}")
        print()

    print("Session ended.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

