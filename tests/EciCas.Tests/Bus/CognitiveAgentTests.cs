using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Bus;

public class CognitiveAgentTests
{
    private sealed class StubSubstrate(Func<string, string, Task<SubstrateResult>> respond) : ISubstrateProvider
    {
        public Task<SubstrateResult> CompleteAsync(string substrateClass, string prompt, CancellationToken cancellationToken) =>
            respond(substrateClass, prompt);
    }

    private sealed class TestCognitiveAgent(IMessageBus bus, BusActivityTracker activity, ISubstrateProvider substrate)
        : CognitiveAgent<string>(bus, activity, NullLogger.Instance, substrate)
    {
        public FallbackPosture FallbackPostureValue { get; set; } = FallbackPosture.Open;
        public string? Published { get; private set; }

        public override string Name => "Test";
        public override IReadOnlyCollection<string> Subscriptions => [];
        protected override string SubstrateClass => "fast-low";
        protected override FallbackPosture Fallback => FallbackPostureValue;
        protected override string BuildPrompt(Envelope envelope) => "prompt";
        protected override string ParseResult(SubstrateResult result) => result.Text;
        protected override string FallbackResult(Envelope envelope) => "fallback";
        protected override void Publish(Envelope envelope, string result, SubstrateResult? diagnostics) => Published = result;
    }

    [Fact]
    public async Task WhenSubstrateSucceeds_PublishesParsedResult()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => Task.FromResult(new SubstrateResult("real answer", TimeSpan.Zero, 10, 0m)));
        var agent = new TestCognitiveAgent(bus, activity, substrate);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.Equal("real answer", agent.Published);
    }

    [Fact]
    public async Task WhenSubstrateFails_AndPostureIsOpen_PublishesFallback()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => throw new InvalidOperationException("down"));
        var agent = new TestCognitiveAgent(bus, activity, substrate) { FallbackPostureValue = FallbackPosture.Open };

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.Equal("fallback", agent.Published);
    }

    [Fact]
    public async Task WhenSubstrateFails_AndPostureIsClosed_PublishesNothing()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var substrate = new StubSubstrate((_, _) => throw new InvalidOperationException("down"));
        var agent = new TestCognitiveAgent(bus, activity, substrate) { FallbackPostureValue = FallbackPosture.Closed };

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Test", Severity.Neutral), CancellationToken.None);

        Assert.Null(agent.Published);
    }
}
