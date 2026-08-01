using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Concierge.Configuration;

namespace Jellyfin.Plugin.Concierge.Core.Llm
{
    /// <summary>
    /// The embedding half of the profile system: a near-mirror of
    /// <see cref="ModelProfiles"/>, with the prefix defaulting that only embeddings
    /// need.
    /// </summary>
    public static class EmbeddingProfiles
    {
        /// <summary>
        /// The result of normalizing the embedding profile list.
        /// </summary>
        /// <param name="Profiles">The profiles, each with a non-empty unique id.</param>
        /// <param name="DefaultProfileId">The default profile's id, or empty when there are no profiles.</param>
        /// <param name="Changed">Whether normalization altered anything.</param>
        public sealed record NormalizedEmbeddingProfiles(
            IReadOnlyList<EmbeddingProfile> Profiles,
            string DefaultProfileId,
            bool Changed);

        /// <summary>
        /// Normalizes the embedding profile list: ids, names, per-model prefix
        /// defaults, and a default id that names something real.
        /// </summary>
        /// <param name="config">The plugin configuration. Profiles may be repaired in place.</param>
        /// <returns>The normalized list and default id.</returns>
        public static NormalizedEmbeddingProfiles Normalize(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var profiles = (config.EmbeddingProfiles ?? Array.Empty<EmbeddingProfile>())
                .Where(p => p is not null)
                .ToList();
            var changed = false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Id) || !seen.Add(profile.Id))
                {
                    profile.Id = NewId();
                    seen.Add(profile.Id);
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    profile.Name = Describe(profile);
                    changed = true;
                }

                if (ApplyDefaultPrefixes(profile))
                {
                    changed = true;
                }
            }

            var defaultId = config.DefaultEmbeddingProfileId ?? string.Empty;
            if (profiles.Count == 0)
            {
                if (defaultId.Length > 0)
                {
                    defaultId = string.Empty;
                    changed = true;
                }
            }
            else if (!profiles.Any(p => string.Equals(p.Id, defaultId, StringComparison.Ordinal)))
            {
                defaultId = profiles[0].Id;
                changed = true;
            }

            return new NormalizedEmbeddingProfiles(profiles, defaultId, changed);
        }

        /// <summary>
        /// Resolves an embedding profile by id, falling back to the default.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="profileId">The wanted profile id; blank means the default.</param>
        /// <returns>The resolved profile.</returns>
        /// <exception cref="InvalidOperationException">No embedding profile is configured.</exception>
        public static EmbeddingProfile Resolve(PluginConfiguration config, string? profileId)
            => Resolve(Normalize(config), profileId);

        /// <summary>
        /// Resolves an embedding profile out of an already-normalized list.
        /// </summary>
        /// <param name="normalized">The normalized profile list.</param>
        /// <param name="profileId">The wanted profile id; blank means the default.</param>
        /// <returns>The resolved profile.</returns>
        /// <exception cref="InvalidOperationException">No embedding profile is configured.</exception>
        public static EmbeddingProfile Resolve(NormalizedEmbeddingProfiles normalized, string? profileId)
        {
            ArgumentNullException.ThrowIfNull(normalized);

            if (normalized.Profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "Concierge: no embedding profile configured. Add one on the Models tab of the plugin settings.");
            }

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                var match = normalized.Profiles.FirstOrDefault(
                    p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
            }

            return normalized.Profiles.First(
                p => string.Equals(p.Id, normalized.DefaultProfileId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Fills in the query/document markers for models known to want them.
        /// </summary>
        /// <remarks>
        /// Only ever fills a profile where <em>both</em> prefixes are blank, so an
        /// owner who deliberately cleared one — a legitimate choice, since a few
        /// fine-tunes drop the asymmetry — does not get it silently restored on the
        /// next read. Getting this wrong has no error and no symptom other than
        /// worse results, which is why the defaults exist at all rather than being
        /// left to whoever reads the model card.
        /// </remarks>
        /// <param name="profile">The profile to default.</param>
        /// <returns>Whether anything was filled in.</returns>
        public static bool ApplyDefaultPrefixes(EmbeddingProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (!string.IsNullOrEmpty(profile.QueryPrefix) || !string.IsNullOrEmpty(profile.DocumentPrefix))
            {
                return false;
            }

            var defaults = DefaultPrefixesFor(profile.Model);
            if (defaults is null)
            {
                return false;
            }

            profile.QueryPrefix = defaults.Value.Query;
            profile.DocumentPrefix = defaults.Value.Document;
            return true;
        }

        /// <summary>
        /// The markers a known embedding model was trained with, or null when the
        /// model is symmetric or unrecognised.
        /// </summary>
        /// <param name="model">The model identifier.</param>
        /// <returns>The query and document prefixes, or null.</returns>
        public static (string Query, string Document)? DefaultPrefixesFor(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            // Matched loosely on purpose: these arrive as "bge-m3", "bge-m3:latest",
            // "BAAI/bge-m3" and half a dozen other spellings depending on whether
            // they came from Ollama, LM Studio or a Hugging Face path.
            var m = model.ToUpperInvariant();

            // E5 and its descendants, including multilingual-e5-* and bge-m3, which
            // is trained in the same style.
            if (m.Contains("E5", StringComparison.Ordinal) || m.Contains("BGE-M3", StringComparison.Ordinal))
            {
                return ("query: ", "passage: ");
            }

            // Nomic uses its own task-prefix vocabulary rather than query/passage.
            if (m.Contains("NOMIC-EMBED", StringComparison.Ordinal))
            {
                return ("search_query: ", "search_document: ");
            }

            // OpenAI, Voyage and Google are symmetric — a prefix here would be text
            // the model was never trained to strip, and would only add noise.
            return null;
        }

        private static string Describe(EmbeddingProfile profile)
            => string.IsNullOrWhiteSpace(profile.Model)
                ? profile.Provider.ToString()
                : string.Create(CultureInfo.InvariantCulture, $"{profile.Provider} {profile.Model}");

        private static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }
}
