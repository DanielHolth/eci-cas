using EciCas.Agents.Archivist;
using EciCas.Agents.Recall;
using EciCas.Agents.Identity;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

public class IdentityAgentTests
{
    private static IdentityAgent CreateAgent(IMessageBus bus, BusActivityTracker activity, string? tempFile = null) =>
        new(bus, activity, NullLogger<IdentityAgent>.Instance, new JsonlAgentStateStore(tempFile ?? Path.GetTempFileName()), ShippedInstructions.Store);

    [Fact]
    public async Task PublishesIdentityAdvisory()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = CreateAgent(bus, activity);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.False(string.IsNullOrEmpty(advisory!.Meta.Get<string>(IdentityAgent.AdviceKey)));
    }

    [Fact]
    public async Task InvalidatesCache_WhenStoreIsWritten()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var path = Path.GetTempFileName();
        var agent = CreateAgent(bus, activity, path);

        var first = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(first, CancellationToken.None);
        Assert.True(advisories.TryRead(out _));

        var written = Envelope.Create(Topics.SystemControl, "Archivist", Severity.Neutral,
            MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ArchivistAgent.WrittenKind));
        await agent.HandleAsync(written, CancellationToken.None);

        var second = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral);
        await agent.HandleAsync(second, CancellationToken.None);
        Assert.True(advisories.TryRead(out _));
    }
}
