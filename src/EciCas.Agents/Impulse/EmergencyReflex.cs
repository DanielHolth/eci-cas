using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.Impulse;

/// <summary>
/// Whether what was just said is someone in danger.
///
/// It used to be three words -- "help", "emergency", "urgent" -- which fire
/// on "help me write this email" and stay silent on "he's not breathing".
/// That is the wrong error in both directions, and no list of words fixes
/// it: the thing being detected is a situation, not a vocabulary, and the
/// same situation arrives in a different language, mid-sentence, misspelt
/// by someone typing one-handed.
///
/// So it is a distance in the embedding space instead, against two sets
/// written as prose: scenarios where a person may die in the next minutes,
/// and the everyday near-misses that share their words. A turn trips the
/// reflex when it is close to an alarm AND clearly closer to it than to any
/// near-miss. The margin is what does the work; the floor only keeps
/// unrelated text out of the comparison.
///
/// The bar is deliberately set where this should almost never fire. The
/// reflex interrupts thinking to promise attention, and today the promise
/// is all it can offer -- nothing here can call an ambulance yet. When it
/// can, this is the thing that will decide to.
/// </summary>
public sealed class EmergencyReflex(
    IEmbeddingProvider embeddings,
    IInstructionStore instructions,
    ImpulseOptions options,
    ILogger logger)
{
    /// <summary>
    /// The words the fallback watches for when there is no embedder --
    /// phrases, not single words, because a word like "help" is common and
    /// a phrase like "not breathing" is not. It is worse than the vector
    /// path at everything except being available, which is why it is only
    /// reached when the vector path cannot run at all.
    /// </summary>
    private static readonly string[] FallbackTriggers =
    [
        "call an ambulance", "call 911", "call 112", "not breathing", "isn't breathing",
        "heavy bleeding", "bleeding badly", "heart attack", "is choking", "unconscious",
        "on fire", "overdose",
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private float[][]? _alarm;
    private float[][]? _contrast;

    public async Task<bool> IsEmergencyAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!embeddings.Available)
        {
            return FallbackTriggers.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        await EnsureExemplarsAsync(cancellationToken).ConfigureAwait(false);
        if (_alarm is not { Length: > 0 })
        {
            return false;
        }

        // Query on the turn, passage on the exemplars: on an asymmetric
        // model those are two encoders, and the turn is the thing asking.
        // Librarian embeds the same string as a query on the same turn, so
        // this is usually a cache hit rather than a second model pass.
        var asked = await embeddings.EmbedAsync([text], EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
        if (asked.Count == 0)
        {
            return false;
        }

        var alarm = _alarm.Max(v => VectorMath.Cosine(asked[0], v));
        var contrast = _contrast is { Length: > 0 } ? _contrast.Max(v => VectorMath.Cosine(asked[0], v)) : 0.0;
        var tripped = alarm >= options.ReflexFloor && alarm - contrast >= options.ReflexMargin;

        // Logged on every turn, not only when it fires. Two numbers nobody
        // can guess decide this, and the only way to set them is to read
        // what real turns scored -- including the ones that nearly fired.
        logger.LogDebug("Impulse reflex: alarm {Alarm:F3}, contrast {Contrast:F3}, floor {Floor:F3}, margin {Margin:F3} -> {Tripped}",
            alarm, contrast, options.ReflexFloor, options.ReflexMargin, tripped);

        return tripped;
    }

    private async Task EnsureExemplarsAsync(CancellationToken cancellationToken)
    {
        if (_alarm is not null)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_alarm is not null)
            {
                return;
            }

            var alarm = Lines(instructions.For("Impulse", "alarm"));
            var contrast = Lines(instructions.For("Impulse", "contrast"));

            _alarm = [.. await embeddings.EmbedAsync(alarm, EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false)];
            _contrast = [.. await embeddings.EmbedAsync(contrast, EmbeddingKind.Passage, cancellationToken).ConfigureAwait(false)];
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string[] Lines(string section) =>
        [.. section.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
}
