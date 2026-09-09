using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Agents.Reflection;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;

namespace EciCas.Agents.TurnWindow;

/// <summary>
/// The running transcript, kept once and read by whoever needs continuity.
///
/// Intent was stateless per turn: every turn's sense of what came before was
/// reconstructed from the outside, by Recall out of archive rows and by
/// Hindsight out of passage notes. That is the right way to remember a
/// conversation from last week and a strange way to remember the sentence
/// before this one -- a person who has just been told their own name should
/// not need a vector search to answer a follow-up about it.
///
/// What is kept is structural, not lexical: both speakers' words verbatim,
/// with the scaffolding around them dropped. Compaction happens by discarding
/// whole old turns, never by thinning the words inside a kept one. Intent is
/// asked to produce a register and Reflection to judge a mood, and both live
/// in exactly the small words a lexical squeeze would take out -- "not bad at
/// all" compacts to "bad".
///
/// It listens on two topics because the two halves of a turn arrive
/// separately, and it records only the person's own text rather than the
/// context Intent built around it, so nothing that is itself a window can
/// end up inside the next one.
/// </summary>
public sealed class TurnWindowAgent : AgentBase
{
    /// <summary>How many turns are retained. Well past any window a tier
    /// asks for, because the newest idea may sit outside the slice and still
    /// has to be findable.</summary>
    private const int Capacity = 64;

    private readonly List<Turn> _turns = [];
    private readonly object _lock = new();

    public TurnWindowAgent(IMessageBus bus, BusActivityTracker activity, ILogger<TurnWindowAgent> logger)
        : base(bus, activity, logger)
    {
    }

    public override string Name => "TurnWindow";
    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.Conclusion];

    public override Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (envelope.Topic == Topics.Perception)
            {
                _turns.Add(new Turn(
                    envelope.CorrelationId,
                    envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty,
                    envelope.Meta.Get<string>(PerceptionAgent.ProfileKey),
                    envelope.Meta.Get<string>(ReflectionAgent.TriggeredByKey) == "self"));

                if (_turns.Count > Capacity)
                {
                    _turns.RemoveRange(0, _turns.Count - Capacity);
                }

                return Task.CompletedTask;
            }

            // A turn is only in the window once it has both halves. An
            // unanswered one is the turn being answered right now, and
            // showing a speaker their own unanswered prompt as history is
            // the one thing a window must never do.
            var reply = envelope.Meta.Get<string>(IntentAgent.ReplyKey);
            var turn = _turns.FindLast(t => t.CorrelationId == envelope.CorrelationId);
            if (turn is not null && !string.IsNullOrWhiteSpace(reply))
            {
                turn.Reply = reply;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The last <paramref name="turns"/> concluded turns this person was
    /// part of, oldest first, with the persona's own ideas timed rather than
    /// merely included.
    ///
    /// Timing, because an idea is a different kind of entry from a reply to
    /// a person: it is the persona talking to itself, and two of them in one
    /// window read as a monologue the person is being asked to join
    /// mid-thought. So the newest one is always present -- brought forward
    /// if the plain slice missed it -- and any older one is dropped.
    ///
    /// A window of zero is a real answer, not a disabled feature: the
    /// smallest tier runs a 4B whose instruction-following degrades with
    /// prompt length, and a transcript it cannot hold costs it the length
    /// bracket and the mood vocabulary it could otherwise obey.
    /// </summary>
    public IReadOnlyList<(string Given, string Replied)> Recent(int turns, Guid exclude, string? profileId)
    {
        if (turns <= 0)
        {
            return [];
        }

        lock (_lock)
        {
            // An idea belongs to nobody, so it is visible to everyone; a
            // person's own turns are not, which is what keeps two profiles
            // on one device out of each other's transcripts.
            var mine = _turns
                .Where(t => t.Reply is not null && t.CorrelationId != exclude)
                .Where(t => t.SelfTriggered || t.ProfileId is null || t.ProfileId == profileId)
                .ToList();

            var slice = mine.TakeLast(turns).ToList();
            var newestIdea = mine.FindLast(t => t.SelfTriggered);

            if (newestIdea is not null)
            {
                slice.RemoveAll(t => t.SelfTriggered && t != newestIdea);
                if (!slice.Contains(newestIdea))
                {
                    slice.Insert(0, newestIdea);
                }
            }

            return [.. slice.Select(t => (t.Utterance, t.Reply!))];
        }
    }

    /// <summary>
    /// One shape for a transcript, so Intent reading its recent turns and
    /// Reflection scoring a batch of them render the same thing. The numbers
    /// are there because Reflection asks the model to answer by index.
    /// </summary>
    public static string Render(IEnumerable<(string Given, string Replied)> turns) =>
        string.Join("\n", turns.Select((t, i) =>
            $"{i + 1}. Given: {PromptCap.Apply(t.Given, TranscriptChars)}\n   Replied: {PromptCap.Apply(t.Replied, TranscriptChars)}"));

    /// <summary>
    /// What one remembered turn may contribute. Wider than PromptCap's
    /// default because a turn is the thing being shown rather than an aside
    /// about it, and still bounded because the window multiplies it.
    /// </summary>
    public const int TranscriptChars = 400;

    private sealed class Turn(Guid correlationId, string utterance, string? profileId, bool selfTriggered)
    {
        public Guid CorrelationId { get; } = correlationId;
        public string Utterance { get; } = utterance;
        public string? ProfileId { get; } = profileId;
        public bool SelfTriggered { get; } = selfTriggered;
        public string? Reply { get; set; }
    }
}
