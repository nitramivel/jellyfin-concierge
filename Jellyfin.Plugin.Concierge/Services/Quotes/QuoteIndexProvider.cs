using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Core.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Quotes
{
    /// <summary>A line of dialogue, ready to show.</summary>
    /// <param name="ItemId">The film or episode.</param>
    /// <param name="Title">Its title.</param>
    /// <param name="Line">The matched line, as it appeared on screen.</param>
    /// <param name="Context">The lines around it, including the match itself.</param>
    /// <param name="Position">Where it is.</param>
    /// <param name="ResumeTicks">
    /// Where playback should start: five seconds before the line, so the viewer hears
    /// the run-up rather than landing mid-word.
    /// </param>
    /// <param name="Exact">Whether the phrase was found word for word.</param>
    /// <param name="Score">1.0 for verbatim, lower for a near miss.</param>
    public sealed record QuoteResult(
        Guid ItemId,
        string Title,
        string Line,
        IReadOnlyList<string> Context,
        TimeSpan Position,
        long ResumeTicks,
        bool Exact,
        double Score);

    /// <summary>
    /// Holds the phrase index in memory and answers quote searches.
    /// </summary>
    /// <remarks>
    /// Loaded once and cached, because building it means reading every extracted
    /// track off disk — fine as a startup cost, absurd per query.
    /// </remarks>
    public sealed class QuoteIndexProvider
    {
        /// <summary>How far before the line playback should start.</summary>
        public static readonly TimeSpan ResumeLeadIn = TimeSpan.FromSeconds(5);

        private readonly IQuoteStore _store;
        private readonly ILogger<QuoteIndexProvider> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private PhraseIndex? _index;
        private Dictionary<Guid, QuoteTrack>? _tracks;
        private List<QuoteWindow>? _windows;

        public QuoteIndexProvider(IQuoteStore store, ILogger<QuoteIndexProvider> logger)
        {
            _store = store;
            _logger = logger;
        }

        /// <summary>Gets how many windows are searchable, or 0 before loading.</summary>
        public int WindowCount => _index?.WindowCount ?? 0;

        /// <summary>Gets how many items have dialogue indexed.</summary>
        public int ItemCount => _tracks?.Count ?? 0;

        /// <summary>Drops the cached index so the next search rebuilds it.</summary>
        public void Invalidate()
        {
            _index = null;
            _tracks = null;
            _windows = null;
        }

        /// <summary>
        /// Searches the dialogue.
        /// </summary>
        /// <param name="phrase">What the searcher typed, with surrounding quotes stripped.</param>
        /// <param name="limit">How many results.</param>
        /// <param name="windowWords">Window size, for rebuilding on load.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Hits with context and timestamps. Empty when nothing is indexed.</returns>
        public async Task<IReadOnlyList<QuoteResult>> SearchAsync(
            string phrase,
            int limit,
            int windowWords,
            CancellationToken cancellationToken)
        {
            await EnsureLoadedAsync(windowWords, cancellationToken).ConfigureAwait(false);

            if (_index is null || _tracks is null || _windows is null)
            {
                return [];
            }

            var results = new List<QuoteResult>();

            foreach (var hit in _index.Search(phrase, limit))
            {
                if (!_tracks.TryGetValue(hit.ItemId, out var track))
                {
                    continue;
                }

                var window = _windows[hit.Window];
                var cues = track.Cues
                    .Select(c => new CleanCue(
                        TimeSpan.FromTicks(c.StartTicks), TimeSpan.FromTicks(c.EndTicks), c.Text, c.Raw))
                    .ToList();

                var cueIndex = LocateLine(cues, window, phrase);
                var line = cueIndex >= 0 && cueIndex < cues.Count ? cues[cueIndex].Raw : window.Text;
                var position = cueIndex >= 0 && cueIndex < cues.Count ? cues[cueIndex].Start : window.Start;

                results.Add(new QuoteResult(
                    hit.ItemId,
                    track.Title,
                    line,
                    CueWindowing.Context(cues, cueIndex < 0 ? window.FirstCue : cueIndex),
                    position,
                    Math.Max(0, (position - ResumeLeadIn).Ticks),
                    hit.Exact,
                    hit.Score));
            }

            return results;
        }

        /// <summary>
        /// Narrows a window down to the cue that actually carries the phrase.
        /// </summary>
        /// <remarks>
        /// A window is forty words and a quote is five, so seeking to the window's
        /// start could drop the viewer half a minute early. This finds the line
        /// itself, which is what the timestamp is for.
        /// </remarks>
        private static int LocateLine(IReadOnlyList<CleanCue> cues, QuoteWindow window, string phrase)
        {
            var needle = string.Join(' ', Core.Retrieval.Tokenizer.Tokenize(phrase));
            if (needle.Length == 0)
            {
                return window.FirstCue;
            }

            var last = Math.Min(cues.Count, window.FirstCue + window.CueCount);

            // Exact first: the cue containing the whole phrase.
            for (var i = window.FirstCue; i < last; i++)
            {
                var text = string.Join(' ', Core.Retrieval.Tokenizer.Tokenize(cues[i].Text));
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            // A quote split across two cues by line wrapping: take the cue sharing the
            // most words with it.
            var wanted = needle.Split(' ');
            var best = window.FirstCue;
            var bestShared = 0;

            for (var i = window.FirstCue; i < last; i++)
            {
                var tokens = Core.Retrieval.Tokenizer.Tokenize(cues[i].Text).ToHashSet(StringComparer.Ordinal);
                var shared = wanted.Count(tokens.Contains);
                if (shared > bestShared)
                {
                    bestShared = shared;
                    best = i;
                }
            }

            return best;
        }

        private async Task EnsureLoadedAsync(int windowWords, CancellationToken cancellationToken)
        {
            if (_index is not null)
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_index is not null)
                {
                    return;
                }

                var tracks = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                if (tracks.Count == 0)
                {
                    // Nothing extracted yet. Leave the cache empty so the next search
                    // looks again rather than caching an absence forever.
                    return;
                }

                var windows = new List<QuoteWindow>();
                foreach (var track in tracks)
                {
                    var cues = track.Cues
                        .Select(c => new CleanCue(
                            TimeSpan.FromTicks(c.StartTicks), TimeSpan.FromTicks(c.EndTicks), c.Text, c.Raw))
                        .ToList();

                    windows.AddRange(CueWindowing.Build(track.ItemId, cues, windowWords));
                }

                _tracks = tracks.ToDictionary(t => t.ItemId);
                _windows = windows;
                _index = PhraseIndex.Build(windows);

                _logger.LogInformation(
                    "Concierge: dialogue index ready — {Items} item(s), {Windows} searchable window(s)",
                    tracks.Count,
                    windows.Count);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
