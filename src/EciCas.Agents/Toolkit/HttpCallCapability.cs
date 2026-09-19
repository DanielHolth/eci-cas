using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// Calls one fixed endpoint. The URL is the manifest's, never the person's:
/// user input only ever lands JSON-escaped inside <see cref="Options.BodyTemplate"/>.
/// </summary>
public sealed class HttpCallCapability(IHttpClientFactory httpClientFactory) : ICapability
{
    /// <param name="Url">Absolute http(s) URL, no placeholders.</param>
    /// <param name="Method">"GET" or "POST" (default).</param>
    /// <param name="BodyTemplate">POST only: JSON body with <c>{command}</c> replaced by the routed text, JSON-escaped.</param>
    /// <param name="BearerTokenEnvironmentVariable">Names an environment variable sent as <c>Authorization: Bearer</c>. Never the secret itself.</param>
    public sealed record Options(string Url = "", string? Method = null, string? BodyTemplate = null, string? BearerTokenEnvironmentVariable = null);

    public string Name => "http_call";

    public string Description => "Calls one fixed web endpoint with the person's words in the body. Options: url, method (GET/POST), bodyTemplate containing {command}, bearerTokenEnvironmentVariable.";

    public CapabilityRisk Risk => CapabilityRisk.Network;

    public Type? OptionsType => typeof(Options);

    public string? Validate(object? options)
    {
        var o = (Options)options!;
        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "url must be an absolute http(s) URL";
        }

        if (o.Url.Contains('{'))
        {
            return "url cannot carry placeholders";
        }

        return o.Method is null or "GET" or "POST" ? null : "method must be GET or POST";
    }

    public async Task<ToolkitOutcome> ExecuteAsync(CapabilityCall call, CancellationToken cancellationToken)
    {
        var o = (Options)call.Options!;
        var method = o.Method == "GET" ? HttpMethod.Get : HttpMethod.Post;
        try
        {
            using var request = new HttpRequestMessage(method, o.Url);
            if (method == HttpMethod.Post && o.BodyTemplate is not null)
            {
                var body = o.BodyTemplate.Replace("{command}", JsonEncodedText.Encode(call.Command).ToString());
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            if (!string.IsNullOrWhiteSpace(o.BearerTokenEnvironmentVariable)
                && Environment.GetEnvironmentVariable(o.BearerTokenEnvironmentVariable) is { Length: > 0 } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new ToolkitOutcome(text, true, null)
                : new ToolkitOutcome(string.Empty, false, $"{uri(o)} replied {(int)response.StatusCode}: {text}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            return new ToolkitOutcome(string.Empty, false, $"Couldn't reach {uri(o)}: {failure.Message}");
        }

        static string uri(Options o) => new Uri(o.Url).Host;
    }
}
