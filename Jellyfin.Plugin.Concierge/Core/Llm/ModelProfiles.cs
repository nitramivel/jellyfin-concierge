using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Concierge.Configuration;

namespace Jellyfin.Plugin.Concierge.Core.Llm
{
    /// <summary>
    /// Turns whatever is stored in configuration into a usable list of chat model
    /// profiles and the one profile a given pass should call.
    /// </summary>
    /// <remarks>
    /// Pure logic, so it is pinned by tests rather than discovered on someone's
    /// server. The config page runs the same rules in JavaScript; if you change one,
    /// change both.
    /// </remarks>
    public static class ModelProfiles
    {
        /// <summary>
        /// The result of normalizing configuration: a list that is always safe to
        /// index, and a default id that always names a member of it — unless the
        /// list is empty, which is the one state callers must still handle.
        /// </summary>
        /// <param name="Profiles">The profiles, each with a non-empty unique id.</param>
        /// <param name="DefaultProfileId">The default profile's id, or empty when there are no profiles.</param>
        /// <param name="Changed">Whether normalization altered anything, so a caller can persist the repair.</param>
        public sealed record NormalizedProfiles(
            IReadOnlyList<ModelProfile> Profiles,
            string DefaultProfileId,
            bool Changed);

        /// <summary>
        /// Normalizes the profile list on a configuration: gives every profile an
        /// id and a name, and makes the default id point at something real.
        /// </summary>
        /// <remarks>
        /// There is no legacy migration step here, and that absence is deliberate —
        /// Concierge never shipped the pre-profile-list scalars, so there is nothing
        /// to fold in. See <see cref="PluginConfiguration"/> for why they must never
        /// be added.
        /// </remarks>
        /// <param name="config">The plugin configuration. Profile ids and names may be repaired in place.</param>
        /// <returns>The normalized list and default id.</returns>
        public static NormalizedProfiles Normalize(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var profiles = (config.ModelProfiles ?? Array.Empty<ModelProfile>())
                .Where(p => p is not null)
                .ToList();
            var changed = false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                // A blank id is what a profile added by hand or by an older page
                // looks like; a duplicate is what copying one looks like. Either way
                // the default id and the per-pass assignments cannot resolve it, so
                // mint a fresh one.
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
            }

            var defaultId = config.DefaultModelProfileId ?? string.Empty;
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
                // Points at a profile that was deleted, or at nothing at all. Falling
                // back to the first keeps searches working; leaving it dangling would
                // fail every query with a configuration error the owner never made.
                defaultId = profiles[0].Id;
                changed = true;
            }

            return new NormalizedProfiles(profiles, defaultId, changed);
        }

        /// <summary>
        /// Resolves the default profile.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The default profile.</returns>
        /// <exception cref="InvalidOperationException">No profile is configured.</exception>
        public static ModelProfile ResolveDefault(PluginConfiguration config)
            => Resolve(Normalize(config), null);

        /// <summary>
        /// Resolves a profile by id, falling back to the default when the id is
        /// blank or names nothing.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="profileId">The wanted profile id; blank means the default.</param>
        /// <returns>The resolved profile.</returns>
        /// <exception cref="InvalidOperationException">No profile is configured.</exception>
        public static ModelProfile Resolve(PluginConfiguration config, string? profileId)
            => Resolve(Normalize(config), profileId);

        /// <summary>
        /// Resolves a profile out of an already-normalized list.
        /// </summary>
        /// <remarks>
        /// <b>Every pass of one query must resolve through this overload against a
        /// single <see cref="Normalize"/> result</b> (hard rule 12). A query calls up
        /// to three chat passes; resolving each against its own Normalize means any
        /// id repaired along the way is minted afresh each time, so two resolves of
        /// what is really one profile compare as two — by reference and by id. The
        /// query then builds two identical providers and reports itself as running
        /// two models when it has only ever had one.
        /// </remarks>
        /// <param name="normalized">The normalized profile list.</param>
        /// <param name="profileId">The wanted profile id; blank means the default.</param>
        /// <returns>The resolved profile.</returns>
        /// <exception cref="InvalidOperationException">No profile is configured.</exception>
        public static ModelProfile Resolve(NormalizedProfiles normalized, string? profileId)
        {
            ArgumentNullException.ThrowIfNull(normalized);

            if (normalized.Profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "Concierge: no model profile configured. Add one on the Models tab of the plugin settings.");
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
        /// A readable fallback label for a profile the owner has not named.
        /// </summary>
        private static string Describe(ModelProfile profile)
            => string.IsNullOrWhiteSpace(profile.Model)
                ? profile.Provider.ToString()
                : string.Create(CultureInfo.InvariantCulture, $"{profile.Provider} {profile.Model}");

        private static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }
}
