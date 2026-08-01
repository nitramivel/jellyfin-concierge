using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Services.Llm;

namespace Jellyfin.Plugin.Concierge.Services.Embeddings
{
    /// <summary>
    /// Google's embedding models (POST {base}/models/{model}:batchEmbedContents).
    /// </summary>
    /// <remarks>
    /// Google carries the query/document asymmetry as a first-class request field —
    /// <c>taskType</c>, with <c>RETRIEVAL_QUERY</c> and <c>RETRIEVAL_DOCUMENT</c> —
    /// so this provider never prepends a text prefix. Sending both would apply the
    /// distinction twice and mean neither is what the model was trained on.
    /// </remarks>
    public sealed class GoogleEmbeddings : IEmbeddingProvider
    {
        /// <summary>The default API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Uri _endpoint;
        private readonly string _modelPath;
        private readonly TimeSpan? _initialRetryDelay;

        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The embedding model identifier.</param>
        /// <param name="apiKey">The API key.</param>
        /// <param name="baseUrl">Optional base URL override.</param>
        /// <param name="dimensions">Requested vector width, or 0 for the model's native width.</param>
        /// <param name="initialRetryDelay">First backoff step; overridden only by tests.</param>
        public GoogleEmbeddings(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            int dimensions = 0,
            TimeSpan? initialRetryDelay = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;

            // Accept "gemini-embedding-001" and "models/gemini-embedding-001" alike;
            // the path segment supplies the prefix either way.
            ModelId = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? model["models/".Length..]
                : model;
            _modelPath = "models/" + ModelId;

            _apiKey = apiKey;
            Dimensions = dimensions;
            _initialRetryDelay = initialRetryDelay;

            var basePart = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            _endpoint = new Uri(basePart + "/" + _modelPath + ":batchEmbedContents");
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <inheritdoc />
        public int Dimensions { get; }

        /// <inheritdoc />
        public async Task<EmbeddingResult> EmbedAsync(
            IReadOnlyList<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(texts);

            if (texts.Count == 0)
            {
                return new EmbeddingResult(Array.Empty<float[]>(), 0);
            }

            var taskType = purpose == EmbeddingPurpose.Query ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT";

            var requests = new List<object>(texts.Count);
            foreach (var text in texts)
            {
                var request = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["model"] = _modelPath,
                    ["content"] = new { parts = new[] { new { text } } },
                    ["taskType"] = taskType,
                };

                if (Dimensions > 0)
                {
                    request["outputDimensionality"] = Dimensions;
                }

                requests.Add(request);
            }

            var payload = new { requests };

            var body = await TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    message.Headers.Add("x-goog-api-key", _apiKey);
                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, failure) => $"Google embeddings returned {(int)status}: {Truncate(failure)}",
                _initialRetryDelay,
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var vectors = new List<float[]>(texts.Count);
            if (root.TryGetProperty("embeddings", out var embeddings) && embeddings.ValueKind == JsonValueKind.Array)
            {
                foreach (var embedding in embeddings.EnumerateArray())
                {
                    if (embedding.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                    {
                        vectors.Add(ReadVector(values));
                    }
                }
            }

            // batchEmbedContents answers positionally and carries no index to sort by,
            // so a short array is not recoverable — it can only be rejected.
            if (vectors.Count != texts.Count)
            {
                throw new InvalidOperationException(
                    $"Concierge: Google returned {vectors.Count} vectors for {texts.Count} inputs.");
            }

            // The batch endpoint reports no token usage, so cost for this provider is
            // estimated from the text rather than billed from the response.
            return new EmbeddingResult(vectors, 0);
        }

        private static float[] ReadVector(JsonElement array)
        {
            var vector = new float[array.GetArrayLength()];
            var i = 0;
            foreach (var value in array.EnumerateArray())
            {
                vector[i++] = value.GetSingle();
            }

            return vector;
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
