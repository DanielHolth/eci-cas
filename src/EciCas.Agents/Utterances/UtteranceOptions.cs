namespace EciCas.Agents.Utterances;

/// <summary>
/// The inverted archive's knobs, all of them measured rather than chosen.
/// Batches 23-25 in tools/retrieval-bench/RESULTS.md are the argument; this
/// class is where the numbers landed.
/// </summary>
public sealed class UtteranceOptions
{
    /// <summary>
    /// The write-time sweep's line, measured at 0.86.
    ///
    /// The roadmap said "start near 0.9 and let the corpus argue it down",
    /// and it argued. Under a top-five adjudicator the threshold is a recall
    /// filter, not a verdict: batch 25 found recall@5 flat at 0.999 across
    /// 0.84-0.96 with a perfect adjudicator making no merges anywhere in that
    /// range, so tightening buys nothing and costs *now* (0.933 down to
    /// 0.617) and split (0.043 up to 0.151). What made 0.92 look necessary
    /// was scoring the threshold as if it decided alone.
    /// </summary>
    public double ThreadThreshold { get; set; } = 0.86;

    /// <summary>
    /// How many candidates above the line the consolidator reads.
    ///
    /// Five, and not four or eight: recall into the candidate set is 0.674 at
    /// one, 0.837 at three, 0.994 at five and 0.999 at eight. There is a
    /// cliff between three and five and nothing after it. The 0.674 at one is
    /// also the consolidator's job description -- a third of the time the
    /// nearest candidate is not the right thread.
    /// </summary>
    public int ConsolidatorCandidates { get; set; } = 5;

    /// <summary>
    /// Whether the consolidator may run at all. It is a substrate call, so
    /// it is what a paid tier buys; threading itself is deterministic and
    /// runs everywhere.
    ///
    /// With it off, a candidate that is not a verbatim restatement mints a
    /// new thread rather than joining the nearest one. That is a deliberate
    /// false split: splits restore today's behaviour, which is duplicates,
    /// and merges make *now* wrong in a way read time cannot undo. An
    /// archive written this way is consolidated later by a backfill over an
    /// untouched log -- the judgment is disposable on purpose.
    /// </summary>
    public bool ConsolidatorEnabled { get; set; }

    /// <summary>
    /// Whether what was said is broken into standalone facts before it is
    /// indexed. A substrate call, so it is what a paid tier buys.
    ///
    /// With it off, an utterance is its own single fact, verbatim -- the
    /// archive as it behaved before the split. That degrades exactly where
    /// you would expect: a ten-sentence paste becomes one row with one
    /// centroid vector, which clears no read floor and, on the rare read it
    /// does win, spends a slot on nine facts nobody asked for. It is still
    /// the right floor for a tier with no call to spend, because ground truth
    /// is untouched and <see cref="FactBackfill"/> re-reads it later.
    /// </summary>
    public bool ExtractorEnabled { get; set; }

    /// <summary>
    /// Characters below which a single-sentence utterance is taken to be its
    /// own fact, with no call spent. "Rex is 4" needs no decontextualising,
    /// and asking a model to restate it can only make it longer or wrong.
    /// Multi-sentence input is always sent regardless of length.
    /// </summary>
    public int ExtractorMinLength { get; set; } = 120;

    /// <summary>
    /// Ceiling on facts from one utterance. Not a quality knob -- it is the
    /// stop on a model that has started listing rather than extracting, which
    /// is the failure mode that turns one paste into a hundred rows.
    /// </summary>
    public int ExtractorMaxFacts { get; set; } = 24;

    /// <summary>
    /// How many content words an utterance needs before it earns a row.
    ///
    /// One: a sentence with no content word in it -- "haha ok", "yeah",
    /// "hmm" -- makes no claim, so there is nothing a later read could want
    /// from it, and it is a vector competing for five slots. Anything above
    /// one starts discarding real short facts ("Rex is 4" is two), so this
    /// is a knob with one safe setting and a measurement to do before it
    /// moves. Zero keeps everything, which is the behaviour before this
    /// existed.
    /// </summary>
    public int MinContentWords { get; set; } = 1;

    /// <summary>
    /// Fallback top-k, used only if RuntimeKnobs has not been resolved. Live
    /// reads take their count from RuntimeKnobs.RecallDepth instead -- see
    /// UtteranceConsult -- so the knob panel's slider actually drives this
    /// number. Five, as benched, matches the knob's own default.
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Cosine floor for a row to be a candidate at read time. Well below the
    /// threading line: threading asks whether two utterances are the same
    /// subject, and reading asks whether one is worth showing.
    /// </summary>
    public double ReadMinScore { get; set; } = 0.55;

    /// <summary>
    /// MMR's balance between relevance and not-saying-it-twice. 0.7 measured
    /// at +0.14 distinct facts per read against plain ranking -- small, real,
    /// and free. Score jitter was tried on the same bench for the same
    /// purpose and lost 0.11, so diversity here is chosen, not randomised.
    /// </summary>
    public double DiversityLambda { get; set; } = 0.7;

    /// <summary>
    /// Document frequency at or below which a word counts as rare for the
    /// lexical lane. Batch 22 could not settle this at 78 statements because
    /// almost everything was rare; two is the value the v5 corpus was swept
    /// at and the value a young archive can live with, since a young archive
    /// is one where rarity admits nearly everything anyway.
    /// </summary>
    public int RareMax { get; set; } = 2;

    /// <summary>
    /// Weight on the lexical half when fusing with cosine. Kept small: the
    /// lexical lane exists to rescue the token queries embeddings are blind
    /// to, not to re-rank the ones they already answer.
    /// </summary>
    public double LexicalWeight { get; set; } = 0.15;
}
