using EciCas.Core;

namespace EciCas.Agents.Toolkit;

public static class ToolkitRequest
{
    public const string NameKey = "toolkit.name";
    public const string CommandKey = "toolkit.command";

    public static MetaBag Build(string name, string command) =>
        MetaBag.Empty
            .With(NameKey, name)
            .With(CommandKey, command);
}
