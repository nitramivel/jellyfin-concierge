using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// The staleness key: what makes a nightly rebuild cost approximately nothing.
    /// </summary>
    /// <remarks>
    /// An item whose hash is unchanged is never re-embedded and never re-enriched,
    /// so a rebuild pays only for what actually changed. A metadata refresh across
    /// the whole library costs the handful of items the refresh touched.
    /// <para>
    /// Taken over <see cref="ItemDocument.RenderSourceText"/> — the library fields
    /// only. That is the trap §5.3 names: hash the source, so an item whose title
    /// and overview were rewritten cannot keep an enrichment written about what it
    /// used to be.
    /// </para>
    /// </remarks>
    public static class DocumentHash
    {
        /// <summary>
        /// Hashes a document's source text.
        /// </summary>
        /// <param name="document">The document.</param>
        /// <returns>A stable lowercase hex digest.</returns>
        public static string Of(ItemDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            return Of(document.RenderSourceText());
        }

        /// <summary>
        /// Hashes arbitrary text.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns>A stable lowercase hex digest.</returns>
        public static string Of(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            // SHA-256 rather than string.GetHashCode: this is persisted across
            // restarts and compared against what a previous process wrote, and
            // GetHashCode is explicitly not stable between runs.
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(digest).ToUpperInvariant()[..32].ToLowerInvariant();
        }
    }
}
