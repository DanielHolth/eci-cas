namespace EciCas.Agents.Toolkit;

/// <summary>
/// A toolkit described in JSON instead of a C# class -- the format a Steam
/// Workshop for toolkits would eventually traffic in. Deliberately narrow:
/// a manifest can only compose the verbs below, never run arbitrary code, so
/// installing one can never mean more than "call this URL" or "say this
/// text" -- the same ceiling whether the manifest came from this developer
/// or, eventually, a stranger's upload. Anything that needs more than that
/// (PowerShell's own NL-to-script translation, a persistent connection, new
/// hardware access) stays a native <see cref="IToolkit"/> on purpose; this
/// format is not meant to reach that far.
/// </summary>
/// <param name="Name">Matches <see cref="IToolkit.Name"/>; also the registered toolkit's identity in routing and the guide's listing.</param>
/// <param name="Description">Shown verbatim by GuideToolkit, same contract as <see cref="ToolkitDescriptor.Description"/>.</param>
/// <param name="Triggers">Same role as <see cref="ToolkitDescriptor.Triggers"/> -- prose exemplars ToolkitManagerAgent embeds and routes against.</param>
/// <param name="Verb">The single action this manifest performs when routed to.</param>
/// <param name="Approved">
/// False until a human opens the file and sets this by hand. A manifest that
/// loads without it is parsed, validated, and left unregistered -- pending,
/// not broken -- the same gap "uploaded" leaves before "subscribed" in any
/// shared-manifest future. Never set this from code.
/// </param>
public sealed record ToolkitManifest(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    ManifestVerb Verb,
    bool Approved = false);

/// <summary>
/// One of a small, closed set of pre-approved actions. <see cref="Kind"/>
/// selects which fields below apply; unrecognized kinds fail validation
/// rather than silently no-opping, since a manifest with a typo'd verb
/// should never load approved-and-inert.
/// </summary>
/// <param name="Kind">"http_call" or "speak_text".</param>
/// <param name="Method">http_call only: "GET" or "POST".</param>
/// <param name="Url">http_call only: absolute URL, no template placeholders -- a manifest names exactly one endpoint, it cannot be redirected by user input.</param>
/// <param name="BodyTemplate">http_call + POST only: JSON body sent as-is, with the literal substring <c>{command}</c> replaced by the routed text, JSON-escaped.</param>
/// <param name="BearerTokenEnvironmentVariable">
/// http_call only, optional. Names an environment variable read at call
/// time and sent as an Authorization: Bearer header -- same convention as
/// <see cref="DiscordOptions.TokenEnvironmentVariable"/>. A manifest can
/// never carry a literal secret, only the name of where to find one.
/// </param>
public sealed record ManifestVerb(
    string Kind,
    string? Method = null,
    string? Url = null,
    string? BodyTemplate = null,
    string? BearerTokenEnvironmentVariable = null);
