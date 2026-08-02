using System;
using System.Net.Http;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;

namespace Jellyfin.Plugin.Concierge.Services.Llm
{
    /// <summary>
    /// Builds the <see cref="ILlmProvider"/> a pass should call.
    /// </summary>
    /// <remarks>
    /// An interface, and orchestration takes the interface rather than the concrete
    /// factory. That seam is the only thing that makes hard rule 5 — the whole
    /// pipeline tested against canned responses — achievable at all: with the
    /// concrete type there is nothing to substitute, and the only testable parts are
    /// whatever pure logic can be lifted out from under it.
    /// </remarks>
    public interface ILlmProviderFactory
    {
        /// <summary>
        /// Creates the provider for the configuration's default model profile.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The provider.</returns>
        ILlmProvider Create(PluginConfiguration config);

        /// <summary>
        /// Creates the provider described by one model profile.
        /// </summary>
        /// <param name="profile">The model profile to call.</param>
        /// <param name="globalEnableThinking">The configuration-wide thinking setting.</param>
        /// <returns>The provider.</returns>
        ILlmProvider Create(ModelProfile profile, bool globalEnableThinking);
    }

    /// <summary>
    /// Builds the configured <see cref="ILlmProvider"/> from plugin configuration.
    /// </summary>
    public sealed class LlmProviderFactory : ILlmProviderFactory
    {
        /// <summary>
        /// The named HttpClient used for chat calls.
        /// </summary>
        public const string HttpClientName = "ConciergeLlm";

        private readonly IHttpClientFactory _httpClientFactory;

        public LlmProviderFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Creates the provider for the configuration's default model profile.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="InvalidOperationException">Configuration is incomplete for the selected provider.</exception>
        public ILlmProvider Create(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            return Create(ModelProfiles.ResolveDefault(config), config.EnableThinking);
        }

        /// <summary>
        /// Creates the provider described by one model profile.
        /// </summary>
        /// <remarks>
        /// Takes the profile explicitly rather than reading the default out of
        /// configuration, so a pass that wants a specific model asks for it here
        /// rather than mutating global config to get it.
        /// </remarks>
        /// <param name="profile">The model profile to call.</param>
        /// <param name="globalEnableThinking">
        /// The configuration-wide thinking setting. The profile's own
        /// <see cref="ModelProfile.Thinking"/> overrides it, and that resolution
        /// happens <em>here</em> rather than at each call site so no caller can
        /// bypass a profile's setting by passing the global flag straight through.
        /// </param>
        /// <returns>The provider.</returns>
        /// <exception cref="InvalidOperationException">The profile is incomplete for its provider.</exception>
        public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                throw new InvalidOperationException(
                    $"Concierge: the model profile '{Describe(profile)}' has no model set.");
            }

            var enableThinking = profile.ThinkingResolved(globalEnableThinking);

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            switch (profile.Provider)
            {
                case LlmProviderKind.Anthropic:
                    RequireApiKey(profile);
                    return new AnthropicProvider(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl),
                        enableThinking);

                case LlmProviderKind.Google:
                    RequireApiKey(profile);
                    return new GoogleProvider(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl),
                        enableThinking);

                case LlmProviderKind.Grok:
                    RequireApiKey(profile);
                    return OpenAiChatProvider.CreateGrok(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl));

                case LlmProviderKind.OpenAi:
                    RequireApiKey(profile);
                    return OpenAiChatProvider.CreateOpenAi(
                        httpClient,
                        profile.Model,
                        profile.ApiKey,
                        NullIfEmpty(profile.BaseUrl),
                        enableThinking);

                case LlmProviderKind.OpenAiCompatible:
                    if (string.IsNullOrWhiteSpace(profile.BaseUrl))
                    {
                        throw new InvalidOperationException(
                            "Concierge: the OpenAI-compatible provider requires a base URL (e.g. http://localhost:11434/v1).");
                    }

                    return OpenAiChatProvider.CreateCompatible(
                        httpClient,
                        profile.Model,
                        profile.BaseUrl,
                        NullIfEmpty(profile.ApiKey));

                default:
                    throw new InvalidOperationException($"Concierge: unknown provider {profile.Provider}.");
            }
        }

        private static void RequireApiKey(ModelProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.ApiKey))
            {
                throw new InvalidOperationException(
                    $"Concierge: the model profile '{Describe(profile)}' uses {profile.Provider}, which requires an API key.");
            }
        }

        /// <summary>
        /// Names a profile in an error the owner has to act on. Errors are read
        /// against the profile list, so the name they see there is the one to use.
        /// </summary>
        private static string Describe(ModelProfile profile)
            => string.IsNullOrWhiteSpace(profile.Name) ? profile.Provider.ToString() : profile.Name;

        private static string? NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
