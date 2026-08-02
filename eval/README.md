# Evaluating search quality

Search quality must be measured against labelled queries. Prompt changes tend to
improve the query that motivated them while quietly regressing others; the files
in this directory are the guard against that.

## Current state

[queries.md](queries.md) contains 40 queries in four groups, but the expected
answers are still blank because they must name items in the owner's library.
[results-phase1.md](results-phase1.md) records fixture-library mechanism tests,
not a real-library quality benchmark.

Do not publish recall, ranking, or quality claims until the expected answers are
filled and the live evaluation has run.

## Labelling the set

For each query, put the title you would be annoyed not to see in the `Expected`
column. Separate multiple acceptable titles with semicolons. Leave a query
blank when the library has no defensible answer; the harness skips it.

The groups diagnose different parts of the system:

| Group | What it tests |
|---|---|
| Plot recall | Enrichment and semantic retrieval |
| Vibe | Themes, semantic retrieval, and ranking |
| Constraints | Planning and fail-open filters |
| Not-Concierge | Correct free/native routing |

## Running the evaluation

Build the index first, then run:

```bash
python3 eval/run-eval.py \
  --url http://your-jellyfin-server:8096 \
  --key "$JELLYFIN_API_KEY"
```

Use `--dry-run` to validate edits without making search requests. The script
uses `POST /Concierge/Search` and writes the result report itself.

An API key can be created under **Dashboard → API Keys**. Never commit it or
paste it into results.

## Reading the report

- **Recall@40** asks whether retrieval put the right item in the shortlist. A
  miss here cannot be fixed by the re-ranker.
- **Recall@5**, **recall@1**, and **MRR** describe ordering once the candidate
  is retrievable.
- **Router split** shows how often the free/native path handled the query.
- **Cost and latency** must be read alongside quality; a better ranking that is
  too slow or expensive may still be the wrong configuration.

The first controlled comparison should use the same labelled set twice: once
with enrichment disabled and once with enrichment enabled. That delta measures
whether paid enrichment is carrying recall or merely decorating results.

Commit generated reports only when they state the exact configuration, models,
index generation, and date used. Fixture results and real-library results must
remain clearly labelled.
