using EciCas.Agents.Hindsight;
using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The sweep is stateless, so a conversation that stays in one neighbourhood
/// gets the same best three notes on every turn — observed live over eighty
/// turns, until Intent started apologising for them. The damper is what makes
/// a second turn on one subject read differently from the first.
/// </summary>
public class HindsightDamperTests
{
    /// <summary>Descending cosine against <see cref="Query"/>, all of them well over the floor.</summary>
    private static float[] Near(float off) => [1f, off, 0f, 0f];

    private static readonly float[] Query = [1f, 0f, 0f, 0f];

    private static async Task<InMemoryPassageStore> CorpusAsync(params string[] ids)
    {
        var store = new InMemoryPassageStore();
        for (var i = 0; i < ids.Length; i++)
        {
            await store.WriteAsync(
                [new Passage(ids[i], $"note {ids[i]}", [], DateTimeOffset.UtcNow, Near(i * 0.2f))],
                null, CancellationToken.None);
        }

        return store;
    }

    private static HindsightAgent AgentOver(IPassageStore passages, IMessageBus bus, BusActivityTracker activity, int window) =>
        new(bus, activity, NullLogger<HindsightAgent>.Instance, new StubEmbeddings(_ => Query), passages,
            Options.Create(new PassageOptions { RepeatWindowTurns = window }), new RuntimeKnobs { RecallDepth = 3 });

    private static async Task<IReadOnlyList<string>> TurnAsync(HindsightAgent agent, System.Threading.Channels.ChannelReader<Envelope> advisories)
    {
        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "what do you think about that?")), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        return advisory!.Meta.Get<IReadOnlyList<string>>(HindsightAgent.NoteIdsKey) ?? [];
    }

    /// <summary>
    /// Depth 3 buys two notes. The second turn on the same subject must
    /// promote the third and fourth rather than repeat the first two — and
    /// must still hand Intent two, not one, or the damper has shortened the
    /// bundle instead of refreshing it.
    /// </summary>
    [Fact]
    public async Task ASecondTurnOnTheSameSubject_WakesTheNextNotesDown()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = AgentOver(await CorpusAsync("a", "b", "c", "d"), bus, activity, window: 3);

        Assert.Equal(["a", "b"], await TurnAsync(agent, advisories));
        Assert.Equal(["c", "d"], await TurnAsync(agent, advisories));
    }

    /// <summary>
    /// Morrow's objection, honoured: a note can be central for a stretch of
    /// turns, and with nothing else above the floor a hard exclusion would
    /// silence hindsight on the one turn its best note actually fits. Saying
    /// it again beats saying nothing — what the damper is for is saying it
    /// alongside three others that were also just said.
    /// </summary>
    [Fact]
    public async Task TheOnlyMatchingNote_ComesBackRatherThanLeavingTheSlotEmpty()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = AgentOver(await CorpusAsync("a"), bus, activity, window: 3);

        Assert.Equal(["a"], await TurnAsync(agent, advisories));
        Assert.Equal(["a"], await TurnAsync(agent, advisories));
    }

    /// <summary>
    /// Out past the window a note is eligible again: the point is to stop a
    /// note arriving three prompts running, not to spend it.
    /// </summary>
    [Fact]
    public async Task ANoteBecomesEligibleAgain_OnceItHasFallenOutOfTheWindow()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = AgentOver(await CorpusAsync("a", "b", "c", "d"), bus, activity, window: 1);

        Assert.Equal(["a", "b"], await TurnAsync(agent, advisories));
        Assert.Equal(["c", "d"], await TurnAsync(agent, advisories));
        Assert.Equal(["a", "b"], await TurnAsync(agent, advisories));
    }

    /// <summary>Zero is the deterministic sweep exactly as it was.</summary>
    [Fact]
    public async Task TheKnobOff_RepeatsTheSameNotesForever()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = AgentOver(await CorpusAsync("a", "b", "c", "d"), bus, activity, window: 0);

        Assert.Equal(["a", "b"], await TurnAsync(agent, advisories));
        Assert.Equal(["a", "b"], await TurnAsync(agent, advisories));
    }
}
