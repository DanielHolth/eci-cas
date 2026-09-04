using System.Reflection;
using EciCas.Agents.Action;
using EciCas.Agents.Archivist;
using EciCas.Agents.Governance;
using EciCas.Agents.Hindsight;
using EciCas.Agents.Identity;
using EciCas.Agents.Impulse;
using EciCas.Agents.Intent;
using EciCas.Agents.Librarian;
using EciCas.Agents.Passages;
using EciCas.Agents.Perception;
using EciCas.Agents.Recall;
using EciCas.Agents.Reflection;
using EciCas.Agents.Security;
using EciCas.Bus;
using EciCas.Core;
using EciCas.Substrates;
using EciCas.Tests.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Tests;

/// <summary>
/// Check-offs, not specifications. Every other test in this suite pins one
/// agent against envelopes the test itself built, which means the roster
/// could stop connecting — a topic nobody publishes, an agent nobody
/// registered — with all of them still green. These are the ones that only
/// fail when something structural broke.
/// </summary>
public class SmokeTests
{
    /// <summary>
    /// One turn, all the way through: text in, spoken action out. Deliberately
    /// asserts nothing about the words — a mock substrate wrote them. What it
    /// proves is that the chain is still a chain.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ATurnGoesInAndAnActionComesOut()
    {
        using var host = new TestHost();
        var actions = host.Bus.Subscribe(Topics.Action);

        await host.StartAsync();
        host.Resolve<PerceptionAgent>().Perceive("what did we say about the trip");

        var action = await actions.ReadAsync(TestHost.Patience);

        Assert.False(string.IsNullOrWhiteSpace(action.Meta.Get<string>(IntentAgent.ReplyKey)));
    }

    /// <summary>
    /// An agent that exists but was never registered is invisible: the bus has
    /// no replay and Governance's roster is config, so the turn simply comes
    /// back thinner. Reflection over the assembly rather than a hand-kept
    /// list, so a new agent joins this check by existing.
    ///
    /// Cheaper than booting the real host, and correspondingly narrower: it
    /// says every agent can be built from the registrations it needs, not that
    /// Program.cs registers them. Those are different claims and only the
    /// first one is worth 30 milliseconds.
    /// </summary>
    [Fact]
    public void EveryAgentInTheAssemblyCanBeConstructedFromTheContainer()
    {
        using var host = new TestHost();

        var declared = typeof(GovernanceAgent).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsSubclassOf(typeof(AgentBase)))
            .ToList();

        Assert.NotEmpty(declared);
        foreach (var type in declared)
        {
            Assert.NotNull(host.Services.GetService(type));
        }
    }

    /// <summary>
    /// Three stores gained temp-file-then-move this week on the argument that
    /// a crash midway must not truncate what the persona knows. Nothing was
    /// checking the half of that which is observable: the temp file does not
    /// outlive the write, and one left behind by an earlier crash does not
    /// block the next one.
    /// </summary>
    [Fact]
    public async Task AWriteLeavesNoTempFileBehind_AndAStaleOneDoesNotBlockIt()
    {
        await InATempDirectory(async dir =>
        {
            var store = new ParquetPassageStore(dir);
            await store.WriteAsync([Note("a")], null, CancellationToken.None);

            var written = Directory.GetFiles(dir, "*.parquet");
            Assert.Single(written);

            // Left by a process that died mid-write. The next write must
            // overwrite it, not trip over it.
            await File.WriteAllTextAsync(written[0] + ".tmp", "half a file");
            await store.WriteAsync([Note("b")], null, CancellationToken.None);

            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(2, await CountAsync(store));
        });
    }

    /// <summary>
    /// The fix for the bundle map that only shrank on the happy path. A turn
    /// that dies between Intent and Security used to stay in the dictionary
    /// for the life of the process; now the bundle's own timer lets it go.
    ///
    /// Asserted as "eventually", never as "within": an upper bound on elapsed
    /// time measures the machine, which is the mistake ChannelBusTests already
    /// made once. Timeout is what fails this if the sweep never happens.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ABundleThatNeverGetsAVerdictIsEventuallyLetGo()
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var agent = new GovernanceAgent(bus, activity, LoggerFor<GovernanceAgent>(),
            Options.Create(new GovernanceOptions { BundleRoster = [], BundleTimeoutMs = 10, BundleAbandonMs = 20 }),
            new JsonlAgentStateStore(Path.GetTempFileName()), ShippedInstructions.Store);

        await agent.HandleAsync(Envelope.Create(Topics.Perception, "Perception", Severity.Neutral), CancellationToken.None);

        var bundles = typeof(GovernanceAgent)
            .GetField("_bundles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
        var count = () => (int)bundles.GetType().GetProperty("Count")!.GetValue(bundles)!;

        Assert.Equal(1, count());
        while (count() > 0)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The passage store used to edit the live cache in place — the same list
    /// SearchAsync enumerates without the lock, from other agents on other
    /// threads. Copy-on-write now, and this is what that buys: searching while
    /// writing no longer throws "collection was modified".
    ///
    /// A race, so it can only fail loudly and never pass falsely: if it goes
    /// green on a machine that happened not to interleave, nothing is claimed
    /// that was not true. Against the old code it threw readily.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SearchingWhileWritingDoesNotThrow()
    {
        await InATempDirectory(async dir =>
        {
            var store = new ParquetPassageStore(dir);
            await store.WriteAsync([Note("seed")], null, CancellationToken.None);

            var writing = Task.Run(async () =>
            {
                for (var i = 0; i < 30; i++)
                {
                    await store.WriteAsync([Note("note " + i)], null, CancellationToken.None);
                }
            });

            var searching = Task.Run(async () =>
            {
                while (!writing.IsCompleted)
                {
                    await store.SearchAsync(Unit, topK: 50, minScore: -1, CancellationToken.None);
                }
            });

            await Task.WhenAll(writing, searching);
            Assert.Equal(31, await CountAsync(store));
        });
    }

    private static readonly float[] Unit = [1f, 0f];

    private static Passage Note(string text) =>
        new(Guid.NewGuid().ToString("N"), text, [], DateTimeOffset.UtcNow, Unit, ModelId: "stub");

    private static async Task<int> CountAsync(IPassageStore store) =>
        (await store.SearchAsync(Unit, topK: 1000, minScore: -1, CancellationToken.None)).Count;

    private static async Task InATempDirectory(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "eci-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ILogger<T> LoggerFor<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    /// <summary>
    /// The roster wired the way Program.cs wires it, with every substrate,
    /// store and file replaced by something that needs no network and no
    /// persona. Registered through a real container rather than by hand, so
    /// the constructors are resolved the same way the host resolves them.
    /// </summary>
    private sealed class TestHost : IDisposable
    {
        /// <summary>
        /// A fresh budget per call, never a shared static one: a static
        /// CancellationTokenSource starts counting at type initialization,
        /// which the CLR is free to run long before the first test body. It
        /// did, and the turn lost a race against a clock that had already
        /// been running.
        /// </summary>
        public static CancellationToken Patience => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

        private readonly ServiceProvider _services;
        private readonly string _dir;
        private readonly List<AgentBase> _started = [];

        public TestHost()
        {
            _dir = Path.Combine(Path.GetTempPath(), "eci-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<BusActivityTracker>();
            services.AddSingleton<IMessageBus, ChannelBus>();
            services.AddSingleton<ISubstrateProvider, MockSubstrateProvider>();
            services.AddSingleton<IEmbeddingProvider>(new StubEmbeddings());
            services.AddSingleton<IInstructionStore>(ShippedInstructions.Store);
            services.AddSingleton<IArchiveStore>(new InMemoryArchiveStore());
            services.AddSingleton<IPassageStore>(new InMemoryPassageStore());
            services.AddSingleton<IAgentStateStore>(new JsonlAgentStateStore(Path.Combine(_dir, "memory.jsonl")));
            services.AddSingleton(Options.Create(new PersonaNameOptions()));
            services.AddSingleton<PersonaName>();
            services.AddSingleton<RuntimeKnobs>();
            // One rule, and a deliberately unmatchable one: SecurityRuleSet
            // refuses an empty set because clearing everything is
            // indistinguishable from mocking it out. The smoke test wants
            // Security in the chain, not Security having an opinion.
            services.AddSingleton(SecurityRuleSet.Parse(
                """{ "rules": [ { "id": "smoke", "verdict": "Red", "concern": "never", "any": ["\bzzzunmatchablezzz\b"] } ] }"""));

            // The real roster and the real manifest names. A test that invents
            // its own would keep passing after the shipped config stopped
            // matching the agents.
            services.AddSingleton(Options.Create(new GovernanceOptions
            {
                BundleRoster = ["Impulse", "Recall", "Identity", "Hindsight"],
                BundleTimeoutMs = 4000,
            }));
            services.AddSingleton(Options.Create(new AgentSubstrateManifest
            {
                Agents =
                {
                    ["Intent"] = new AgentSubstrateEntry { Class = "fast-medium" },
                    ["Librarian"] = new AgentSubstrateEntry { Class = "fast-low" },
                    ["Recall"] = new AgentSubstrateEntry { Class = "fast-low" },
                    ["Reflection"] = new AgentSubstrateEntry { Class = "fast-medium" },
                    ["Archivist"] = new AgentSubstrateEntry { Class = "fast-low" },
                },
            }));
            services.AddSingleton(Options.Create(new RecallOptions()));
            services.AddSingleton(Options.Create(new LibrarianOptions()));
            services.AddSingleton(Options.Create(new ArchivistOptions()));
            services.AddSingleton(Options.Create(new ReflectionOptions()));
            services.AddSingleton(Options.Create(new PassageOptions()));

            foreach (var type in typeof(GovernanceAgent).Assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsSubclassOf(typeof(AgentBase))))
            {
                services.AddSingleton(type);
            }

            _services = services.BuildServiceProvider();
        }

        public IServiceProvider Services => _services;

        public IMessageBus Bus => _services.GetRequiredService<IMessageBus>();

        public T Resolve<T>() where T : notnull => _services.GetRequiredService<T>();

        /// <summary>
        /// StartAsync, not ExecuteAsync, and for the reason ChannelBusTests
        /// already documents: the bus has no replay, so an agent that has not
        /// claimed its queue before the first publish misses the turn.
        /// </summary>
        public async Task StartAsync()
        {
            foreach (var type in typeof(GovernanceAgent).Assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsSubclassOf(typeof(AgentBase))))
            {
                var agent = (AgentBase)_services.GetRequiredService(type);
                await agent.StartAsync(CancellationToken.None);
                _started.Add(agent);
            }
        }

        public void Dispose()
        {
            foreach (var agent in _started)
            {
                agent.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            _services.Dispose();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that will not delete is not a test failure.
            }
        }
    }
}
