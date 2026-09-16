using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Posts a message to a Discord channel through the bot REST API. The "app"
/// half of "install the Morrow app on Discord" is the bot registration and
/// server invite, done once in Discord's developer portal outside this
/// process -- what this toolkit needs from that is only the bot token
/// (<see cref="DiscordOptions.TokenEnvironmentVariable"/>), which authorizes
/// the same way a person invited that bot into their server would.
///
/// This is the send half only: a message posted here shows up in Discord as
/// something Morrow said, one-way. Listening for messages posted back --
/// the other half of a real two-way bot -- needs a persistent gateway
/// connection, which is a different shape of thing (a long-running listener
/// feeding Perception, not a request/response toolkit) and is not built yet.
/// </summary>
public sealed class DiscordToolkit(IHttpClientFactory httpClientFactory, IOptions<DiscordOptions> options) : IToolkit
{
    public string Name => "discord";

    public async Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return new ToolkitOutcome(string.Empty, false, "The Discord toolkit is turned off.");
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultChannelId))
        {
            return new ToolkitOutcome(string.Empty, false, "No Discord channel is configured.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolkitOutcome(string.Empty, false, "Nothing to post.");
        }

        try
        {
            var http = httpClientFactory.CreateClient("discord");
            using var response = await http.PostAsJsonAsync(
                $"channels/{settings.DefaultChannelId}/messages",
                new { content = command },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new ToolkitOutcome(string.Empty, false, $"Discord replied {(int)response.StatusCode}: {body}");
            }

            return new ToolkitOutcome("Posted to Discord.", true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            return new ToolkitOutcome(string.Empty, false, $"Couldn't reach Discord: {failure.Message}");
        }
    }
}
