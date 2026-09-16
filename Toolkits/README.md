# Toolkit manifests

Any `*.json` file dropped in this folder is read at startup as a toolkit
manifest -- no rebuild required. A manifest can only compose two verbs,
never run arbitrary code:

```json
{
  "name": "weather",
  "description": "Looks up the current weather for a city.",
  "triggers": [
    "What's the weather like?",
    "Is it raining outside?",
    "Do I need a jacket today?"
  ],
  "verb": {
    "kind": "http_call",
    "method": "GET",
    "url": "https://api.example.com/weather"
  },
  "approved": false
}
```

```json
{
  "name": "read-it-back",
  "description": "Speaks the given text aloud.",
  "triggers": ["Say that out loud", "Read this to me"],
  "verb": { "kind": "speak_text" },
  "approved": false
}
```

## The approval gate

`"approved"` defaults to `false` and **nothing in code ever sets it**. A
manifest that fails validation, or is valid but not yet approved, is logged
at startup and skipped -- it never becomes a callable toolkit. Turning one on
is a deliberate, human, one-line edit: open the file, change `false` to
`true`, restart. This is the same gap a Steam-Workshop-style "subscribe"
button would need to preserve later -- installing a manifest and it running
are not allowed to be the same action.

## `http_call` fields

- `method`: `"GET"` or `"POST"` (default `"POST"`).
- `url`: absolute, fixed at author time -- never built from what the user said, so a manifest cannot be redirected by input.
- `bodyTemplate` (POST only): a JSON string with the literal substring `{command}` replaced by whatever text routed here, JSON-escaped.
- `bearerTokenEnvironmentVariable` (optional): names an environment variable read at call time and sent as `Authorization: Bearer <value>`. A manifest can never carry a literal secret -- only the name of where to find one, same convention as `Discord:TokenEnvironmentVariable` in `appsettings.json`.

## `speak_text`

No fields. Speaks whatever text was routed here through the Windows speech
engine, same as the built-in accessibility toolkit.

## What this format is not for

Anything that needs more than "call one fixed URL" or "say this text" --
running code, a persistent connection, new hardware access, translating
natural language into something else first -- stays a native C# `IToolkit`.
The manifest format's whole safety property is that its ceiling is fixed and
small; growing it to cover more would grow what an approved-by-mistake or
eventually shared manifest can do.
