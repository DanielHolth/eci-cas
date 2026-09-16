namespace EciCas.Agents.Toolkit;

/// <summary>
/// Where <see cref="DiscordToolkit"/> posts and how it authenticates. The
/// token is never a literal here -- same convention as
/// <c>Substrates:Providers:*:ApiKeyEnvironmentVariable</c> -- because a bot
/// token checked into appsettings is a bot token checked into git.
/// </summary>
public sealed class DiscordOptions
{
    public bool Enabled { get; set; }

    /// <summary>Read at startup by <c>ToolkitRegistration</c> to authorize the named HttpClient. Never stored here.</summary>
    public string? TokenEnvironmentVariable { get; set; }

    /// <summary>The channel a bare "post to Discord" ask goes to, absent one named in the command.</summary>
    public string? DefaultChannelId { get; set; }
}
