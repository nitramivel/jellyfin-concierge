#!/usr/bin/env python3
"""Run the evaluation set against a live Concierge index and write the results.

    python3 eval/run-eval.py --url http://192.168.1.9:8096 --key "$JELLYFIN_API_KEY"

Reads queries.md, runs every labelled query through POST /Concierge/Search,
finds where each expected title landed, and writes results-<phase>.md.

Nothing here talks to a model directly. It exercises the plugin exactly as a
client would, so what it measures is what a user would actually get.

Standard library only — this has to run on the server with no pip install.
"""

import argparse
import json
import re
import statistics
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent

# Rows look like:  | 13 | dark and twisted | Se7en; Oldboy |
ROW = re.compile(r"^\|\s*(\d+)\s*\|(.+?)\|(.*?)\|\s*$")
GROUP = re.compile(r"^##\s+Group\s+\d+\s+[—-]\s+(.+?)\s*$")


class Query:
    def __init__(self, number, group, text, expected):
        self.number = number
        self.group = group
        self.text = text
        # Several titles may be right for one question. Semicolon-separated,
        # because commas turn up inside titles far too often to use as a
        # separator ("Crouching Tiger, Hidden Dragon").
        self.expected = [e.strip() for e in expected.split(";") if e.strip()]
        self.rank = None          # 1-based rank of the first expected hit
        self.route = None
        self.reason = ""
        self.duration_ms = 0
        self.cost = 0.0
        self.hits = []
        self.error = None

    @property
    def labelled(self):
        return bool(self.expected)

    @property
    def router_only(self):
        """Group 4 asks about routing, not ranking."""
        return self.group.lower().startswith("not-concierge")


def parse_queries(path):
    queries, group = [], "ungrouped"

    for line in path.read_text(encoding="utf-8").splitlines():
        heading = GROUP.match(line)
        if heading:
            group = heading.group(1)
            continue

        row = ROW.match(line)
        if not row:
            continue

        number, text, expected = row.groups()
        text = text.strip()

        # Skip the header separator and the header row itself.
        if not text or set(text) <= set("-: "):
            continue

        queries.append(Query(int(number), group, text, expected.strip()))

    return queries


def search(url, key, query, limit):
    body = json.dumps({"Query": query, "Limit": limit}).encode()
    request = urllib.request.Request(
        url.rstrip("/") + "/Concierge/Search",
        data=body,
        headers={
            "Content-Type": "application/json",
            "X-Emby-Token": key,
        },
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def matches(expected, name):
    """Loose title match, so "Se7en" finds "Se7en (1995)" and vice versa."""
    a = re.sub(r"[^a-z0-9]+", "", expected.lower())
    b = re.sub(r"[^a-z0-9]+", "", name.lower())
    return bool(a) and (a in b or b in a)


def recall_at(queries, k):
    scored = [q for q in queries if q.labelled and not q.router_only]
    if not scored:
        return None
    hit = sum(1 for q in scored if q.rank is not None and q.rank <= k)
    return hit / len(scored)


def mrr(queries):
    scored = [q for q in queries if q.labelled and not q.router_only]
    if not scored:
        return None
    return statistics.fmean(1.0 / q.rank if q.rank else 0.0 for q in scored)


def pct(value):
    return "—" if value is None else f"{value * 100:.0f}%"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True, help="Jellyfin base URL, e.g. http://192.168.1.9:8096")
    parser.add_argument("--key", required=True, help="Jellyfin API key")
    parser.add_argument("--limit", type=int, default=40, help="results per query (the recall@N ceiling)")
    parser.add_argument("--queries", default=str(HERE / "queries.md"))
    parser.add_argument("--out", default=str(HERE / "results-phase1.md"))
    parser.add_argument("--phase", default="phase 1 — free path (BM25 + vectors + fusion, no re-rank)")
    parser.add_argument("--dry-run", action="store_true", help="parse the query file and stop")
    args = parser.parse_args()

    # A key read from a file or an unset shell variable arrives with a trailing
    # newline, and urllib rejects that as a header value rather than trimming it.
    # Both eval runs on 2026-08-04 died this way and still wrote a full report.
    args.key = (args.key or "").strip()
    if not args.key:
        sys.exit("No API key. Dashboard -> API Keys, then pass it with --key.")

    queries = parse_queries(Path(args.queries))
    if not queries:
        sys.exit(f"No queries parsed from {args.queries}")

    labelled = [q for q in queries if q.labelled]
    unlabelled = [q for q in queries if not q.labelled and not q.router_only]

    print(f"{len(queries)} quer(y/ies) parsed, {len(labelled)} with an expected answer")
    if unlabelled:
        print(f"  {len(unlabelled)} still have a blank Expected column and are not scored:")
        for q in unlabelled[:8]:
            print(f"    {q.number:>2}. {q.text}")
        if len(unlabelled) > 8:
            print(f"    ... and {len(unlabelled) - 8} more")

    if args.dry_run:
        return

    print()
    for q in queries:
        try:
            started = time.monotonic()
            result = search(args.url, args.key, q.text, args.limit)
            q.duration_ms = int((time.monotonic() - started) * 1000)
        except urllib.error.HTTPError as e:
            q.error = f"HTTP {e.code}"
            print(f"  {q.number:>2}. {q.text[:44]:<44} ERROR {q.error}")

            # Auth will not fix itself on the next query, and forty rows of MISS
            # read like a search failure rather than a rejected key.
            if e.code in (401, 403):
                sys.exit(
                    f"\nHTTP {e.code} from {args.url} - the API key was refused. "
                    "Nothing was measured; no results file written.\n"
                    "Get a key from Dashboard -> API Keys and check it is the whole "
                    "string with no newline."
                )
            continue
        except Exception as e:  # noqa: BLE001 - a broken run should report, not crash
            q.error = str(e)
            print(f"  {q.number:>2}. {q.text[:44]:<44} ERROR {q.error}")
            continue

        q.route = result.get("Route", "?")
        q.reason = result.get("RouteReason", "")
        q.cost = float(result.get("CostUsd") or 0)
        q.hits = [h.get("Name", "") for h in result.get("Hits") or []]

        for index, name in enumerate(q.hits, start=1):
            if any(matches(e, name) for e in q.expected):
                q.rank = index
                break

        if q.router_only:
            verdict = "OK" if q.route == "Native" else f"WRONG ({q.route})"
        elif not q.labelled:
            verdict = f"unlabelled, top: {q.hits[0] if q.hits else '—'}"
        elif q.rank:
            verdict = f"rank {q.rank}"
        else:
            verdict = f"MISS (top: {q.hits[0] if q.hits else 'nothing'})"

        print(f"  {q.number:>2}. {q.text[:44]:<44} {verdict}")

    # ---- summary ----
    ran = [q for q in queries if q.error is None]
    if not ran:
        reasons = sorted({q.error for q in queries if q.error})
        sys.exit(
            "Every query failed, so there is nothing to report and "
            f"{args.out} was left alone.\n  " + "\n  ".join(reasons)
        )

    groups = {}
    for q in queries:
        groups.setdefault(q.group, []).append(q)

    lines = []
    lines.append(f"# Results — {args.phase}\n")
    lines.append(f"Measured against a live index. Path: **{args.phase}**.\n")
    lines.append(f"Ran {len([q for q in queries if q.error is None])} of {len(queries)} queries; "
                 f"{len(labelled)} had an expected answer.\n")

    lines.append("\n## Retrieval\n")
    lines.append("| Group | Queries | recall@40 | recall@5 | recall@1 | MRR |")
    lines.append("|---|---|---|---|---|---|")

    for name, items in groups.items():
        scored = [q for q in items if q.labelled and not q.router_only]
        if not scored:
            continue
        lines.append(
            f"| {name} | {len(scored)} | {pct(recall_at(scored, args.limit))} | "
            f"{pct(recall_at(scored, 5))} | {pct(recall_at(scored, 1))} | "
            f"{mrr(scored):.3f} |")

    overall = [q for q in queries if q.labelled and not q.router_only]
    if overall:
        lines.append(
            f"| **All** | **{len(overall)}** | **{pct(recall_at(overall, args.limit))}** | "
            f"**{pct(recall_at(overall, 5))}** | **{pct(recall_at(overall, 1))}** | "
            f"**{mrr(overall):.3f}** |")

    # ---- router ----
    router = [q for q in queries if q.router_only and q.error is None]
    if router:
        correct = sum(1 for q in router if q.route == "Native")
        lines.append(f"\n## Router\n")
        lines.append(f"{correct} of {len(router)} title-shaped queries stayed on the free native path.\n")
        wrong = [q for q in router if q.route != "Native"]
        if wrong:
            lines.append("Sent to Concierge when they should not have been:\n")
            for q in wrong:
                lines.append(f"- `{q.text}` → {q.route} ({q.reason})")

    routed = [q for q in queries if q.error is None and not q.router_only]
    if routed:
        native = sum(1 for q in routed if q.route == "Native")
        lines.append(f"\nOf the {len(routed)} description-shaped queries, {native} were routed to native search.\n")

    # ---- cost and latency ----
    timed = [q for q in queries if q.error is None]
    if timed:
        durations = sorted(q.duration_ms for q in timed)
        p95 = durations[min(len(durations) - 1, int(len(durations) * 0.95))]
        lines.append("\n## Cost and latency\n")
        lines.append(f"- mean latency **{statistics.fmean(durations):.0f}ms**, p95 **{p95}ms**")
        lines.append(f"- total cost for {len(timed)} queries: **${sum(q.cost for q in timed):.5f}**")
        lines.append(f"- mean cost per query: **${statistics.fmean([q.cost for q in timed]):.5f}**")

    # ---- per query ----
    lines.append("\n## Every query\n")
    lines.append("| # | Group | Query | Expected | Rank | Route | ms |")
    lines.append("|---|---|---|---|---|---|---|")
    for q in queries:
        rank = "—" if q.router_only else (str(q.rank) if q.rank else ("MISS" if q.labelled else "—"))
        expected = "; ".join(q.expected) if q.expected else "—"
        lines.append(
            f"| {q.number} | {q.group} | {q.text} | {expected} | {rank} | "
            f"{q.error or q.route} | {q.duration_ms} |")

    misses = [q for q in queries if q.labelled and not q.router_only and not q.rank]
    if misses:
        lines.append("\n## Misses — read these before changing anything\n")
        lines.append("An expected item that never reached the top 40 is a **retrieval** failure, and the lever "
                     "is enrichment. One that reached the shortlist but ranked low is a **ranking** failure, and "
                     "the lever is the phase-2 re-rank prompt. From a results page the two look identical.\n")
        for q in misses:
            lines.append(f"- `{q.text}` — expected {'; '.join(q.expected)}; "
                         f"top result was {q.hits[0] if q.hits else 'nothing'}")

    if unlabelled:
        lines.append(f"\n## Not yet labelled\n")
        lines.append(f"{len(unlabelled)} quer(y/ies) have no expected answer and were not scored.\n")

    Path(args.out).write_text("\n".join(lines) + "\n", encoding="utf-8")

    print()
    print(f"recall@{args.limit}: {pct(recall_at(overall, args.limit))}   "
          f"recall@5: {pct(recall_at(overall, 5))}   "
          f"recall@1: {pct(recall_at(overall, 1))}")
    print(f"Wrote {args.out}")


if __name__ == "__main__":
    main()
