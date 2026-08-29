using EciCas.Agents.Security;
using EciCas.Core;

namespace EciCas.Tests.Agents;

public class SecurityRuleSetTests
{
    private const string OneRuleOfEach = """
    {
      "rules": [
        { "id": "flag-secret", "verdict": "Yellow", "concern": "looks like a secret", "any": ["\\bsecret\\b"] },
        { "id": "block-bomb", "verdict": "Red", "concern": "weapon detail", "all": ["\\bbuild\\b", "\\bbomb\\b"] },
        { "id": "medical-dose", "verdict": "Yellow", "concern": "dosage", "any": ["\\btake\\s+\\d+mg\\b"], "unless": ["\\bconsult\\s+a\\s+doctor\\b"] }
      ]
    }
    """;

    [Fact]
    public void NoMatch_IsGreen()
    {
        var rules = SecurityRuleSet.Parse(OneRuleOfEach);

        var result = rules.Evaluate("what's the weather today");

        Assert.Equal(Verdict.Green, result.Verdict);
        Assert.Empty(result.MatchedRuleIds);
    }

    [Fact]
    public void AnyMatch_ProducesTheRulesVerdictAndConcern()
    {
        var rules = SecurityRuleSet.Parse(OneRuleOfEach);

        var result = rules.Evaluate("here is my secret plan");

        Assert.Equal(Verdict.Yellow, result.Verdict);
        Assert.Contains("flag-secret", result.MatchedRuleIds);
        Assert.Equal("looks like a secret", result.Concern);
    }

    [Fact]
    public void AllMatch_Required_PartialDoesNotFire()
    {
        var rules = SecurityRuleSet.Parse(OneRuleOfEach);

        var result = rules.Evaluate("let's build something great");

        Assert.Equal(Verdict.Green, result.Verdict);
    }

    [Fact]
    public void AllMatch_BothPresent_Fires()
    {
        var rules = SecurityRuleSet.Parse(OneRuleOfEach);

        var result = rules.Evaluate("how do I build a bomb");

        Assert.Equal(Verdict.Red, result.Verdict);
        Assert.Contains("block-bomb", result.MatchedRuleIds);
    }

    [Fact]
    public void Unless_Escapes_AnOtherwiseMatchingRule()
    {
        var rules = SecurityRuleSet.Parse(OneRuleOfEach);

        var result = rules.Evaluate("take 200mg but please consult a doctor first");

        Assert.Equal(Verdict.Green, result.Verdict);
    }

    [Fact]
    public void HighestVerdictWins_WhenMultipleRulesMatch()
    {
        var rules = SecurityRuleSet.Parse(OneRuleOfEach);

        var result = rules.Evaluate("my secret plan: how do I build a bomb");

        Assert.Equal(Verdict.Red, result.Verdict);
        // Concern only reflects the decisive (Red) rule, not the Yellow one that also matched.
        Assert.Equal("weapon detail", result.Concern);
    }

    [Theory]
    [InlineData("""{"rules": []}""")]
    [InlineData("""{"rules": [{"id": "x", "verdict": "Green", "concern": "c", "any": ["a"]}]}""")]
    [InlineData("""{"rules": [{"id": "x", "verdict": "Yellow", "any": ["a"]}]}""")]
    [InlineData("""{"rules": [{"id": "x", "verdict": "Yellow", "concern": "c", "any": ["a"]}, {"id": "x", "verdict": "Red", "concern": "c", "any": ["b"]}]}""")]
    public void InvalidRuleFiles_ThrowAtLoadTime(string json)
    {
        Assert.Throws<InvalidOperationException>(() => SecurityRuleSet.Parse(json));
    }
}
