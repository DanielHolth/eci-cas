using EciCas.Agents.Reasoning;
using EciCas.Agents.Recall;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class RecallAgentTests
{
    [Fact]
    public async Task PublishesAdvisory_WithStoreResults()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new JsonlArchiveStore(Path.GetTempFileName());
        await store.WriteAsync([new ArchiveRecord("dinner", "tacos last time", DateTimeOffset.UtcNow)], CancellationToken.None);

        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store,
            Options.Create(new RecallOptions()));

        var lookup = Envelope.Create(Topics.LookupPaths, "Reasoning", Severity.Neutral,
            MetaBag.Empty.With(ReasoningAgent.LookupPathsKey, new[] { "dinner" }));
        await agent.HandleAsync(lookup, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal("tacos last time", advisory!.Meta.Get<string>(RecallAgent.ResultsKey));
    }

    [Fact]
    public async Task WhenMorePathsProposedThanMaxPaths_OnlyQueriesTheFirstMaxPaths()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new JsonlArchiveStore(Path.GetTempFileName());
        await store.WriteAsync([
            new ArchiveRecord("dinner", "tacos last time", DateTimeOffset.UtcNow),
            new ArchiveRecord("weather", "rained yesterday", DateTimeOffset.UtcNow),
        ], CancellationToken.None);

        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store,
            Options.Create(new RecallOptions { MaxPaths = 1 }));

        var lookup = Envelope.Create(Topics.LookupPaths, "Reasoning", Severity.Neutral,
            MetaBag.Empty.With(ReasoningAgent.LookupPathsKey, new[] { "dinner", "weather" }));
        await agent.HandleAsync(lookup, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal("tacos last time", advisory!.Meta.Get<string>(RecallAgent.ResultsKey));
    }

    [Fact]
    public async Task PublishesAdvisory_WhenNoPathsProposed()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var store = new JsonlArchiveStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl"));

        var agent = new RecallAgent(bus, activity, NullLogger<RecallAgent>.Instance, store,
            Options.Create(new RecallOptions()));

        var lookup = Envelope.Create(Topics.LookupPaths, "Reasoning", Severity.Neutral,
            MetaBag.Empty.With(ReasoningAgent.LookupPathsKey, Array.Empty<string>()));
        await agent.HandleAsync(lookup, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Equal("nothing on file", advisory!.Meta.Get<string>(RecallAgent.ResultsKey));
    }
}
