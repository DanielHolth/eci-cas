using EciCas.Core;

namespace EciCas.Agents.Toolkit;

public static class ToolkitResult
{
    public const string NameKey = "toolkit.name";
    public const string OutputKey = "toolkit.output";
    public const string SuccessKey = "toolkit.success";
    public const string ErrorKey = "toolkit.error";
    public const string CommandKey = "toolkit.command";
    public const string ReferencesKey = "toolkit.references";

    public static MetaBag Build(string name, string command, string output, bool success, string? error = null, IReadOnlyList<ToolkitReference>? references = null) =>
        MetaBag.Empty
            .With(NameKey, name)
            .With(CommandKey, command)
            .With(OutputKey, output)
            .With(SuccessKey, success)
            .With(ErrorKey, error ?? string.Empty)
            .With(ReferencesKey, references ?? []);
}
