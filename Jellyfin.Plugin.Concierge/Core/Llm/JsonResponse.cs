using System;

namespace Jellyfin.Plugin.Concierge.Core.Llm
{
    /// <summary>
    /// Shared handling of the envelope a model wraps its JSON in.
    /// </summary>
    /// <remarks>
    /// Extracted so every parser that reads a model response agrees on what counts
    /// as the response. The scan below is subtler than it looks and was hardened
    /// against real model output; a second copy of it in another parser would be a
    /// second copy to get wrong.
    /// </remarks>
    public static class JsonResponse
    {
        /// <summary>
        /// Extracts the first complete top-level JSON object from model output,
        /// tolerating code fences and stray prose on either side of it.
        /// </summary>
        /// <remarks>
        /// This brace-matches forward from the opening brace rather than reaching for
        /// the last '}' in the buffer. Models routinely wrap the object in a ```json
        /// fence and add a sentence afterwards; taking the last brace swallows that
        /// trailing text and produces a parse error partway through otherwise-valid
        /// output. Braces inside string literals are ignored, so a '}' in a plot
        /// description cannot terminate the scan early.
        /// </remarks>
        /// <param name="text">The raw model output.</param>
        /// <returns>The JSON object, as text.</returns>
        /// <exception cref="FormatException">No complete JSON object is present.</exception>
        public static string ExtractObject(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            var start = text.IndexOf('{', StringComparison.Ordinal);
            if (start < 0)
            {
                throw new FormatException("Model response contains no JSON object.");
            }

            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            return text[start..(i + 1)];
                        }

                        break;
                    default:
                        break;
                }
            }

            // Ran out of input with the object still open — the usual cause is the
            // response being cut off by the output-token cap.
            throw new FormatException("Model response contains no complete JSON object.");
        }
    }
}
