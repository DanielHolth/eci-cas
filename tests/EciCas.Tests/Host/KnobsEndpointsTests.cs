using EciCas.Core;
using EciCas.Host.Endpoints;

namespace EciCas.Tests.Host;

public class KnobsEndpointsTests
{
    [Fact]
    public async Task SaveKnobsAsync_WritesUpdatedValuesToEachTargetAndLeavesValidJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eci-cas-knobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var tierPath = Path.Combine(dir, "appsettings.Pro.json");
        var basePath = Path.Combine(dir, "appsettings.json");

        await File.WriteAllTextAsync(tierPath, "{\n  \"Knobs\": {\n    \"RecallDepth\": 5,\n    \"MaxSentences\": 2,\n    \"ReflectionEvery\": 5,\n    \"PerceptionChars\": 512,\n    \"ContextTurns\": 5,\n    \"Mood\": \"Neutral\"\n  }\n}\n");
        await File.WriteAllTextAsync(basePath, "{\n  \"Shell\": {\n    \"Dictation\": {\n      \"Language\": \"en\"\n    }\n  }\n}\n");

        var knobs = new RuntimeKnobs
        {
            RecallDepth = 7,
            MaxSentences = 4,
            ReflectionEvery = 9,
            PerceptionChars = 1024,
            ContextTurns = 6,
            Mood = Mood.Helpful,
            Language = "fr",
        };

        var ok = await KnobsEndpoints.SaveKnobsAsync([tierPath], knobs);
        var languageOk = await KnobsEndpoints.SaveLanguageAsync([basePath], knobs.Language);

        Assert.True(ok);
        Assert.True(languageOk);

        var tierText = await File.ReadAllTextAsync(tierPath);
        var baseText = await File.ReadAllTextAsync(basePath);

        Assert.Contains("\"RecallDepth\": 7", tierText);
        Assert.Contains("\"MaxSentences\": 4", tierText);
        Assert.Contains("\"Mood\": \"Helpful\"", tierText);
        Assert.Contains("\"Language\": \"fr\"", baseText);

        Assert.DoesNotContain(".tmp", tierText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".tmp", baseText, StringComparison.OrdinalIgnoreCase);
    }
}
