# Morrow-ECI

The companion surface for ECI-CAS. Next.js/React/TypeScript; the backend
lives one level up (`src/EciCas.*`). See [`docs/architecture.md`](../docs/architecture.md).

## Running it

```bash
npm install
npm run dev
```

Needs `EciCas.Host` on `http://localhost:5179` (`dotnet run --project
../src/EciCas.Host`). Bare like that the host runs the free mock tier and
every reply is an echo of its prompt — add `-- --Tier=Pro` plus vendor keys
for real ones.

`API_BASE` defaults to **same-origin**, because that is the shipped case: one
process serves this app and the API, and there is no CORS to get wrong on
someone else's machine. `.env.development` points `next dev` back at `:5179`
and is the only reason that file is checked in — a port, not a secret.
Override with `NEXT_PUBLIC_ECI_API_BASE`.

```bash
npm run build
```

Writes the static export to `out/` (`output: "export"`, `trailingSlash: true`
so `/overlay/` is a directory ASP.NET can serve a default document from). The
shell copies `out/` beside its exe at build time; nothing else consumes it.

## What it may do

A **void observer**: it reads `events.*` over SSE and never publishes to the
bus. The one sanctioned way in is `POST /api/perceive`, which is exactly
what typing at the console REPL does. Keep future features inside that.

## Two surfaces

`/` is the conversation: three columns, everything below. `/overlay/` is the
same session as a **watermark** — the avatar alone on a transparent page, for
the desktop shell to host in a click-through window. It reuses `Avatar`,
`useTurnLog`, `useVitals` and `useSpeech` and adds no state of its own beyond
a linger timer for the last reply.

The overlay is the surface that **speaks**; the window the shell opens for
`/` is loaded as `?mute=1` so one voice does not become two. A plain browser
tab has no query string and speaks, as before.

`lib/shell.ts` is the entire channel between page and shell, feature-detected
on `window.chrome.webview` so `/overlay/` still opens in an ordinary browser:
the shell posts `{listening, interactable}` in, the page posts `{type:
"session"}` (open the conversation) and `{type: "drag"}` (past 4px of pointer
travel — the OS move loop takes the rest of the gesture, so no click follows)
out.

## Layout

Three columns, the outer two collapsible and drag-resizable
(`ResizableAside`; width persists in `localStorage`).

| Column | Component | Source |
|---|---|---|
| left · Thoughts | `ThoughtsPanel` | what Recall read, Archivist wrote, Reflection noticed — a running list, newest first, with a badge for unseen ideas |
| centre | `Avatar`, `Transcript`, composer | the conversation |
| right · Debug | `KnobsPanel` + `EventLog` | live knobs, then one row per event |

## Event → UI

| Backend | UI |
|---|---|
| `perception.text` + Intent's reply | `Transcript` — scrolling log, person right, persona left |
| Impulse's expression | `Avatar` — a drawn face, one pose per expression, animation on top |
| Security's verdict | `SecurityIcon` — yellow/red only; click for the matched rule |
| Intent's advise/refuse | `SpeechBubble` — dashed amber when `governance.degraded` |
| `GET /api/persona` | the name under the avatar; re-read once a turn settles, since a rename is an ordinary archive write nothing pushes |
| `GET /api/log` + `/api/log/stream` | `EventLog` — collapsed rows expanding to fixed slots: Perception, Impulse, Librarian-n, Hindsight-n, Intent, Security (when not green), Archivist-n, Reflection, Cost, Latency |
| `GET/POST /api/knobs` | `KnobsPanel` — reply length, tone, reflection cadence, recall depth. Session experiments, not config: they reset on host restart |
| `GET/POST /api/profiles` | `ProfilePicker`, `ProfileChip` — the archive is scoped per person |

The drawer holds no reduction of its own: `TurnProjection` on the host folds
a turn's envelopes into one `TurnRecord` of plain strings, and the same
records feed the optional JSONL sink, so screen and disk cannot drift.
`useTurnLog` replays `/api/log` then follows `/api/log/stream`, replacing by
`correlationId`. Slots are named and filled, not appended — envelope arrival
order is not display order.

The **expression is chosen on the backend, never here.** Impulse appraises it
and Governance forwards it on the action, which is fresher on a block.
`readExpression` only validates the word against the six this app can draw.

## Known quirks

- The **Live/Disconnected** label reflects `EventSource.onopen`, which in some
  setups doesn't fire until the first message arrives — it can read
  Disconnected while connected and merely idle.
- Below `lg`, both drawers overlay the conversation rather than reflowing it.

## Open

- **Register.** `Avatar` is a hand-drawn SVG face — brow angle, eye openness,
  mouth path, plus CSS keyframes, no sprite sheet, no animation library. Two
  motion layers: an idle one (breathing, blinking, pupil drift) so the persona
  is alive between turns, and an expression one belonging to the current mood.
  Poses transition rather than cut, and all motion is off under
  `prefers-reduced-motion` — the mood stays legible from the static pose,
  which is why it lives in geometry. A seventh mood is a row in `FACE`. Still
  a guess, though: cartoon face, or something more abstract?
- **Thought colours** — Recall orange, Archivist emerald, Reflection indigo
  are inherited placeholders. The drawer prints the same agent lines the
  console does, so there is a palette to pin them to.
- **Dictation.** The shell's voice hotkey drives the overlay's listening
  state and nothing transcribes yet: there is no speech-to-text in the repo.
  The seam is named in `App.xaml.cs` — on key release, POST the transcript to
  `/api/perceive`, the same call this composer makes.
