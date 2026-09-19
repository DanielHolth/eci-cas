# Morrow User Manual

Bare-bones first draft. Morrow is a desktop companion: a small watermark on your desktop that you talk to, plus a conversation window with panels for settings and diagnostics.

## Hotkeys

| Key | What it does |
| --- | --- |
| **`-`** (hold) | Push-to-talk. Morrow listens while the key is down; let go when you are done. The key is watched, not claimed, so it still types a hyphen in games, chat boxes and terminals. Typing a hyphen therefore opens the microphone. |
| **`\|`** (Shift + backslash on most layouts) | Toggles whether clicks land on Morrow or pass through her to the desktop underneath. Pass-through is the default. The tray menu's **Clickable** box does the same. |

Both keys can be changed in `appsettings.json` under `Shell` (`VoiceKey`, `InteractKey`). Keys are named by the character they type, with optional `Ctrl+`, `Shift+`, `Alt+` or `Win+` prefixes. If you type a lot of hyphens, put a modifier in front of the voice key.

## Finding and talking to Morrow

- **The watermark.** Morrow sits as a small face in a corner of your desktop. Speech bubbles (what she heard, what she is thinking, her reply) appear above her and fade after a while.
- **The tray icon.** Double-click it, or right-click and choose **Open conversation**, to open the full conversation window.
- **Talking.** The default way is to hold `-` and speak. You can also type in the conversation window's input box and press Enter. The input box counts characters against the *Input length* knob, so long messages are not cut silently.
- **Screen capture.** Off by default. When turned on (see *Enable screen capture* below), Morrow takes one screenshot each time you start talking, so a question can be about what is on your screen.

## The conversation window

### Header

- **Panels** (dropdown, top left): opens one left-hand panel at a time. Choosing the empty option closes it. The options are Thoughts, References, PowerShell (only if enabled), Toolkit and Settings.
- **Title bar**: Morrow's name, connection state (Live / Disconnected), the current stage of the turn, and her impulse line.
- **Debug** (top right): opens the Debug panel on the right edge.

### Body

- **Face**: Morrow's avatar. Its expression follows her mood and speech.
- **Energy meter**: shows her level and how much energy she has left. While she has energy she answers with the paid API models. When it runs dry she falls back to the smaller local model, and says so in her own voice, so answers get shorter and duller until it refills.
- **Transcript**: the conversation so far.
- **Input box**: an alternative to the voice key; type here and press Enter.

## Panels

### Thoughts

What Morrow has learned and reflected on, newest first.

- **Learned**: facts she extracted from your conversation. Double-click a line (or use the pencil) to correct it, or use the bin to delete it. The original utterance is never changed.
- **Hindsight**: the second thoughts she had about a turn after the fact.
- **Reflection**: unprompted thoughts she wrote to her own notes. A red badge on the Panels dropdown counts reflections you have not looked at yet.

Learned lines are collapsed by default; Hindsight and Reflection are open.

### PowerShell (preview)

Appears only when *Enable PowerShell [Preview]* is ticked in Settings. Shows the PowerShell console output for commands Morrow runs on your machine.

### Toolkit

Lists the toolkits Morrow can use: the tools she can *do* things with beyond talking (PowerShell, Discord, web search, and any JSON toolkits you have approved). Click one to see its description, whether it is ready or disabled, and the result and turn of its last run. Ask Morrow "what can you do?" at any time for the same list in her own words.

### Settings

#### Profile & display

| Setting | What it does |
| --- | --- |
| **Profile** | Who Morrow thinks she is talking to (your name and avatar). Click to edit. |
| **Theme** | Light or dark. |
| **Voice** | Which voice Morrow speaks with. *Default voice* uses your system default. |
| **Bubble fade** | How long the heard, idea and reply bubbles stay on screen before fading (0.5 s steps). |
| **Dictation language** | The language the microphone listens for: English, French, Spanish or German. It is pinned rather than auto-detected, so a take does not flip language mid-sentence. Needs **Save** and a shell restart. |

#### Knobs

Changes take effect immediately. Nothing is stored until you press **Save**, which is grey while what you are running matches what is written in the settings file.

| Setting | What it does |
| --- | --- |
| **Save** | Writes the current knobs into the settings file so they survive a restart. |
| **Enable PowerShell [Preview]** | Lets Morrow run PowerShell commands on your machine. Off by default. |
| **Enable screen capture [disclaimer]** | Off by default. On, the shell takes one screenshot each time the voice key arms. On tiers with vision, the screenshot is sent to the vision model so Morrow can see what you see. Off means no capture happens at all. Needs **Save** and a shell restart. |
| **Tier** | Which preset is in force: **Mock** (test stubs), **Free** (local, no cost), **Budget**, **Pro**, **Premium**. It chooses the models, how wide recall fans out, and whether reflection runs. Switching re-seeds the sliders below. |
| **Reply length** | The sentence ceiling for Morrow's replies. She is told half of it as a floor too, so the range is what governs. |
| **Input length** | How many characters of your input reach Morrow. |
| **Context window** | How many earlier turns she sees when answering. Zero is a valid setting. |
| **Mood** | How she feels this turn. Mood is the state behind her tone, not the tone itself. |
| **Reflection every** | How many turns pass between reflections. Each one is an unprompted thought written to her notes. |
| **Recall depth** | How many remembered rows one recall lane may return. |

### Debug (right side)

The event log: everything the console prints about each turn, newest first. Each turn shows what she perceived, what she recalled, her hindsight, her intent (the reply) and the cost of the turn. It is for seeing what her faculties did, and is the first place to look when an answer seems off.

## Toolkits

- **Web search**: ask about current things (news, weather, prices, recent events) and Morrow searches the web. Morrow gets a few short snippets plus the most relevant passages of the best-matching page (read and ranked locally, no model involved). The References panel shows one card per turn listing just the links used. Available on Budget, Pro and Premium (PowerShell stays Pro and up). Morrow first answers from what she knows, then follows up on her own with what the search found (a moment later, as a separate turn).
- **Discord**: posts a message to Morrow's Discord channel.
- **PowerShell**: see above. Off by default.
- **JSON toolkits**: plain files in the `Toolkits` folder. A human has to mark one `Approved` before it runs, so dropping a file in never turns it on by itself.

## Privacy and consent

Two things are off until you turn them on: **PowerShell** and **screen capture**. Web search sends your question to a search engine. Nothing is sent to a model vendor unless the tier you chose uses one.

## Getting help

Ask Morrow "what can you do?" or "how do I use you?".
