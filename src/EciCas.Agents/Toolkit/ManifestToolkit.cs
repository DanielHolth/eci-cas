using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Executes one <see cref="ToolkitManifest"/>'s verb. Only ever constructed
/// for a manifest that already passed <see cref="ManifestToolkitLoader"/>'s
/// validation and carries <c>Approved: true</c> -- there is no path from
/// "file on disk" to "callable" that skips the human flipping that flag.
/// </summary>
public sealed class ManifestToolkit(ToolkitManifest manifest, IHttpClientFactory httpClientFactory) : IToolkit
{
    public string Name => manifest.Name;

    public Task<ToolkitOutcome> ExecuteAsync(string command, CancellationToken cancellationToken) =>
        manifest.Verb.Kind switch
        {
            "http_call" => HttpCallAsync(command, cancellationToken),
            "speak_text" => SpeakAsync(command, cancellationToken),
            _ => Task.FromResult(new ToolkitOutcome(string.Empty, false, $"Manifest '{manifest.Name}' names an unknown verb '{manifest.Verb.Kind}'.")),
        };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task<ToolkitOutcome> SpeakAsyncWindows(string text, CancellationToken cancellationToken) =>
        SpeechOutput.SpeakAsync(text, cancellationToken);

    private static Task<ToolkitOutcome> SpeakAsync(string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Task.FromResult(new ToolkitOutcome(string.Empty, false, "Nothing to read aloud."));
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new ToolkitOutcome(string.Empty, false, "Text-to-speech isn't available on this machine."));
        }

        return SpeakAsyncWindows(command, cancellationToken);
    }

    private async Task<ToolkitOutcome> HttpCallAsync(string command, CancellationToken cancellationToken)
    {
        var verb = manifest.Verb;
        if (string.IsNullOrWhiteSpace(verb.Url))
        {
            return new ToolkitOutcome(string.Empty, false, $"Manifest '{manifest.Name}' has no URL to call.");
        }

        HttpMethod method;
        if (verb.Method is null || string.Equals(verb.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            method = HttpMethod.Post;
        }
        else if (string.Equals(verb.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            method = HttpMethod.Get;
        }
        else
        {
            return new ToolkitOutcome(string.Empty, false, $"Manifest '{manifest.Name}' names an unsupported HTTP method '{verb.Method}'.");
        }

        try
        {
            using var request = new HttpRequestMessage(method, verb.Url);

            if (method != HttpMethod.Get && verb.BodyTemplate is not null)
            {
                var body = verb.BodyTemplate.Replace("{command}", JsonEncodedText.Encode(command).ToString());
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            if (!string.IsNullOrWhiteSpace(verb.BearerTokenEnvironmentVariable))
            {
                var token = Environment.GetEnvironmentVariable(verb.BearerTokenEnvironmentVariable);
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }

            var http = httpClientFactory.CreateClient();
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? new ToolkitOutcome(responseBody, true, null)
                : new ToolkitOutcome(string.Empty, false, $"'{manifest.Name}' replied {(int)response.StatusCode}: {responseBody}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            return new ToolkitOutcome(string.Empty, false, $"Couldn't reach '{manifest.Name}': {failure.Message}");
        }
    }
}
