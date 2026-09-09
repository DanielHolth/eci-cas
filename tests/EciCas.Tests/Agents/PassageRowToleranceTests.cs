using EciCas.Agents.Passages;
using Parquet.Serialization;

namespace EciCas.Tests.Agents;

/// <summary>
/// A column nobody wrote comes back as "" rather than null, and "" is not
/// JSON. One such row used to throw out of the passage load that runs before
/// the bus starts, so a single unreadable note took the whole host down.
/// </summary>
public class PassageRowToleranceTests
{
    private sealed class LegacyRow
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Pairs { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string Embedding { get; set; } = "";
        public string ParentIds { get; set; } = "";
    }

    [Fact]
    public async Task ARowWithNoPairsOrLineageStillLoads()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, ParquetPassageStore.FileName);
            await using (var stream = File.Create(path))
            {
                await ParquetSerializer.SerializeAsync(
                    new List<LegacyRow>
                    {
                        new()
                        {
                            Id = "a",
                            Text = "a thought",
                            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                            Embedding = Convert.ToBase64String(new byte[4]),
                        },
                    },
                    stream);
            }

            var latest = await new ParquetPassageStore(dir).LatestAsync(CancellationToken.None);

            Assert.NotNull(latest);
            Assert.Equal("a thought", latest!.Text);
            Assert.Empty(latest.Pairs);
            Assert.Empty(latest.ParentIds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
