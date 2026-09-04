using EciCas.Agents.Archivist;
using EciCas.Agents.Recall;
using EciCas.Agents.Identity;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EciCas.Tests.Agents;

public class IdentityAgentTests
{
    private static IdentityAgent CreateAgent(IMessageBus bus, BusActivityTracker activity, string? tempFile = null,
        IArchiveStore? archive = null) =>
        new(bus, activity, NullLogger<IdentityAgent>.Instance, new JsonlAgentStateStore(tempFile ?? Path.GetTempFileName()),
            ShippedInstructions.Store,
            new PersonaName(archive ?? new InMemoryArchiveStore(), Options.Create(new PersonaNameOptions())));

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
    public async Task Advisory_CarriesTheNameThisProfileGaveIt()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var archive = new InMemoryArchiveStore();
        await archive.WriteAsync([Named("Sol")], "daniel", CancellationToken.None);
        var agent = CreateAgent(bus, activity, archive: archive);

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(EciCas.Agents.Perception.PerceptionAgent.ProfileKey, "daniel"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Contains("Sol", advisory!.Meta.Get<string>(IdentityAgent.AdviceKey)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advisory_CarriesTheDefaultName_WhenNobodyHasNamedIt()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = CreateAgent(bus, activity);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Contains("Morrow", advisory!.Meta.Get<string>(IdentityAgent.AdviceKey)!, StringComparison.Ordinal);
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

    [Fact]
    public async Task Rename_IsVisibleOnTheNextTurn()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var archive = new InMemoryArchiveStore();
        var agent = CreateAgent(bus, activity, archive: archive);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral), CancellationToken.None);
        Assert.True(advisories.TryRead(out var before));
        Assert.Contains("Morrow", before!.Meta.Get<string>(IdentityAgent.AdviceKey)!, StringComparison.Ordinal);

        await archive.WriteAsync([Named("Sol")], null, CancellationToken.None);
        await agent.HandleAsync(Envelope.Create(Topics.SystemControl, "Archivist", Severity.Neutral,
            MetaBag.Empty.With(ArchivistAgent.ControlKindKey, ArchivistAgent.WrittenKind)), CancellationToken.None);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral), CancellationToken.None);
        Assert.True(advisories.TryRead(out var after));
        Assert.Contains("Sol", after!.Meta.Get<string>(IdentityAgent.AdviceKey)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-profile promise rests entirely on this: "assistant" is in
    /// Archive:SharedCategories, so a name filed there would be one name for
    /// every person on the device.
    /// </summary>
    [Fact]
    public void PersonaCategory_IsNotShared() =>
        Assert.DoesNotContain(PersonaName.Pair.Category, ParquetArchiveStore.DefaultSharedCategories, StringComparer.OrdinalIgnoreCase);

    private static ArchiveRecord Named(string name) => new(
        PersonaName.Pair.Category, PersonaName.Pair.Topic, "this", PersonaName.Subject, PersonaName.NameKey,
        name, DateTimeOffset.UtcNow, ArchiveDomain.Internal);
}
