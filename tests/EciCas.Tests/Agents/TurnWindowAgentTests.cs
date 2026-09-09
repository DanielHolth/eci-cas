using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Agents.TurnWindow;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

/// <summary>
/// The window is the only place continuity is kept as itself rather than
/// reconstructed, so what it refuses to show matters as much as what it
/// shows: the turn being answered, another profile's turns, and every idea
/// but the newest.
/// </summary>
public class TurnWindowAgentTests
{
    private static TurnWindowAgent Build()
    {
        var activity = new BusActivityTracker();
        return new TurnWindowAgent(new ChannelBus(activity), activity, NullLogger<TurnWindowAgent>.Instance);
    }

    private static async Task<Guid> Turn(TurnWindowAgent window, string given, string? replied,
        string? profileId = null, bool self = false)
    {
        var meta = MetaBag.Empty.With(PerceptionAgent.TextKey, given);
        if (profileId is not null)
        {
            meta = meta.With(PerceptionAgent.ProfileKey, profileId);
        }

        if (self)
        {
            meta = meta.With(ReflectionAgent.TriggeredByKey, "self");
        }

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral, meta);
        await window.HandleAsync(perception, CancellationToken.None);

        if (replied is not null)
        {
            await window.HandleAsync(
                perception.Derive(Topics.Conclusion, "Intent", Severity.Neutral,
                    MetaBag.Empty.With(IntentAgent.ReplyKey, replied)),
                CancellationToken.None);
        }

        return perception.CorrelationId;
    }

    [Fact]
    public async Task ConcludedTurnsComeBackOldestFirst()
    {
        var window = Build();
        await Turn(window, "one", "first");
        await Turn(window, "two", "second");

        var recent = window.Recent(5, Guid.NewGuid(), null);

        Assert.Equal([("one", "first"), ("two", "second")], recent);
    }

    /// <summary>
    /// An unanswered turn is the turn being answered right now. Showing a
    /// speaker their own live prompt as history is the one thing a window
    /// must never do, and the correlation id is a second guard behind the
    /// reply check in case the two arrive out of order.
    /// </summary>
    [Fact]
    public async Task TheTurnBeingAnsweredIsNotItsOwnHistory()
    {
        var window = Build();
        await Turn(window, "one", "first");
        var current = await Turn(window, "two", null);

        Assert.Equal([("one", "first")], window.Recent(5, current, null));
    }

    [Fact]
    public async Task AWindowOfZeroShowsNothing()
    {
        var window = Build();
        await Turn(window, "one", "first");

        Assert.Empty(window.Recent(0, Guid.NewGuid(), null));
    }

    [Fact]
    public async Task ProfilesDoNotReadEachOthersTurns()
    {
        var window = Build();
        await Turn(window, "mine", "yours", profileId: "a");
        await Turn(window, "theirs", "not yours", profileId: "b");

        Assert.Equal([("mine", "yours")], window.Recent(5, Guid.NewGuid(), "a"));
    }

    /// <summary>
    /// An idea is the persona talking to itself and belongs to nobody, so
    /// every profile sees it -- but only ever one, because two in a window
    /// read as a monologue the person is asked to join mid-thought.
    /// </summary>
    [Fact]
    public async Task OnlyTheNewestIdeaIsEverInTheWindow()
    {
        var window = Build();
        await Turn(window, "old idea", "old musing", self: true);
        await Turn(window, "hello", "hi", profileId: "a");
        await Turn(window, "new idea", "new musing", self: true);

        var recent = window.Recent(5, Guid.NewGuid(), "a");

        Assert.Equal([("hello", "hi"), ("new idea", "new musing")], recent);
    }

    /// <summary>
    /// And it is present even when the plain slice is too short to reach
    /// it: Intent always sees its last idea.
    /// </summary>
    [Fact]
    public async Task TheNewestIdeaIsBroughtForwardWhenTheSliceMissesIt()
    {
        var window = Build();
        await Turn(window, "an idea", "a musing", self: true);
        await Turn(window, "one", "first", profileId: "a");
        await Turn(window, "two", "second", profileId: "a");

        var recent = window.Recent(2, Guid.NewGuid(), "a");

        Assert.Equal([("an idea", "a musing"), ("one", "first"), ("two", "second")], recent);
    }

    [Fact]
    public void RenderNumbersTurnsForTheModelToAnswerByIndex() =>
        Assert.Equal("1. Given: a\n   Replied: b\n2. Given: c\n   Replied: d",
            TurnWindowAgent.Render([("a", "b"), ("c", "d")]));
}
