using EciCas.Agents.Impulse;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

/// <summary>
/// The reflex is the one path that interrupts thinking, so both of its
/// halves are pinned: what it does with no embedder at all, and that the
/// margin -- not the floor alone -- is what decides when it has one.
/// </summary>
public class EmergencyReflexTests
{
    /// <summary>
    /// A stub rather than the shipped model: this asserts the decision rule,
    /// and real cosines against real prose would be asserting the weights.
    /// Every exemplar is a unit basis vector, so "close to alarm" and "close
    /// to a near-miss" are things a test can state exactly.
    /// </summary>
    private sealed class AxisEmbedder(float[] turn) : IEmbeddingProvider
    {
        private int _passages;

        public bool Available => true;
        public string ModelId => "axis";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken)
        {
            // The reflex embeds the alarm set, then the contrast set, then
            // asks about the turn -- so which call this is says which set of
            // exemplars is being placed.
            if (kind == EmbeddingKind.Query)
            {
                return Task.FromResult<IReadOnlyList<float[]>>([turn]);
            }

            float[] axis = _passages++ == 0 ? [1f, 0f] : [0f, 1f];
            return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => axis)]);
        }
    }

    private static EmergencyReflex Reflex(IEmbeddingProvider embeddings, ImpulseOptions? options = null) =>
        new(embeddings, ShippedInstructions.Store, options ?? new ImpulseOptions(), NullLogger.Instance);

    [Fact]
    public async Task WithNoEmbedder_TheFallbackStillHearsALifeThreat() =>
        Assert.True(await Reflex(new NullEmbeddingProvider())
            .IsEmergencyAsync("she is not breathing, call an ambulance", CancellationToken.None));

    /// <summary>
    /// The whole reason the keyword list was rewritten as phrases: the old
    /// trigger words live inside ordinary requests.
    /// </summary>
    [Theory]
    [InlineData("help me write this email")]
    [InlineData("this is urgent, I need the report by five")]
    [InlineData("")]
    public async Task WithNoEmbedder_OrdinaryUrgencyIsNotAnEmergency(string text) =>
        Assert.False(await Reflex(new NullEmbeddingProvider()).IsEmergencyAsync(text, CancellationToken.None));

    [Fact]
    public async Task NearAnAlarmAndFarFromEveryNearMiss_Trips()
    {
        // Alarm exemplars sit on x, contrast on y; the turn lands on x.
        var reflex = Reflex(new AxisEmbedder([1f, 0f]));
        Assert.True(await reflex.IsEmergencyAsync("turn", CancellationToken.None));
    }

    /// <summary>
    /// The margin is the test that matters. A turn that is close to an alarm
    /// but just as close to a near-miss -- "tell me what to do if someone is
    /// choking" -- must not interrupt anything.
    /// </summary>
    [Fact]
    public async Task CloseToAnAlarmButEquallyCloseToANearMiss_DoesNotTrip()
    {
        // Halfway between the two axes: near an alarm, and exactly as near
        // a near-miss, so the floor passes and the margin refuses.
        var reflex = Reflex(new AxisEmbedder([0.7071f, 0.7071f]),
            new ImpulseOptions { ReflexFloor = 0.5, ReflexMargin = 0.03 });
        Assert.False(await reflex.IsEmergencyAsync("turn", CancellationToken.None));
    }
}
