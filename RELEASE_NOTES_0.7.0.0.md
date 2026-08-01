# Concierge 0.7.0.0 — phase 3: search what they said

Quote a line and get the film **and the minute**. Reads the subtitles already
inside your own files: **no model, no embeddings, no money** — only CPU, once,
per file.

## Why this one matters more than it looks

`im your freaky nicki` returned *Freaky Friday* instead of *Obsession*, and the
reason was not a bug. *Obsession* is a 2026 film, so the enrichment model has
never heard of it and correctly returned "I don't know this one" rather than
inventing a plot. Ten of your 263 items are in that position, all of them from
2025 or 2026.

Those films are findable only by their title and overview — no character names,
no quotes, no mood. **Quote search is the fix, and it is the only one that
scales**, because the words come out of the subtitle file rather than out of a
model's memory. It does not care how new a film is or whether anything has ever
been trained on it.

## How to turn it on

**Scheduled Tasks → Read dialogue for Concierge quote search.** Films first, and
that is deliberate: about 140 items with usable text subtitles, finishing in
minutes. Then search with quotation marks:

```
"one wish willow"
"say hello to my little friend"
```

Results carry the line, the two or three lines around it, and a timestamp
**five seconds before it is said**, so playback starts on the run-up rather than
mid-word.

## What it does not index, and why that is visible

The settings page now reports coverage and groups the failures by cause, because
one of them you can fix for free:

- **Image-only subtitles** (PGS, VobSub) cannot be read without OCR — a
  dependency, a GPU and an error rate. Downloading an external English track for
  those items converts each one into a text track Concierge can read.
- **Forced tracks are refused outright.** A forced track carries a few dozen
  foreign-language lines, so indexing one *looks like success* while producing a
  film nobody can find by anything actually said in it.
- Hearing-impaired tracks are used when they are all there is, with their
  `[door creaks]` annotations stripped — those are descriptions of sound, and
  left in they would make a search for something tense rank whichever film has
  the most ominous music in it.

## Details worth knowing

- **Resumable.** Every item is written the moment it is read, so stopping the
  task keeps everything done so far. It will be interrupted — installing any
  plugin tears the host down — and that is designed for rather than hoped about.
- **Re-running is nearly free.** Staleness is keyed on stream, path, size and
  modified time, so an unchanged library costs a few seconds of stat calls.
- **Misremembered quotes still work.** "Luke, I am your father" is not a line in
  any Star Wars film; character-trigram matching catches near misses, still for
  nothing.
- **Lyrics survive.** The plan said strip note-wrapped cues wholesale, but
  `♪ ominous music ♪` and `♪ Let it go ♪` arrive identically marked and only the
  first is a sound description. The content is dropped only when it reads as one.
- **Episodes are off by default.** Films are ~73,000 searchable windows; the
  whole library is ~850,000. That second number is where a real full-text
  database would beat the index built in here, and it is worth asking before
  relying on it at that scale.

258 tests.

## Upgrading

Drop-in, and it changes nothing until you run the extraction task. No API keys
involved and nothing is spent.
