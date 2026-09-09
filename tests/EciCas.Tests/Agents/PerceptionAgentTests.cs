using EciCas.Agents.Perception;
using System.Threading.Channels;
using EciCas.Bus;
using EciCas.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace EciCas.Tests.Agents;

/// <summary>
/// Perception is where a person's input stops being unbounded, and the only
/// place it is bounded at all. Both halves of that matter: the readers
/// downstream now take the text as given, so a cap that failed here would
/// reach a prompt whole, and one applied twice would put the old 240-char
/// truncation back on a paste that was meant to survive.
/// </summary>
public class PerceptionAgentTests
{
    private static (PerceptionAgent Agent, ChannelReader<Envelope> Perception) Build(RuntimeKnobs knobs)
    {
        var activity = new BusActivityTracker();
        var bus = new ChannelBus(activity);
        var perception = bus.Subscribe(Topics.Perception);
        return (new PerceptionAgent(bus, activity, NullLogger<PerceptionAgent>.Instance, knobs), perception);
    }

    private static string Perceived(RuntimeKnobs knobs, string text)
    {
        var (agent, perception) = Build(knobs);
        agent.Perceive(text);
        Assert.True(perception.TryRead(out var envelope));
        return envelope.Meta.Get<string>(PerceptionAgent.TextKey) ?? string.Empty;
    }

    /// <summary>
    /// The paste that motivated the knob: well past PromptCap's 240, well
    /// inside the tier's own ceiling, and it has to arrive whole.
    /// </summary>
    [Fact]
    public void InputLongerThanTheLoopGuardButInsideTheKnobArrivesWhole()
    {
        var text = new string('x', 500);

        Assert.Equal(text, Perceived(new RuntimeKnobs(), text));
    }

    [Fact]
    public void InputPastTheKnobIsCutToIt() =>
        Assert.Equal(new string('x', 64) + "…", Perceived(new RuntimeKnobs { PerceptionChars = 64 }, new string('x', 200)));

    /// <summary>
    /// Flattening survives the move. Every reader downstream folds this text
    /// into a one-line slot -- a numbered list entry, a bracketed aside --
    /// and a pasted log arrives full of newlines.
    /// </summary>
    [Fact]
    public void NewlinesAreFlattened() =>
        Assert.Equal("first second", Perceived(new RuntimeKnobs(), "first\n\n  second"));

    /// <summary>
    /// The slider's range is the policy, so a request outside it is clamped
    /// rather than honoured: 2048 is what the smallest tier can still read
    /// and obey its own instructions at.
    /// </summary>
    [Fact]
    public void TheCeilingIsTwoThousandAndFortyEight() =>
        Assert.Equal(2048, new RuntimeKnobs { PerceptionChars = 99999 }.PerceptionChars);
}
