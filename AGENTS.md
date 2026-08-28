# Agent instructions

Write the least code/tests/docs that fully satisfies the requirement — no
speculative abstractions, no redundant test variants for the same claim,
no comments or docs restating what the code already says; when in doubt,
cut. Less is more.

## Subagents

Always spawn subagents with `model: "sonnet"`. Pass it explicitly on every
Agent call, regardless of which model the parent session is running.
(Exception: `subagent_type: "fork"` always inherits the parent model and
ignores the override.)
