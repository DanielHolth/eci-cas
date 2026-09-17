namespace EciCas.Agents.Utterances;

using EciCas.Core;

/// <summary>
/// What the extractor read, turned into rows.
///
/// One implementation for all three callers -- the live write, the boot
/// gap-fill and the boot re-read -- because a row's shape is not one of the
/// things that may differ between them. The live path and the backfill each
/// grew their own copy of this loop and they had already drifted: only one
/// of them stamped the utterance's own turn rather than today's, which is
/// the difference between a recovered fact that has been recallable since it
/// was said and one that claims to be new.
/// </summary>
public static class FactMint
{
    public static async Task<List<Fact>> RowsAsync(
        Utterance utterance,
        IReadOnlyList<ExtractedFact> extracted,
        UtteranceOptions options,
        IFactReliabilityScorer? scorer = null,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<Fact>();
        foreach (var fact in extracted)
        {
            var sentence = fact.Text;

            // "Read, and there was nothing here." Not filtered, not
            // keyworded, not embedded -- it is not a claim, it is a receipt
            // for a model call, so the same call is never bought twice.
            if (sentence.Length == 0)
            {
                rows.Add(Fact.NothingStated(utterance, fact.OriginModel));
                continue;
            }

            // Nothing to recall from a sentence with no content word in it,
            // and it would compete for one of five slots forever. Per fact
            // rather than per utterance: a paste that mixes "hey!" with three
            // real claims loses only the greeting.
            var keywords = KeywordExtractor.Content(sentence);
            if (!UtteranceFilter.Keep(keywords, options))
            {
                continue;
            }

            var row = new Fact(
                Id: Guid.NewGuid().ToString("n"),
                // The utterance's own turn, not today's: a recovered fact
                // has been recallable since it was said.
                Turn: utterance.Turn,
                Text: sentence,
                Timestamp: utterance.Timestamp,
                Speaker: utterance.Speaker,
                Keywords: keywords,
                OriginModel: fact.OriginModel,
                Class: fact.Class,
                Entity: fact.Entity,
                Sensitivity: fact.Sensitivity);

            if (scorer is not null)
            {
                var score = await scorer.ScoreAsync(row, cancellationToken).ConfigureAwait(false);
                row = row with
                {
                    Confidence = score.Confidence,
                    Freshness = score.Freshness,
                    EvaluatedAt = score.EvaluatedAt,
                };
            }

            rows.Add(row);
        }

        return rows;
    }
}
