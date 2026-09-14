using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using EciCas.Agents.Intent;
using EciCas.Agents.Perception;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Agents.Sight;

/// <summary>
/// What is on the screen, as an advisory beside Impulse, Identity and Recall.
///
/// Two things make this agent shaped unlike every other cognitive one, and
/// both come from the same observation: the screen is captured the moment the
/// microphone opens, and the person then spends one to five seconds saying
/// their sentence. That is a substrate call's worth of time, free, and it is
/// spent before the turn exists.
///
/// So the cheap look does not wait for the turn. <see cref="Glimpse"/> is
/// called by the surface at the moment of capture and starts a low-detail
/// describe immediately, with no question attached -- a description of a
/// screen is ambient, and asking "what is here" needs nothing from the
/// person. By the time the transcript lands and the perception envelope
/// reaches <see cref="HandleAsync"/>, the answer is usually already sitting in
/// a completed task, and the advisory costs the turn nothing but a lookup.
///
/// The expensive look does wait, because it is the one that needs the words.
/// High detail resizes to 2048 instead of 512 and costs sixteen times as much,
/// which is only worth it when someone asked something the cheap look could
/// not answer. Two parties may ask for it: the person, by saying something
/// that names the screen, and the cheap look itself, by admitting it could not
/// make out what it was being shown.
///
/// Intent is not replaced by any of this. A player who has done a thing a
/// hundred times is understood by what Recall knows about them, not by what a
/// picture shows, and an agent that saw the screen but not the archive would
/// answer worse than the one that sees both. Sight describes; Intent speaks.
/// </summary>
public sealed class SightAgent : AgentBase, ICognitiveAgent
{
    /// <summary>What was seen, in the persona's own reading of it.</summary>
    public const string AdviceKey = "sight.advice";

    /// <summary>What the local OCR read, verbatim. Separate from the advice
    /// because it is evidence rather than judgement -- Intent quoting a line of
    /// this is quoting the screen, not a model's memory of the screen. It is
    /// also the only thing Sight has to offer on a tier with no eyes at all,
    /// which is what makes Free a narrower tier rather than a broken one.</summary>
    public const string WordsKey = "sight.words";

    /// <summary>The screenshot this turn was seen through, for the turn log.</summary>
    public const string ImageKey = "sight.image";

    /// <summary>Set on the reading Sight publishes straight to Security,
    /// the way Impulse marks its reflex. Tells the turn log which of the two
    /// proposals on a reading turn was the one that spoke.</summary>
    public const string ReadingKey = "sight.reading";

    /// <summary>
    /// Calls waiting for an envelope to be billed against. The glance is made
    /// before the turn exists -- that is the whole point of it -- so there is
    /// no correlation id to publish its cost under until perception lands.
    /// Queued here and drained once the turn arrives, which is late but
    /// correct, where publishing at call time would be neither.
    /// </summary>
    private readonly ConcurrentQueue<Trace> _traces = new();

    private readonly IMessageBus _bus;
    private readonly ISubstrateProvider _substrate;
    private readonly IInstructionStore _instructions;
    private readonly SightOptions _options;
    private readonly SubstrateOptions _substrates;
    private readonly ILogger<SightAgent> _logger;

    /// <summary>
    /// The look now in flight, or the one that just landed. One slot rather
    /// than a queue: a second hold of the voice key abandons the first, which
    /// is what the microphone does too.
    /// </summary>
    private Task<Seen>? _pending;

    /// <summary>
    /// What was on the screen last turn, and what was asked about it.
    ///
    /// Carried because a screen is a continuing thing and one frame of it is
    /// not. "Is it still there", "did that work", "what changed" are ordinary
    /// things to say to something watching your screen, and none of them can
    /// be answered from a single picture. One turn back rather than a window:
    /// this is orientation, and a transcript of the last six screens would
    /// cost more prompt than the picture it exists to explain.
    /// </summary>
    private Seen _previous = Seen.Nothing;
    private string _previousAsked = string.Empty;

    /// <summary>
    /// What the persona said last, which is half of what makes the next
    /// sentence mean anything. "Tell me more about the festival" names nothing
    /// on the screen and everything in the reply that preceded it, and a look
    /// told only the person's half is looking for a word it has never seen.
    /// Arrives on the conclusion, one topic later than the rest of this state.
    /// </summary>
    private string _previousReply = string.Empty;

    public SightAgent(
        IMessageBus bus,
        BusActivityTracker activity,
        ILogger<SightAgent> logger,
        ISubstrateProvider substrate,
        IInstructionStore instructions,
        IOptions<SightOptions> options,
        IOptions<SubstrateOptions> substrates)
        : base(bus, activity, logger)
    {
        _bus = bus;
        _substrate = substrate;
        _instructions = instructions;
        _options = options.Value;
        _substrates = substrates.Value;
        _logger = logger;
    }

    public override string Name => "Sight";

    public override IReadOnlyCollection<string> Subscriptions => [Topics.Perception, Topics.Conclusion];

    /// <summary>
    /// No eyes this tier. Free configures no Sight substrate at all, and a
    /// blind Sight is not a disabled one: the screenshot is still taken, the
    /// local OCR still reads it, and the words still reach Intent. She knows
    /// what it says and not what it looks like, which is a difference a person
    /// can hear and a tier boundary worth having.
    /// </summary>
    private bool Blind => !_options.Enabled || _substrates.Agents.GetValueOrDefault(Name)?.UseSubstrate is not true;

    /// <summary>
    /// How to take a screenshot, when no one has taken one already. Set by the
    /// shell, which owns the screen; null in the host, which does not have one.
    /// The voice path never needs it -- it has already called
    /// <see cref="Glimpse"/> before the turn exists, which is the cheap path
    /// and the normal one.
    /// </summary>
    public Func<Task>? Capture { get; set; }

    /// <summary>
    /// The screen, the moment it was captured, while the person is still
    /// talking. Fire and forget: the turn that follows collects it, and a look
    /// that fails leaves that turn simply blind.
    /// </summary>
    public void Glimpse(string path, byte[] jpeg, string words)
    {
        if (Blind)
        {
            // Still remembered, so the OCR half arrives even with no eyes.
            _pending = Task.FromResult(new Seen(path, string.Empty, words, null, false));
            return;
        }

        var image = new SubstrateImage(jpeg, "image/jpeg", _options.Detail);
        _pending = Task.Run(() => GlanceAsync(path, image, words));
    }

    /// <summary>
    /// The turn has arrived. Collect the look that was already running, pay for
    /// a closer one if this turn turns out to want it, and publish.
    ///
    /// The advisory is published even when there is nothing to say, and that is
    /// load-bearing: Governance holds the bundle until every advisor on its
    /// roster has reported, so an agent staying silent on the turns it has no
    /// opinion about would cost each of those turns the whole bundle timeout.
    /// </summary>
    public override async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // The conclusion is not a turn to look at; it is the other half of the
        // last one. Kept for the next look, and nothing else happens here.
        if (envelope.Topic == Topics.Conclusion)
        {
            if (envelope.Meta.Get<string>(IntentAgent.ReplyKey) is { Length: > 0 } reply)
            {
                _previousReply = reply;
            }

            return;
        }

        var asked = envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;

        // A reading is a close look already, so the escalation below stands
        // down for one: paying for high detail twice on the same picture buys
        // the same pixels twice.
        var reading = Reading(asked);

        var seen = Seen.Nothing;
        string? degraded = null;

        // Nothing was captured on the way in. That is the typed turn: a person
        // at the keyboard opened no microphone, so there was no moment to take
        // the shot at and no sentence being spoken to take it during. Take it
        // now, in the turn, and pay the latency -- a typed "what is on my
        // screen" that answers blind is the one failure this agent exists to
        // prevent.
        if (Volatile.Read(ref _pending) is null && Capture is { } capture)
        {
            try
            {
                await capture().ConfigureAwait(false);
            }
            catch (Exception ex) when (!SubstrateHealth.IsShutdown(ex, cancellationToken))
            {
                _logger.LogWarning("Sight could not take a shot of its own: {Cause}", SubstrateHealth.Classify(ex));
            }
        }

        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is not null)
        {
            try
            {
                seen = await pending
                    .WaitAsync(TimeSpan.FromMilliseconds(_options.CollectMs), cancellationToken)
                    .ConfigureAwait(false);
                degraded = seen.Degraded;
            }
            catch (Exception ex) when (!SubstrateHealth.IsShutdown(ex, cancellationToken))
            {
                degraded = SubstrateHealth.Classify(ex);
                _logger.LogWarning("Sight could not collect its glance: {Cause}", degraded);
            }

            if (degraded is null && !reading && Escalating(asked, seen))
            {
                seen = await LookAgainAsync(seen, asked, cancellationToken).ConfigureAwait(false);
                degraded = seen.Degraded;
            }
        }

        var meta = MetaBag.Empty;

        if (seen.Description.Length > 0)
        {
            meta = meta.With(AdviceKey, PromptCap.Apply(seen.Description, _options.AdviceChars));
        }

        if (seen.Words.Length > 0)
        {
            meta = meta.With(WordsKey, PromptCap.Apply(seen.Words, _options.WordsChars));
        }

        if (seen.Path.Length > 0)
        {
            meta = meta.With(ImageKey, seen.Path);
        }

        if (reading)
        {
            await SpeakReadingAsync(envelope, seen, asked, cancellationToken).ConfigureAwait(false);
        }

        // Held for one turn only, and without its bytes: a screenshot kept past
        // the turn that wanted it is a megabyte of nothing.
        _previous = seen with { Image = null };
        _previousAsked = asked;

        _bus.Publish(Topics.Advisories, envelope.Derive(
            Topics.Advisories, Name, envelope.Severity, SubstrateHealth.Mark(meta, degraded)));

        Bill(envelope);
    }

    /// <summary>
    /// "Read the screen to me" -- the one thing Sight says in its own voice.
    ///
    /// It goes straight onto the proposal topic, past Intent, and that is the
    /// whole point of it: Intent is held to two to four sentences, and a
    /// person who cannot see their screen asking what is on it is owed the
    /// screen rather than a summary of it. Security still gates it, because
    /// everything published here is read by the same gate.
    ///
    /// Governance's own rule does the rest. A proposal that is not marked as a
    /// reflex claims the turn, and the first claim wins: this one is published
    /// while Intent is still waiting on the bundle, so the reading speaks and
    /// Intent's reply is dropped. If the close reading turns out slow enough
    /// that Intent got there first, the claim fails and the person simply gets
    /// the shorter answer -- late is the failure mode, never doubled.
    /// </summary>
    private async Task SpeakReadingAsync(Envelope envelope, Seen seen, string asked, CancellationToken cancellationToken)
    {
        var reading = await ReadAsync(seen, asked, cancellationToken).ConfigureAwait(false);
        if (reading.Length == 0)
        {
            return;
        }

        _bus.Publish(Topics.Proposal, envelope.Derive(
            Topics.Proposal, Name, envelope.Severity,
            MetaBag.Empty.With(IntentAgent.ReplyKey, reading).With(ReadingKey, true)));
    }

    /// <summary>
    /// The screen laid out as something to listen to. At full detail, since a
    /// reading is exactly the case the cheap pass cannot serve -- and falling
    /// back to the local OCR transcript when there are no eyes at all, which
    /// is a plainer reading than the model's but a true one, and free.
    /// </summary>
    private async Task<string> ReadAsync(Seen seen, string asked, CancellationToken cancellationToken)
    {
        if (seen.Image is null)
        {
            return seen.Words;
        }

        var prompt = new StringBuilder(_instructions.For(Name, "read"))
            .Append("\n\nThey said: ").Append(PromptCap.Apply(asked));

        if (seen.Words.Length > 0)
        {
            prompt.Append("\n\nText read off this screen by the machine's own reader:\n")
                .Append(PromptCap.Apply(seen.Words, _options.WordsChars));
        }

        var image = new SubstrateImage(seen.Image, "image/jpeg", ImageDetail.High);
        var result = await CallAsync("read", prompt.ToString(), image, cancellationToken).ConfigureAwait(false);

        return result is null ? seen.Words : result.Text.Trim();
    }

    /// <summary>
    /// A reading asked for, as against a look. Substring matching over a
    /// configured list, the same shape as <see cref="Escalating"/> and for the
    /// same reason -- and deliberately narrow: mistaking an ordinary question
    /// for a reading spends the persona's whole turn reciting a screen nobody
    /// asked about.
    /// </summary>
    private bool Reading(string asked) =>
        _options.ReadPhrases.Any(phrase =>
            phrase.Length > 0 && asked.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The cheap pass: no question, low detail, last turn's screen for
    /// orientation. Its whole job is to say what is there, so that the turn
    /// arriving a second later already has it.
    /// </summary>
    private async Task<Seen> GlanceAsync(string path, SubstrateImage image, string words)
    {
        var prompt = new StringBuilder(_instructions.For(Name, "glance"));

        if (_previous.Description.Length > 0)
        {
            prompt.Append("\n\nA moment ago this screen looked like: ")
                .Append(PromptCap.Apply(_previous.Description, _options.AdviceChars));

            if (_previousAsked.Length > 0)
            {
                prompt.Append("\nand they said: ").Append(PromptCap.Apply(_previousAsked));
            }
        }

        // The words go in even on the cheap pass. They are free, they are
        // already read, and they are exactly what a 512-pixel image cannot
        // supply: a model that can see a tooltip's shape and read its text
        // describes a screen far better than one doing either alone.
        if (words.Length > 0)
        {
            prompt.Append("\n\nText read off this screen by the machine's own reader:\n")
                .Append(PromptCap.Apply(words, _options.WordsChars));
        }

        var result = await CallAsync("glance", prompt.ToString(), image, CancellationToken.None).ConfigureAwait(false);

        return result is null
            ? new Seen(path, string.Empty, words, SubstrateHealth.Unreachable, false)
            : Parse(path, result.Text, words) with { Image = image.Bytes };
    }

    /// <summary>
    /// The expensive pass, and the only one that is told what was asked. Same
    /// picture, sixteen times the patches, and this time it knows what it is
    /// looking for.
    /// </summary>
    private async Task<Seen> LookAgainAsync(Seen glance, string asked, CancellationToken cancellationToken)
    {
        if (glance.Image is null)
        {
            return glance;
        }

        _logger.LogInformation("Sight is taking a closer look at {Path}", glance.Path);

        var prompt = new StringBuilder(_instructions.For(Name, "look"))
            .Append("\n\nThey said: ").Append(PromptCap.Apply(asked));

        if (glance.Description.Length > 0)
        {
            prompt.Append("\n\nAt a glance you took this for: ").Append(glance.Description);
        }

        if (glance.Words.Length > 0)
        {
            prompt.Append("\n\nText read off this screen by the machine's own reader:\n")
                .Append(PromptCap.Apply(glance.Words, _options.WordsChars));
        }

        var image = new SubstrateImage(glance.Image, "image/jpeg", ImageDetail.High);
        var result = await CallAsync("look", prompt.ToString(), image, cancellationToken).ConfigureAwait(false);

        // A failed second look keeps the first. The cheap description is a
        // worse answer than the close one and a far better answer than none.
        return result is null ? glance : Parse(glance.Path, result.Text, glance.Words);
    }

    private async Task<SubstrateResult?> CallAsync(string label, string prompt, SubstrateImage image, CancellationToken cancellationToken)
    {
        _logger.LogDebug("{Agent} {Detail} prompt >>>\n{Prompt}", Name, image.Detail, prompt);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _substrate.CompleteAsync(Name, prompt, image, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "{Agent} looked at {Detail} detail in {LatencyMs}ms, {Tokens} tokens",
                Name, image.Detail, result.Latency.TotalMilliseconds, result.TokenCount);
            _logger.LogDebug("{Agent} response <<<\n{Response}", Name, result.Text);

            // Queued on the way out, the same as a failure. Only the failures
            // were, which left every look this agent actually completed off
            // the meter: the turn log showed one call where two had been made,
            // and the running total was short by the price of every glance
            // since the agent shipped. A faculty that spends money silently is
            // the one thing an energy meter cannot have.
            _traces.Enqueue(new Trace(label, result, result.Latency.TotalMilliseconds, null));

            return result;
        }
        catch (Exception ex) when (!SubstrateHealth.IsShutdown(ex, cancellationToken))
        {
            // With the exception, unlike everywhere else: a classified cause
            // says a look failed and nothing about why, and the why here is
            // the vendor's own body -- an unsupported image, a model that
            // cannot see, a rejected detail level all classify identically.
            _logger.LogWarning(ex,
                "{Agent} could not look ({Detail}, {ElapsedMs}ms): {Cause}",
                Name, image.Detail, Stopwatch.GetElapsedTime(started).TotalMilliseconds, SubstrateHealth.Classify(ex));

            _traces.Enqueue(new Trace(label, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, SubstrateHealth.Classify(ex)));
            return null;
        }
    }

    /// <summary>
    /// Two ways a turn earns the closer look, and it need satisfy only one.
    ///
    /// The person names the screen -- a phrase from
    /// <see cref="SightOptions.CloserPhrases"/>, which is config rather than
    /// code because two languages are spoken at this machine and a list of
    /// English verbs would leave half the questions on the cheap pass.
    ///
    /// Or the cheap pass asks for it itself. That is the better signal of the
    /// two: it is the only party that has actually seen the image, and it needs
    /// no guess about what the person meant by their words.
    /// </summary>
    private bool Escalating(string asked, Seen seen)
    {
        if (!_options.CloserEnabled || _options.Detail == ImageDetail.High || seen.Image is null)
        {
            return false;
        }

        return seen.WantsCloser
            || _options.CloserPhrases.Any(phrase =>
                phrase.Length > 0 && asked.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The cheap pass ends with <see cref="SightOptions.CloserMarker"/> on a
    /// line of its own when it could not resolve something it thinks matters. A
    /// marker rather than structured output because everything else this agent
    /// says is prose that goes straight into another agent's prompt, and one
    /// reserved word is cheaper to parse -- and cheaper to teach -- than a JSON
    /// contract wrapped around a sentence.
    /// </summary>
    private Seen Parse(string path, string text, string words)
    {
        var trimmed = text.Trim();
        var closer = trimmed.EndsWith(_options.CloserMarker, StringComparison.OrdinalIgnoreCase);
        if (closer)
        {
            trimmed = trimmed[..^_options.CloserMarker.Length].TrimEnd();
        }

        return new Seen(path, trimmed, words, null, closer);
    }

    /// <summary>
    /// Every call made for this turn, onto the telemetry topic under the
    /// turn's own correlation id. Labelled, because a turn can hold three of
    /// them at two prices and "Sight spent that" is not the same answer as
    /// "the closer look spent that".
    /// </summary>
    private void Bill(Envelope envelope)
    {
        while (_traces.TryDequeue(out var trace))
        {
            if (trace.Result is { } result)
            {
                SubstrateTrace.Publish(_bus, envelope, Name, result, trace.Label);
            }
            else
            {
                SubstrateTrace.PublishFailure(_bus, envelope, Name, trace.LatencyMs, trace.Cause ?? SubstrateHealth.Unreachable, trace.Label);
            }
        }
    }

    /// <summary>One substrate call this agent made, and what it cost.</summary>
    private sealed record Trace(string Label, SubstrateResult? Result, double LatencyMs, string? Cause);

    /// <param name="Path">The screenshot on disk this was read from.</param>
    /// <param name="Description">What the model made of it, or empty.</param>
    /// <param name="Words">What the local OCR read off it.</param>
    /// <param name="Degraded">Non-null when this is a blind turn rather than a seen one.</param>
    /// <param name="WantsCloser">The cheap pass said it could not make something out.</param>
    private sealed record Seen(string Path, string Description, string Words, string? Degraded, bool WantsCloser)
    {
        public static readonly Seen Nothing = new(string.Empty, string.Empty, string.Empty, null, false);

        /// <summary>The picture itself, kept only until the turn decides
        /// whether it wants a closer look at it.</summary>
        public byte[]? Image { get; init; }
    }
}
