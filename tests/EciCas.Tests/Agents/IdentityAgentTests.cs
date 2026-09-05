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
    private const string Nl = "\n";

    private static IdentityAgent CreateAgent(IMessageBus bus, BusActivityTracker activity, string? tempFile = null,
        IArchiveStore? archive = null, IInstructionStore? instructions = null) =>
        new(bus, activity, NullLogger<IdentityAgent>.Instance, new JsonlAgentStateStore(tempFile ?? Path.GetTempFileName()),
            instructions ?? ShippedInstructions.Store,
            new PersonaName(archive ?? new InMemoryArchiveStore(), Options.Create(new PersonaNameOptions())));

    /// <summary>
    /// The shipped instructions with identity.txt's name section replaced.
    ///
    /// Whether the persona is told its own name every turn is a prose
    /// decision, and it is currently off -- the reminder spent output on
    /// introducing itself instead of on the topic, and what it is called is
    /// in the archive for Recall to fetch on the turns that ask. The
    /// machinery that carries a name to Intent still has to work for the day
    /// that line comes back, so the tests for it supply their own body rather
    /// than reading whichever way the prose currently leans.
    /// </summary>
    private static IInstructionStore WithNameSection(string body)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        foreach (var file in Directory.GetFiles(ShippedInstructions.Directory))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        var path = Path.Combine(dir, "identity.txt");
        var text = File.ReadAllText(path).ReplaceLineEndings(Nl);
        var marker = text.IndexOf("## " + IdentityAgent.NameSection, StringComparison.Ordinal);
        Assert.True(marker >= 0, "identity.txt no longer has a name section");
        var next = text.IndexOf(Nl + "## ", marker + 1, StringComparison.Ordinal);

        File.WriteAllText(path,
            text[..marker] + "## " + IdentityAgent.NameSection + Nl + body + (next >= 0 ? text[next..] : string.Empty));
        return new FileInstructionStore(dir);
    }

    private static IInstructionStore TellsIntentTheName() => WithNameSection("You are called {name}." + Nl);

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
        var agent = CreateAgent(bus, activity, archive: archive, instructions: TellsIntentTheName());

        var perception = Envelope.Create(Topics.Perception, "Perception", Severity.Neutral,
            MetaBag.Empty.With(EciCas.Agents.Perception.PerceptionAgent.ProfileKey, "daniel"));
        await agent.HandleAsync(perception, CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        Assert.Contains("Sol", advisory!.Meta.Get<string>(IdentityAgent.AdviceKey)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the persona is reminded of its own name on every turn is a
    /// prose decision -- the reminder spends output on introducing itself
    /// instead of on the topic. Emptying the section has to be enough to stop
    /// it, and has to leave the advisory clean rather than ending in a space.
    /// </summary>
    [Fact]
    public async Task WhenTheNameSectionIsEmptied_TheAdvisoryDropsItEntirely()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = CreateAgent(bus, activity, instructions: WithNameSection(string.Empty));

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral), CancellationToken.None);

        Assert.True(advisories.TryRead(out var advisory));
        var advice = advisory!.Meta.Get<string>(IdentityAgent.AdviceKey)!;
        Assert.DoesNotContain("Morrow", advice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(advice.Trim(), advice);
        Assert.NotEmpty(advice);
    }

    [Fact]
    public async Task Advisory_CarriesTheDefaultName_WhenNobodyHasNamedIt()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var advisories = bus.Subscribe(Topics.Advisories);
        var agent = CreateAgent(bus, activity, instructions: TellsIntentTheName());

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
        var agent = CreateAgent(bus, activity, archive: archive, instructions: TellsIntentTheName());

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
