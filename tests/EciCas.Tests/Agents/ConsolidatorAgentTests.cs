using EciCas.Agents.Consolidator;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class ConsolidatorAgentTests
{
    [Fact]
    public async Task FlushesAndAnnouncesWritten_AtBatchSize()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var control = bus.Subscribe(Topics.SystemControl);
        var path = Path.GetTempFileName();
        var store = new JsonlArchiveStore(path);

        var agent = new ConsolidatorAgent(bus, activity, NullLogger<ConsolidatorAgent>.Instance, store,
            Options.Create(new ConsolidatorOptions { BatchSize = 2 }));

        var first = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn one"));
        await agent.HandleAsync(first, CancellationToken.None);
        Assert.False(control.TryRead(out _));

        var second = Envelope.Create(Topics.Bundle, "Governance", Severity.Neutral,
            MetaBag.Empty.With(PerceptionAgent.TextKey, "turn two"));
        await agent.HandleAsync(second, CancellationToken.None);

        Assert.True(control.TryRead(out var written));
        Assert.Equal(ConsolidatorAgent.WrittenKind, written!.Meta.Get<string>(ConsolidatorAgent.ControlKindKey));

        var records = await store.LookupAsync(["turn"], maxPerPath: 10, CancellationToken.None);
        Assert.Equal(2, records.Count);
    }
}
