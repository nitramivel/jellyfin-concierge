using System;
using System.Net.Http;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;

namespace Jellyfin.Plugin.Concierge.Services.Embeddings
{
    /// <summary>
    /// Builds the configured <see cref="IEmbeddingProvider"/> from plugin
    /// configuration.
    /// </summary>
    public sealed class EmbeddingProviderFactory : IEmbeddingProviderFactory
    {
        /// <summary>
        /// The named HttpClient used for embedding calls.
        /// </summary>
        public const string HttpClientName = "ConciergeEmbeddings";

        private readonly IHttpClientFactory _httpClientFactory;

        public EmbeddingProviderFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Creates the provider for the configuration's selected embedding profile.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="InvalidOperationException">No embedding profile is configured.</exception>
        public IEmbeddingProvider Create(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            return Create(EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId));
        }

        /// <summary>
        /// Creates the provider described by one embedding profile.
        /// </summary>
        /// <remarks>
        /// The prefixes are resolved into the provider here rather than passed per
        /// call, for the same reason thinking is resolved inside
        /// <see cref="Llm.LlmProviderFactory"/>: no call site can then get them
        /// wrong, and getting them wrong has no visible symptom.
        /// </remarks>
        /// <param name="profile">The embedding profile to call.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="InvalidOperationException">The profile is incomplete for its provider.</exception>
        public IEmbeddingProvider Create(EmbeddingProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                throw new InvalidOperationException(
                    $"Concierge: the embedding profile '{Describe(profile)}' has no model set.");
            }

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            switch (profile.Provider)
            {
                case EmbeddingProviderKind.OpenAi:
                    RequireApiKey(profile);
                    return new OpenAiCompatibleEmbeddings(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl),
                        profile.Dimensions,
                        profile.QueryPrefix,
                        profile.DocumentPrefix,
                        sendDimensions: true);

                case EmbeddingProviderKind.OpenAiCompatible:
                    if (string.IsNullOrWhiteSpace(profile.BaseUrl))
                    {
                        throw new InvalidOperationException(
                            "Concierge: the OpenAI-compatible embedding provider requires a base URL (e.g. http://localhost:11434/v1).");
                    }

                    return new OpenAiCompatibleEmbeddings(
                        httpClient,
                        profile.Model,
                        NullIfEmpty(profile.ApiKey),
                        profile.BaseUrl,
                        profile.Dimensions,
                        profile.QueryPrefix,
                        profile.DocumentPrefix,

                        // `dimensions` is an OpenAI extension. Ollama and LM Studio
                        // have no use for it and are entitled to reject a field they
                        // do not recognise, so a local profile that wants a narrower
                        // vector has to pick a model that produces one.
                        sendDimensions: false);

                case EmbeddingProviderKind.Google:
                    RequireApiKey(profile);
                    return new GoogleEmbeddings(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl),
                        profile.Dimensions);

                case EmbeddingProviderKind.Voyage:
                    RequireApiKey(profile);
                    return new VoyageEmbeddings(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl),
                        profile.Dimensions);

                default:
                    throw new InvalidOperationException(
                        $"Concierge: unknown embedding provider {profile.Provider}.");
            }
        }

        private static void RequireApiKey(EmbeddingProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.ApiKey))
            {
                throw new InvalidOperationException(
                    $"Concierge: the embedding profile '{Describe(profile)}' uses {profile.Provider}, which requires an API key.");
            }
        }

        private static string Describe(EmbeddingProfile profile)
            => string.IsNullOrWhiteSpace(profile.Name) ? profile.Provider.ToString() : profile.Name;

        private static string? NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
