# Results — phase 1 — free path (BM25 + vectors + fusion, no re-rank)

Measured against a live index. Path: **phase 1 — free path (BM25 + vectors + fusion, no re-rank)**.

Ran 40 of 40 queries; 40 had an expected answer.


## Retrieval

| Group | Queries | recall@40 | recall@5 | recall@1 | MRR |
|---|---|---|---|---|---|
| Plot recall | 12 | 100% | 100% | 100% | 1.000 |
| Vibe | 12 | 100% | 58% | 25% | 0.394 |
| Constraints | 8 | 88% | 50% | 38% | 0.464 |
| **All** | **32** | **97%** | **72%** | **56%** | **0.639** |

## Router

6 of 8 title-shaped queries stayed on the free native path.

Sent to Concierge when they should not have been:

- `fargo` → Both (short, and names nothing in the library)
- `1917` → Concierge (carries a time or length constraint)

Of the 32 description-shaped queries, 1 were routed to native search.


## Cost and latency

- mean latency **138ms**, p95 **346ms**
- total cost for 40 queries: **$0.00000**
- mean cost per query: **$0.00000**

## Every query

| # | Group | Query | Expected | Rank | Route | ms |
|---|---|---|---|---|---|---|
| 1 | Plot recall | the one where they kill the guy's dog | John Wick | 1 | Concierge | 6 |
| 2 | Plot recall | the one where he pays to have his ex erased from his memory | Eternal Sunshine of the Spotless Mind | 1 | Concierge | 1 |
| 3 | Plot recall | the one where the narrator turns out to be the same person as the other guy | Fight Club | 1 | Concierge | 1 |
| 4 | Plot recall | the movie where a guy's whole life is secretly a TV show | The Truman Show | 1 | Concierge | 1 |
| 5 | Plot recall | the one where he sails to the edge of the world and hits a wall | The Truman Show | 1 | Concierge | 0 |
| 6 | Plot recall | the one where the poor family all get jobs working for the rich family | Parasite | 1 | Concierge | 0 |
| 7 | Plot recall | the one with the spinning top at the end | Inception | 1 | Concierge | 346 |
| 8 | Plot recall | film where they're stuck reliving the same day | Groundhog Day; Edge of Tomorrow | 1 | Concierge | 216 |
| 9 | Plot recall | the one shot to look like a single unbroken take | Birdman or (The Unexpected Virtue of Ignorance) | 1 | Concierge | 273 |
| 10 | Plot recall | the one where you die seven days after watching the tape | The Ring | 1 | Concierge | 155 |
| 11 | Plot recall | the one where the family is attacked by their own doubles | Us | 1 | Concierge | 347 |
| 12 | Plot recall | the one where an hour on the planet costs them seven years | Interstellar | 1 | Concierge | 290 |
| 13 | Vibe | dark and twisted | Hereditary; Midsommar; American Psycho; The Substance; Gummo | 20 | Concierge | 166 |
| 14 | Vibe | nostalgic 90s classics | The Matrix; Pulp Fiction; Forrest Gump; The Shawshank Redemption; Groundhog Day | 11 | Concierge | 148 |
| 15 | Vibe | something funny but not stupid, for a Sunday | The Grand Budapest Hotel; The Royal Tenenbaums; Ferris Bueller's Day Off; Superbad | 23 | Concierge | 169 |
| 16 | Vibe | something gentle and cosy for a rainy afternoon | Ratatouille; Moonrise Kingdom; Spirited Away; Fantastic Mr. Fox | 3 | Concierge | 176 |
| 17 | Vibe | a comfort watch | Ferris Bueller's Day Off; Groundhog Day; Ratatouille; Elf | 15 | Concierge | 191 |
| 18 | Vibe | something bleak | Gummo; I'm Thinking of Ending Things; Hereditary; Midsommar | 4 | Concierge | 209 |
| 19 | Vibe | stylish and cool, nothing heavy | Baby Driver; Scott Pilgrim vs. the World; True Romance | 1 | Concierge | 218 |
| 20 | Vibe | genuinely frightening, not just gory | Hereditary; The Ring; Get Out; Cure | 2 | Concierge | 172 |
| 21 | Vibe | something to put on with friends who aren't paying attention | Happy Gilmore; Step Brothers; Zoolander; Nacho Libre | 17 | Concierge | 153 |
| 22 | Vibe | slow and beautiful, nothing happens | 2001: A Space Odyssey; I'm Thinking of Ending Things; Thirty Two Short Films About Glenn Gould | 1 | Concierge | 156 |
| 23 | Vibe | feel-good but not saccharine | The Secret Life of Walter Mitty; The Perks of Being a Wallflower; Ratatouille | 3 | Concierge | 230 |
| 24 | Vibe | tense the whole way through | Parasite; Gravity; Sunshine; Get Out | 1 | Concierge | 235 |
| 25 | Constraints | 90s sci-fi under two hours | Men in Black; Back to the Future Part III | 1 | Concierge | 171 |
| 26 | Constraints | something from the 80s | Back to the Future; Blade Runner; E.T. the Extra-Terrestrial; Ferris Bueller's Day Off; Planes, Trains and Automobiles | 1 | Concierge | 200 |
| 27 | Constraints | a short comedy, under 100 minutes | Fantastic Mr. Fox; Elf; The Darjeeling Limited; Can't Buy Me Love | MISS | Concierge | 171 |
| 28 | Constraints | recent thrillers | Saltburn; The Substance; The Housemaid; Obsession | 29 | Concierge | 216 |
| 29 | Constraints | anything with Toni Collette | Hereditary; I'm Thinking of Ending Things | 1 | Concierge | 202 |
| 30 | Constraints | a Wes Anderson film | The Grand Budapest Hotel; Moonrise Kingdom; The Royal Tenenbaums; Isle of Dogs; Asteroid City; Fantastic Mr. Fox; The Darjeeling Limited; The Life Aquatic with Steve Zissou | 2 | Native | 1 |
| 31 | Constraints | animated, but not for children | Isle of Dogs; Fantastic Mr. Fox | 16 | Concierge | 162 |
| 32 | Constraints | something made before 1970 | 2001: A Space Odyssey; A Hard Day's Night; Yellow Submarine; Help!; Magical Mystery Tour | 9 | Concierge | 141 |
| 33 | Not-Concierge | blade | Native | — | Native | 1 |
| 34 | Not-Concierge | bla | Native | — | Native | 1 |
| 35 | Not-Concierge | the of | Native | — | Native | 1 |
| 36 | Not-Concierge | fargo | Native | — | Both | 168 |
| 37 | Not-Concierge | lord of the rings | Native | — | Native | 1 |
| 38 | Not-Concierge | tarantino | Native | — | Native | 1 |
| 39 | Not-Concierge | s | Native | — | Native | 1 |
| 40 | Not-Concierge | 1917 | Native — a title, not a year filter | — | Concierge | 216 |

## Misses — read these before changing anything

An expected item that never reached the top 40 is a **retrieval** failure, and the lever is enrichment. One that reached the shortlist but ranked low is a **ranking** failure, and the lever is the phase-2 re-rank prompt. From a results page the two look identical.

- `a short comedy, under 100 minutes` — expected Fantastic Mr. Fox; Elf; The Darjeeling Limited; Can't Buy Me Love; top result was Thirty Two Short Films About Glenn Gould
