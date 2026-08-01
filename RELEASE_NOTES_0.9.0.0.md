# Concierge 0.9.0.0 — a query log you can actually break down

## The old log was destroying your history

It kept the **last 200 searches** in one file and rewrote the whole thing on
every search. Search 201 destroyed search 1. No usage question spanning more than
a couple of days could ever be answered, because the answer had already been
overwritten.

Searches are now appended, one line each, to **one file per calendar month**,
kept for **two years**:

```
data/concierge/queries/queries-2026-08.jsonl
```

Three things follow, and each was the reason:

- **Append, never rewrite.** Recording a search is one line however much history
  exists, so the log grows without the cost of writing it growing too.
- **Crash-tolerant by construction.** A process killed mid-write leaves one bad
  final line, which the reader skips. The same accident against a single JSON
  array loses every record in the file.
- **Month-partitioned.** "What did August cost" reads one file, and retention is
  deleting old files rather than rewriting surviving ones.

Your existing 67 records are **imported automatically** on first write — the old
`runs.json` is renamed rather than deleted, so you can check the import.

## Usage and cost, broken down

New panel on the settings page, and `GET /Concierge/Usage?months=3` behind it:

- **Totals** — searches, cost, cost per search, cost per *paid* search, tokens in
  and out, mean and **p95** latency.
- **The router's report card** — what share of searches were answered for
  nothing. This is the number the plan says moves your bill more than model
  choice does, so it is called out rather than buried, and warned about when it
  drops below 40%.
- **By month, by pass, by model, by route, by user.**

Two details that matter for trusting it: every total is **summed call by call**,
never estimated from token counts at a single rate — so a search that ran a cheap
plan model and an expensive re-rank model appears correctly as two lines. And a
call bucket counts *distinct searches*, not calls, so summing the column across
buckets cannot double-count a search that made two.

Cached searches and free searches are counted separately from paid ones. Counting
cache hits as ordinary searches would understate what the cache saves and
overstate what a search costs.

## You can now keep the numbers without keeping the words

New setting: **record the text of each search**, on by default.

A search you cannot see is a search you cannot debug — every diagnosis in this
plugin's history started by reading what somebody actually typed. But two years
of retention makes this a standing record of what everyone in the house searched
for, which is a different thing from a cost log and deserves its own decision.

Turning it off keeps **every number** — timing, tokens, cost, model, route, which
user — and drops only the words. Usage breakdowns are completely unaffected.

## Also recorded now

Whether a search was cached, whether it was re-ranked, and how many lines of
dialogue matched. All three were visible at request time and lost by the evening.

278 tests.

## Upgrading

Drop-in. The import happens on your next search.
