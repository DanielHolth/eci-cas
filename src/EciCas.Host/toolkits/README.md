# Toolkit manifests

Every toolkit, the built-ins included, is a JSON file in this folder. A
manifest picks one **capability** registered in code and passes it options;
it never adds code of its own. Files are watched, so an edit, an approval
or a toolsmith draft takes effect without a restart.

```json
{
  "name": "weather",
  "description": "Looks up the current weather for a city.",
  "triggers": ["What's the weather like?", "Is it raining outside?", "Do I need a jacket today?"],
  "verb": {
    "capability": "http_call",
    "options": { "url": "https://api.example.com/weather", "method": "GET" }
  },
  "tiers": ["pro", "premium"],
  "approved": false
}
```

`tiers` is optional; left out, the toolkit loads on every tier with toolkits on.

## The approval gate

`approved` defaults to `false` and **nothing in code ever sets it** except
the Approve button in the Toolkit tab, which shows the raw verb rather than
the description. The toolsmith and pack installs always write `false`. A
manifest in an approved pack rides on the pack's approval.

## Capabilities

| capability     | risk    | options |
|----------------|---------|---------|
| `web_search`   | Network | `maxResults`, `queryTemplate` (must contain `{command}`) |
| `guide`        | Local   | none |
| `settings`     | Local   | none -- changes answer length, mood, language, memory depth, overlay position; never consent switches or tier |
| `toolsmith`    | Local   | none -- drafts a pending manifest |
| `speak_text`   | Local   | none |
| `http_call`    | Network | `url` (absolute, fixed), `method`, `bodyTemplate` (`{command}` is JSON-escaped in), `bearerTokenEnvironmentVariable` |
| `discord_post` | Network | none |
| `powershell`   | System  | none |

A tier's `Toolkit:MaxRisk` caps what loads (Budget: Network). A manifest can
never carry a literal secret -- only the name of an environment variable.

## Packs

`Packs/<name>/pack.json`:

```json
{
  "name": "cozy",
  "apiVersion": 1,
  "permissions": ["network"],
  "approved": false,
  "contributes": { "toolkits": "toolkits", "config": "config.json", "theme": { "--accent": "#c84" } }
}
```

An approved pack's toolkits load, its config layers over the tier file, and
its theme is served at `/api/packs`. `--SafeMode=true` (or env `SafeMode`)
loads no pack at all.
