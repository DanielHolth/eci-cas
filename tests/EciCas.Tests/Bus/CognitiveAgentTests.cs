using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Bus;

public class CognitiveAgentTests
{
    private sealed class StubSubstrate(Func<string, string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) =>
            respond(substrateClass, prompt);
    }

    private static IOptions<AgentSubstrateManifest> ManifestWith(string substrateClass, bool useSubstrate = true) =>
        Options.Create(new AgentSubstrateManifest { Agents = { ["Test"] = new AgentSubstrateEntry { Class = substrateClass, UseSubstrate = useSubstrate } } });

    private sealed class TestCognitiveAgent(IMessageBus bus, BusActivityTracker activity, ISubstrateProvider substrate, IOptions<AgentSubstrateManifest> agentSubstrates)
        : CognitiveAgent<string>(bus, activity, NullLogger.Instance, substrate, agentSubstrates)
    {
        public FallbackPosture FallbackPostureValue { get; set; } = FallbackPosture.Open;
        public string? Published { get; private set; }
        public string? Degraded { get; private set; }

        public override string Name => "Test";
        public override IReadOnlyCollection<string> Subscriptions => [];
        protected override FallbackPosture Fallback => FallbackPostureValue;
        protected override string BuildPrompt(Envelope envelope) => "prompt";
        protected override string ParseResult(SubstrateResult result) => result.Text;
        protected override string FallbackResult(Envelope envelope) => "fallback";
        protected override void Publish(Envelope envelope, string prompt, string result, SubstrateResult? diagnostics, string? degraded)
        {
            Published = result;
            Degraded = degraded;
        }
    }

    [Fact]
    public async Task WhenSubstrateSucceeds_PublishesParsedResult()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => Task.FromResult(new SubstrateResult("real answer", TimeSpan.Zero, 10, 0m)));
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low"));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.Equal("real answer", agent.Published);
    }

    [Fact]
    public async Task WhenSubstrateFails_AndPostureIsOpen_PublishesFallback()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => throw new InvalidOperationException("down"));
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low")) { FallbackPostureValue = FallbackPosture.Open };

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.Equal("fallback", agent.Published);
        Assert.NotNull(agent.Degraded);
    }

    [Fact]
    public async Task WhenSubstrateFails_AndPostureIsClosed_PublishesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => throw new InvalidOperationException("down"));
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low")) { FallbackPostureValue = FallbackPosture.Closed };

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.Null(agent.Published);
    }

    [Fact]
    public async Task WhenUseSubstrateIsFalse_PublishesFallbackWithoutCallingSubstrate()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var called = false;
        var substrate = new StubSubstrate((_, _) => { called = true; return Task.FromResult(new SubstrateResult("real answer", TimeSpan.Zero, 10, 0m)); });
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low", useSubstrate: false));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.False(called);
        Assert.Equal("fallback", agent.Published);
    }

    [Fact]
    public async Task WhenNoManifestEntryForAgentName_Throws()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => Task.FromResult(new SubstrateResult("real answer", TimeSpan.Zero, 10, 0m)));
        var agent = new TestCognitiveAgent(bus, activity, substrate, Options.Create(new AgentSubstrateManifest()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None));
    }

    [Fact]
    public async Task WhenSubstrateSucceeds_PublishesWhatTheCallCost()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var telemetry = bus.Subscribe(Topics.Telemetry);
        var substrate = new StubSubstrate((_, _) => Task.FromResult(new SubstrateResult("real answer", TimeSpan.FromMilliseconds(42), 10, 0.002m)));
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low"));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.True(telemetry.TryRead(out var trace));
        Assert.Equal("Test", trace.Meta.Get<string>(SubstrateTrace.AgentKey));
        Assert.Equal("fast-low", trace.Meta.Get<string>(SubstrateTrace.ClassKey));
        Assert.Equal(42d, trace.Meta.Get<double>(SubstrateTrace.LatencyKey));
        Assert.Equal(0.002m, trace.Meta.Get<decimal>(SubstrateTrace.CostKey));
    }

    [Fact]
    public async Task WhenSubstrateFails_PublishesTheCauseAndNoCost()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var telemetry = bus.Subscribe(Topics.Telemetry);
        var substrate = new StubSubstrate((_, _) => throw new HttpRequestException("down"));
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low"));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.True(telemetry.TryRead(out var trace));
        Assert.Equal(SubstrateHealth.Unreachable, trace.Meta.Get<string>(SubstrateHealth.DegradedKey));
        Assert.False(trace.Meta.ContainsKey(SubstrateTrace.CostKey));
    }

    [Fact]
    public async Task WhenUseSubstrateIsFalse_PublishesNoTelemetry()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var telemetry = bus.Subscribe(Topics.Telemetry);
        var substrate = new StubSubstrate((_, _) => Task.FromResult(new SubstrateResult("real answer", TimeSpan.Zero, 10, 0m)));
        var agent = new TestCognitiveAgent(bus, activity, substrate, ManifestWith("fast-low", useSubstrate: false));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.False(telemetry.TryRead(out _));
    }
}
