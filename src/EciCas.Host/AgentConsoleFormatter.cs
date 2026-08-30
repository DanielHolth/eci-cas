using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace EciCas.Host;

/// <summary>
/// Custom Console formatter, not the stock "simple" one: Information-level
/// lines (one per agent per turn, e.g. RecallAgent's consolidated pick
/// summary) collapse to a single physical line, colored per agent by a hash
/// of its short category name so a scrolling console stays scannable.
/// Warning/Error/Critical keep the stock two-line "warn: Category[id]" +
/// indented-message shape untouched, per the user's explicit ask to leave
/// warnings/errors as they are.
/// </summary>
public sealed class AgentConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "agent";

    private const string Esc = "";

    // 256-color ANSI codes, picked to stay readable on both light and dark
    // terminal backgrounds — not hand-assigned per agent, since agents get
    // added/renamed over time and a hash keeps this file untouched then.
    private static readonly int[] Palette = [39, 43, 76, 178, 141, 208, 51, 199, 106, 172, 63, 220];

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        if (logEntry.LogLevel >= LogLevel.Warning)
        {
            WriteLikeDefault(logEntry, message, textWriter);
            return;
        }

        var agent = ShortCategory(logEntry.Category);
        textWriter.Write(Colorize(agent, AgentColorCode(agent)));
        textWriter.Write('[');
        textWriter.Write(logEntry.EventId.Id);
        textWriter.Write("]  ");
        textWriter.WriteLine(message);
    }

    private static void WriteLikeDefault<TState>(in LogEntry<TState> logEntry, string message, TextWriter textWriter)
    {
        var (prefix, code) = logEntry.LogLevel switch
        {
            LogLevel.Warning => ("warn", 178),
            _ => ("fail", 196),
        };

        textWriter.Write(Colorize(prefix, code));
        textWriter.Write(": ");
        textWriter.Write(logEntry.Category);
        textWriter.Write('[');
        textWriter.Write(logEntry.EventId.Id);
        textWriter.WriteLine(']');
        textWriter.Write("      ");
        textWriter.WriteLine(message);
        if (logEntry.Exception is { } ex)
        {
            textWriter.WriteLine(ex);
        }
    }

    private static string ShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot < 0 ? category : category[(lastDot + 1)..];
    }

    private static int AgentColorCode(string agent) => Palette[(uint)agent.GetHashCode() % (uint)Palette.Length];

    private static string Colorize(string text, int ansi256Code) => $"{Esc}[38;5;{ansi256Code}m{text}{Esc}[0m";
}
