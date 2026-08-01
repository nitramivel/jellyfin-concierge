using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Concierge.Core.Subtitles
{
    /// <summary>One subtitle cue, as it appeared in the file.</summary>
    /// <param name="Start">When it appears.</param>
    /// <param name="End">When it goes away.</param>
    /// <param name="Text">The raw text, newlines collapsed to spaces.</param>
    public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    /// <summary>
    /// Parses SRT, and only SRT.
    /// </summary>
    /// <remarks>
    /// <b>Concierge parses exactly one subtitle format, forever.</b> Jellyfin's
    /// <c>ISubtitleEncoder</c> is always asked for <c>"srt"</c>, and it converts
    /// ASS/SSA, mov_text, WebVTT and external files into it on the way out. That
    /// single decision deletes the multi-format parser this was originally going to
    /// be.
    /// <para>
    /// Forgiving on purpose: subtitle files in the wild are full of missing blank
    /// lines, stray BOMs, comma-versus-period decimal separators, and sequence
    /// numbers that restart halfway through. A cue that cannot be read is skipped
    /// rather than failing the file — losing one line is nothing, losing a film is
    /// not.
    /// </para>
    /// </remarks>
    public static class SrtParser
    {
        /// <summary>
        /// Parses an SRT document.
        /// </summary>
        /// <param name="content">The file's text.</param>
        /// <returns>The cues, in file order. Never throws.</returns>
        public static IReadOnlyList<Cue> Parse(string? content)
        {
            var cues = new List<Cue>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return cues;
            }

            // A BOM survives most reads and would otherwise break the first cue's
            // sequence number, silently costing the opening of every film.
            var text = content.TrimStart('﻿');
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            var i = 0;
            while (i < lines.Length)
            {
                // Find the next timing line. Anchoring on the arrow rather than the
                // sequence number means a file with broken or missing numbers still
                // parses completely.
                var arrow = lines[i].IndexOf("-->", StringComparison.Ordinal);
                if (arrow < 0)
                {
                    i++;
                    continue;
                }

                var timing = lines[i];
                i++;

                if (!TryParseTime(timing[..arrow], out var start)
                    || !TryParseTime(timing[(arrow + 3)..], out var end))
                {
                    continue;
                }

                var body = new List<string>();
                while (i < lines.Length
                    && !string.IsNullOrWhiteSpace(lines[i])
                    && lines[i].IndexOf("-->", StringComparison.Ordinal) < 0)
                {
                    body.Add(lines[i].Trim());
                    i++;
                }

                // A cue whose body is only its own sequence number is what a file with
                // no blank line separators looks like.
                if (body.Count > 0 && body[^1].Length <= 5 && int.TryParse(body[^1], out _))
                {
                    body.RemoveAt(body.Count - 1);
                }

                if (body.Count == 0)
                {
                    continue;
                }

                cues.Add(new Cue(start, end, string.Join(' ', body).Trim()));
            }

            return cues;
        }

        /// <summary>
        /// Parses an SRT timestamp: <c>00:01:23,456</c>, and the period-separated
        /// spelling some tools emit.
        /// </summary>
        private static bool TryParseTime(string value, out TimeSpan time)
        {
            time = default;
            var trimmed = value.Trim().Replace(',', '.');

            // Trailing position data — "00:00:01.000 X1:100 X2:200" — is legal and
            // must not defeat the parse.
            var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                trimmed = trimmed[..space];
            }

            if (trimmed.Length == 0)
            {
                return false;
            }

            var parts = trimmed.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                return false;
            }

            var hours = 0;
            var offset = 0;
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
                {
                    return false;
                }

                offset = 1;
            }

            if (!int.TryParse(parts[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || !double.TryParse(
                    parts[offset + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            time = new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
            return true;
        }
    }
}
