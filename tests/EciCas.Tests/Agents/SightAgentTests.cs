using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Sight;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

/// <summary>
/// The turn as the agent actually sees it, because the first live run did
/// not: the screenshot was taken, the file was written, and Governance still
/// named Sight impaired. Nothing in the unit tests then covered the path
/// from a captured picture to a published advisory.
/// </summary>
public class SightAgentTests
{
    private static SightAgent CreateAgent(IMessageBus bus, BusActivityTracker activity, bool eyes = true) =>
        new(bus, activity, NullLogger<SightAgent>.Instance, new MockSubstrateProvider(),
            ShippedInstructions.Store,
            Options.Create(new SightOptions()),
            Options.Create(new SubstrateOptions { Agents = { ["Sight"] = new SubstrateAgentEntry { UseSubstrate = eyes } } }));

    private static Envelope Perceived(string text) =>
        Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, text));

    [Fact]
    public async Task AGlimpseBecomesAnAdvisory()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var agent = CreateAgent(bus, activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        agent.Glimpse("shot.jpg", [1, 2, 3], "SPACE HAVEN");
        await agent.HandleAsync(Perceived("what am I looking at"), CancellationToken.None);

        var advisory = await advisories.ReadAsync(CancellationToken.None);
        Assert.Null(advisory.Meta.Get<string>(SubstrateHealth.DegradedKey));
        Assert.False(string.IsNullOrWhiteSpace(advisory.Meta.Get<string>(SightAgent.AdviceKey)));
        Assert.Equal("SPACE HAVEN", advisory.Meta.Get<string>(SightAgent.WordsKey));
        Assert.Equal("shot.jpg", advisory.Meta.Get<string>(SightAgent.ImageKey));
    }

    /// <summary>
    /// No eyes is not a fault: the tier said so. The OCR still reaches Intent,
    /// and an advisory marked degraded would have Governance apologising for
    /// a faculty the person never paid for.
    /// </summary>
    [Fact]
    public async Task BlindStillCarriesTheWords_AndIsNotDegraded()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var agent = CreateAgent(bus, activity, eyes: false);
        var advisories = bus.Subscribe(Topics.Advisories);

        agent.Glimpse("shot.jpg", [1, 2, 3], "SPACE HAVEN");
        await agent.HandleAsync(Perceived("what am I looking at"), CancellationToken.None);

        var advisory = await advisories.ReadAsync(CancellationToken.None);
        Assert.Null(advisory.Meta.Get<string>(SubstrateHealth.DegradedKey));
        Assert.Null(advisory.Meta.Get<string>(SightAgent.AdviceKey));
        Assert.Equal("SPACE HAVEN", advisory.Meta.Get<string>(SightAgent.WordsKey));
    }

    /// <summary>
    /// The conclusion is the other half of the last turn, not a turn of its
    /// own. Sight subscribes to it only to remember what the persona said --
    /// treating it as one would publish a second advisory per turn and have
    /// Governance waiting on a bundle that had already closed.
    /// </summary>
    [Fact]
    public async Task AConclusionIsNotATurn()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var agent = CreateAgent(bus, activity);
        var advisories = bus.Subscribe(Topics.Advisories);

        await agent.HandleAsync(
            Envelope.Create(Topics.Conclusion, "Governance", Severity.Neutral,
                MetaBag.Empty.With(IntentAgent.ReplyKey, "There is a programming festival coming up.")),
            CancellationToken.None);

        Assert.False(advisories.TryRead(out _));
    }
}
