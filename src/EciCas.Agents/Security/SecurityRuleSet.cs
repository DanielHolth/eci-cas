using System.Text.Json;
using System.Text.RegularExpressions;
using EciCas.Core;

namespace EciCas.Agents.Security;

/// <summary>
/// Security's rule engine: a closed, declarative pattern list. Evaluation is
/// total and order-independent — every rule is tested, highest verdict wins.
/// Green is the absence of a match. No model here — the hard stop only works
/// while it stays mechanical.
/// </summary>
public sealed class SecurityRuleSet
{
    private readonly IReadOnlyList<SecurityRule> _rules;

    private SecurityRuleSet(IReadOnlyList<SecurityRule> rules) => _rules = rules;

    public static SecurityRuleSet Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Security rules file not found: {path}. Security cannot run real with no rules — " +
                "point Security:RulesPath at a real file.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static SecurityRuleSet Parse(string json, string source = "<memory>")
    {
        RuleFile? document;
        try
        {
            document = JsonSerializer.Deserialize<RuleFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{source}: not valid JSON — {ex.Message}", ex);
        }

        if (document?.Rules is null || document.Rules.Count == 0)
        {
            throw new InvalidOperationException(
                $"{source}: 'rules' must be a non-empty list. An empty rule set clears everything, " +
                "which is indistinguishable from a mock.");
        }

        var seen = new HashSet<string>();
        var rules = new List<SecurityRule>();
        foreach (var raw in document.Rules)
        {
            if (string.IsNullOrWhiteSpace(raw.Id))
            {
                throw new InvalidOperationException(
                    $"{source}: a rule has no 'id'. Every verdict names the rule that produced it.");
            }

            if (!seen.Add(raw.Id))
            {
                throw new InvalidOperationException($"{source}: duplicate rule id '{raw.Id}'.");
            }

            if (!Enum.TryParse<Verdict>(raw.Verdict, ignoreCase: true, out var verdict))
            {
                throw new InvalidOperationException(
                    $"{source}: rule '{raw.Id}' has verdict '{raw.Verdict}'; must be Yellow or Red.");
            }

            if (verdict == Verdict.Green)
            {
                throw new InvalidOperationException(
                    $"{source}: rule '{raw.Id}' declares verdict 'Green'. Green is the ABSENCE of a match, " +
                    "not something a rule asserts — use 'unless' on the rule you mean to narrow.");
            }

            if (string.IsNullOrWhiteSpace(raw.Concern))
            {
                throw new InvalidOperationException(
                    $"{source}: rule '{raw.Id}' has no 'concern'. A non-green verdict travels to a reasoner " +
                    "that has to act on it — a verdict with no reason is not actionable.");
            }

            rules.Add(new SecurityRule
            {
                Id = raw.Id,
                Verdict = verdict,
                Concern = raw.Concern,
                AnyOf = Compile(raw.Any, raw.Id, source),
                AllOf = Compile(raw.All, raw.Id, source),
                Unless = Compile(raw.Unless, raw.Id, source),
            });
        }

        return new SecurityRuleSet(rules);
    }

    private static IReadOnlyList<Regex> Compile(List<string>? patterns, string ruleId, string source)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return [];
        }

        try
        {
            return patterns.Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToList();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{source}: rule '{ruleId}' has a bad pattern — {ex.Message}", ex);
        }
    }

    public SecurityEvaluation Evaluate(string? text)
    {
        text ??= string.Empty;
        var verdict = Verdict.Green;
        var matched = new List<SecurityRule>();

        foreach (var rule in _rules)
        {
            if (!rule.Matches(text))
            {
                continue;
            }

            matched.Add(rule);
            if (rule.Verdict > verdict)
            {
                verdict = rule.Verdict;
            }
        }

        if (verdict == Verdict.Green)
        {
            return new SecurityEvaluation(Verdict.Green, string.Empty, []);
        }

        // The concern comes from the rules that produced THIS verdict, not
        // from every rule that matched: a red action that also tripped a
        // yellow advisory should be explained by the red.
        var decisive = matched.Where(rule => rule.Verdict == verdict).ToList();
        var concern = string.Join(" ", decisive.Select(rule => rule.Concern));
        if (concern.Length > 300)
        {
            concern = concern[..300];
        }

        return new SecurityEvaluation(verdict, concern, matched.Select(rule => rule.Id).ToList());
    }

    private sealed class RuleFile
    {
        public string? Version { get; set; }
        public List<RawRule>? Rules { get; set; }
    }

    private sealed class RawRule
    {
        public string? Id { get; set; }
        public string? Verdict { get; set; }
        public string? Concern { get; set; }
        public string? Description { get; set; }
        public List<string>? Any { get; set; }
        public List<string>? All { get; set; }
        public List<string>? Unless { get; set; }
    }
}
