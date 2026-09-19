using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Toolkit;

/// <summary>Pulls the part of a web page that answers a question. No model reads it: the page is cut into chunks and the nearest ones (by embedding) are kept.</summary>
public interface IPageReader
{
    /// <summary>Null when the page could not be read or held nothing usable; never throws for an ordinary failure.</summary>
    Task<string?> ExtractAsync(string url, string question, CancellationToken cancellationToken);
}

public sealed partial class PageReader(
    IHttpClientFactory httpClientFactory,
    IEmbeddingProvider embeddings,
    IOptions<SearchOptions> options,
    ILogger<PageReader> logger) : IPageReader
{
    private const int MaxChunksEmbedded = 60;

    public async Task<string?> ExtractAsync(string url, string question, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ReaderGuard.IsAllowedUrl(uri))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.ReadTimeoutSeconds));

            var html = await FetchAsync(uri, settings.PageBytes, timeout.Token).ConfigureAwait(false);
            if (html is null)
            {
                return null;
            }

            var chunks = Chunk(HtmlToText(html), settings.ChunkChars);
            if (chunks.Count == 0)
            {
                return null;
            }

            var picked = await PickAsync(chunks, question, settings.ExtractChars, timeout.Token).ConfigureAwait(false);
            return string.Join(" ", picked);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Reading {Url} timed out", url);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            logger.LogInformation("Reading {Url} failed: {Reason}", url, ex.Message);
            return null;
        }
    }

    private async Task<string?> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("reader");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type is not ("text/html" or "application/xhtml+xml" or "text/plain"))
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes];
        var read = 0;
        while (read < maxBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, maxBytes - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet ?? "utf-8");
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(buffer, 0, read);
    }

    /// <summary>The chunks nearest the question, in page order, up to a character budget of whole chunks.</summary>
    private async Task<IReadOnlyList<string>> PickAsync(IReadOnlyList<string> chunks, string question, int budget, CancellationToken cancellationToken)
    {
        var candidates = chunks.Take(MaxChunksEmbedded).ToList();
        if (!embeddings.Available)
        {
            return TakeWithin(candidates.Select((c, i) => (c, i, 0.0)), budget, byPosition: true);
        }

        var asked = await embeddings.EmbedAsync([question], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
        var vectors = await embeddings.EmbedAsync(candidates, EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false);
        return TakeWithin(candidates.Select((c, i) => (c, i, (double)VectorMath.Cosine(asked[0], vectors[i]))), budget, byPosition: false);
    }

    private static IReadOnlyList<string> TakeWithin(IEnumerable<(string Text, int Index, double Score)> scored, int budget, bool byPosition)
    {
        var chosen = new List<(string Text, int Index)>();
        var used = 0;
        foreach (var (text, index, _) in byPosition ? scored : scored.OrderByDescending(s => s.Score))
        {
            if (used + text.Length > budget && chosen.Count > 0)
            {
                continue;
            }

            chosen.Add((text, index));
            used += text.Length;
        }

        return [.. chosen.OrderBy(c => c.Index).Select(c => c.Text)];
    }

    internal static IReadOnlyList<string> Chunk(string text, int size)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length >= 40)
            {
                chunks.Add(current.ToString().Trim());
            }

            current.Clear();
        }

        foreach (var paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var sentence in SentenceBreak().Split(paragraph))
            {
                if (current.Length > 0 && current.Length + sentence.Length + 1 > size)
                {
                    Flush();
                }

                current.Append(current.Length > 0 ? " " : string.Empty).Append(sentence);
            }
        }

        Flush();
        return chunks;
    }

    internal static string HtmlToText(string html)
    {
        var body = MainBlock().Match(html) is { Success: true } main ? main.Value : html;
        body = Noise().Replace(body, " ");
        body = BlockBreak().Replace(body, "\n");
        body = Tag().Replace(body, " ");
        body = WebUtility.HtmlDecode(body);
        return string.Join('\n', body.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0));
    }

    [GeneratedRegex(@"<(main|article)\b.*</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainBlock();

    [GeneratedRegex(@"<(script|style|noscript|svg|nav|footer|header|aside|form|template)\b.*?</\1>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|tr|h[1-6]|section|table|blockquote|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();
}
