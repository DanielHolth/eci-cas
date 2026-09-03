# Retrieval probe

Throwaway rig for one question, asked before the retrieval rewrite is worth
starting: **does cosine over pre-embedded fact rows find the row a real
question is asking about?**

Today retrieval is three serial LLM hops — Librarian picks pairs, Recall
picks rows, Intent answers. The proposed replacement is one hop: embed the
query, cosine over pre-embedded rows, hand top-K straight to Intent. That
only works if the vectors can find the right row without a model reading it,
and the reason to doubt it is that a question rarely shares words with the
fact that answers it. "What's my name?" against `this/user/name = Daniel`
matches on almost nothing — the name is the answer, not the question.

So the probe scores three representations of the same row separately:

| | what is embedded |
|---|---|
| `raw` | `son / marcus holth birthdate = 2020-08-28` — the string Recall's prompt already shows |
| `path` | the full five-part address plus the value |
| `gloss` | a written description of what the row answers, supplied per address |

`raw` is the honest baseline. If `gloss` does not beat it clearly, one-hop
retrieval is dead and the afternoon was cheap.

## Finding your addresses

The questions file needs the address each question should retrieve, and
those only exist inside the parquet files. This prints every one, and
needs no weights:

```
dotnet run --project tools/EciCas.RetrievalProbe -- --list <archive dir>
```

One row per line, `address = value`. Copy the addresses you want to ask
about into the `expect` fields.

## Running

Needs the embedding weights (`./scripts/get-embedding-model.ps1`) and a real
archive — not a fresh build output, which holds one seeded row.

```
dotnet run --project tools/EciCas.RetrievalProbe -- --archive <path>/bin/Debug/net10.0/archive --questions questions.json --model models/embedding/model.onnx --vocab models/embedding/vocab.txt
```

Add `--glosses glosses.json` (`{ "<address>": "<what it answers>" }`) to
score the third row. Rows with no gloss drop out of that run rather than
falling back to the path, so a gloss score is never half a path score.

## Asymmetric models

`all-MiniLM-L6-v2` is symmetric — trained so two sentences meaning the same
thing score high, which is not the question-to-fact case. E5 and BGE are
trained for it and expect prefixes:

```
--query-prefix "query: " --passage-prefix "passage: "
```

Worth a second run before concluding anything about vectors in general: a
bad number from a symmetric model measures the model, not the idea.

## Reading the result

`hit@1` and `hit@3` are what the design rides on — top-K goes to Intent
unfiltered, so a fact ranked fourth is a fact the persona does not have.
`MRR` separates "just missed" from "nowhere near". Every question not ranked
first is printed with what won instead, which is usually where the actual
insight is.

The scoring arithmetic is tested in `tests/EciCas.Tests/Tools/ScoringTests.cs`;
the probe itself is only runnable where the weights and an archive are.
