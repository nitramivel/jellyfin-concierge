# Results — phase 2 — plan + re-rank

Measured against a live index. Path: **phase 2 — plan + re-rank**.

Ran 40 of 40 queries; 40 had an expected answer.


## Retrieval

| Group | Queries | recall@40 | recall@5 | recall@1 | MRR |
|---|---|---|---|---|---|
| Plot recall | 12 | 100% | 100% | 100% | 1.000 |
| Vibe | 12 | 100% | 75% | 50% | 0.626 |
| Constraints | 8 | 100% | 75% | 75% | 0.783 |
| **All** | **32** | **100%** | **84%** | **75%** | **0.806** |

## Router

1 of 8 title-shaped queries stayed on the free native path.

Sent to Concierge when they should not have been:

- `blade` → Both (matches names in the library, but no clear keyword winner)
- `bla` → Both (matches names in the library, but no clear keyword winner)
- `the of` → Both (matches names in the library, but no clear keyword winner)
- `fargo` → Both (short, and names nothing in the library)
- `lord of the rings` → Both (matches names in the library, but no clear keyword winner)
- `s` → Both (matches names in the library, but no clear keyword winner)
- `1917` → Concierge (carries a time or length constraint)

Of the 32 description-shaped queries, 0 were routed to native search.


## Cost and latency

- mean latency **3131ms**, p95 **8244ms**
- total cost for 40 queries: **$0.26506**
- mean cost per query: **$0.00663**

## Every query

| # | Group | Query | Expected | Rank | Route | ms |
|---|---|---|---|---|---|---|
| 1 | Plot recall | the one where they kill the guy's dog | John Wick | 1 | Concierge | 3168 |
| 2 | Plot recall | the one where he pays to have his ex erased from his memory | Eternal Sunshine of the Spotless Mind | 1 | Concierge | 2613 |
| 3 | Plot recall | the one where the narrator turns out to be the same person as the other guy | Fight Club | 1 | Concierge | 2308 |
| 4 | Plot recall | the movie where a guy's whole life is secretly a TV show | The Truman Show | 1 | Concierge | 2634 |
| 5 | Plot recall | the one where he sails to the edge of the world and hits a wall | The Truman Show | 1 | Concierge | 2052 |
| 6 | Plot recall | the one where the poor family all get jobs working for the rich family | Parasite | 1 | Concierge | 2349 |
| 7 | Plot recall | the one with the spinning top at the end | Inception | 1 | Concierge | 2319 |
| 8 | Plot recall | film where they're stuck reliving the same day | Groundhog Day; Edge of Tomorrow | 1 | Concierge | 2731 |
| 9 | Plot recall | the one shot to look like a single unbroken take | Birdman or (The Unexpected Virtue of Ignorance) | 1 | Concierge | 3894 |
| 10 | Plot recall | the one where you die seven days after watching the tape | The Ring | 1 | Concierge | 2139 |
| 11 | Plot recall | the one where the family is attacked by their own doubles | Us | 1 | Concierge | 2130 |
| 12 | Plot recall | the one where an hour on the planet costs them seven years | Interstellar | 1 | Concierge | 1953 |
| 13 | Vibe | dark and twisted | Hereditary; Midsommar; American Psycho; The Substance; Gummo | 1 | Concierge | 2466 |
| 14 | Vibe | nostalgic 90s classics | The Matrix; Pulp Fiction; Forrest Gump; The Shawshank Redemption; Groundhog Day | 1 | Concierge | 3134 |
| 15 | Vibe | something funny but not stupid, for a Sunday | The Grand Budapest Hotel; The Royal Tenenbaums; Ferris Bueller's Day Off; Superbad | 10 | Concierge | 3044 |
| 16 | Vibe | something gentle and cosy for a rainy afternoon | Ratatouille; Moonrise Kingdom; Spirited Away; Fantastic Mr. Fox | 6 | Concierge | 2559 |
| 17 | Vibe | a comfort watch | Ferris Bueller's Day Off; Groundhog Day; Ratatouille; Elf | 3 | Concierge | 2212 |
| 18 | Vibe | something bleak | Gummo; I'm Thinking of Ending Things; Hereditary; Midsommar | 1 | Concierge | 1774 |
| 19 | Vibe | stylish and cool, nothing heavy | Baby Driver; Scott Pilgrim vs. the World; True Romance | 1 | Concierge | 2317 |
| 20 | Vibe | genuinely frightening, not just gory | Hereditary; The Ring; Get Out; Cure | 1 | Concierge | 3126 |
| 21 | Vibe | something to put on with friends who aren't paying attention | Happy Gilmore; Step Brothers; Zoolander; Nacho Libre | 12 | Concierge | 3222 |
| 22 | Vibe | slow and beautiful, nothing happens | 2001: A Space Odyssey; I'm Thinking of Ending Things; Thirty Two Short Films About Glenn Gould | 2 | Concierge | 2730 |
| 23 | Vibe | feel-good but not saccharine | The Secret Life of Walter Mitty; The Perks of Being a Wallflower; Ratatouille | 3 | Concierge | 3002 |
| 24 | Vibe | tense the whole way through | Parasite; Gravity; Sunshine; Get Out | 1 | Concierge | 3096 |
| 25 | Constraints | 90s sci-fi under two hours | Men in Black; Back to the Future Part III | 1 | Concierge | 2006 |
| 26 | Constraints | something from the 80s | Back to the Future; Blade Runner; E.T. the Extra-Terrestrial; Ferris Bueller's Day Off; Planes, Trains and Automobiles | 1 | Concierge | 2495 |
| 27 | Constraints | a short comedy, under 100 minutes | Fantastic Mr. Fox; Elf; The Darjeeling Limited; Can't Buy Me Love | 8 | Concierge | 2581 |
| 28 | Constraints | recent thrillers | Saltburn; The Substance; The Housemaid; Obsession | 1 | Concierge | 2857 |
| 29 | Constraints | anything with Toni Collette | Hereditary; I'm Thinking of Ending Things | 1 | Concierge | 1457 |
| 30 | Constraints | a Wes Anderson film | The Grand Budapest Hotel; Moonrise Kingdom; The Royal Tenenbaums; Isle of Dogs; Asteroid City; Fantastic Mr. Fox; The Darjeeling Limited; The Life Aquatic with Steve Zissou | 1 | Both | 2975 |
| 31 | Constraints | animated, but not for children | Isle of Dogs; Fantastic Mr. Fox | 7 | Concierge | 2955 |
| 32 | Constraints | something made before 1970 | 2001: A Space Odyssey; A Hard Day's Night; Yellow Submarine; Help!; Magical Mystery Tour | 1 | Concierge | 3562 |
| 33 | Not-Concierge | blade | Native | — | Both | 3543 |
| 34 | Not-Concierge | bla | Native | — | Both | 3579 |
| 35 | Not-Concierge | the of | Native | — | Both | 5847 |
| 36 | Not-Concierge | fargo | Native | — | Both | 6463 |
| 37 | Not-Concierge | lord of the rings | Native | — | Both | 11394 |
| 38 | Not-Concierge | tarantino | Native | — | Native | 4 |
| 39 | Not-Concierge | s | Native | — | Both | 2287 |
| 40 | Not-Concierge | 1917 | Native — a title, not a year filter | — | Concierge | 8244 |
