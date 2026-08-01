namespace Jellyfin.Plugin.Concierge.Configuration
{
    /// <summary>
    /// One saved way of turning text into a vector.
    /// </summary>
    /// <remarks>
    /// A parallel type to <see cref="ModelProfile"/> rather than the same one, and
    /// deliberately so. Four of the chat profile's fields are meaningless on every
    /// embedding profile — output price, cached-input price, thinking, and the
    /// output cap — and a "let it think" checkbox on a thing that cannot think is
    /// not a cosmetic problem, it is an invitation to set it. What this carries
    /// instead is <see cref="Dimensions"/> and the two prefixes, none of which mean
    /// anything to a chat model. Two types, one pattern.
    /// <para>
    /// Mutable with a parameterless constructor for <see cref="System.Xml.Serialization.XmlSerializer"/>,
    /// exactly as <see cref="ModelProfile"/> is.
    /// </para>
    /// </remarks>
    public class EmbeddingProfile
    {
        /// <summary>
        /// Gets or sets the stable identifier for this profile, referenced by
        /// <see cref="PluginConfiguration.EmbeddingProfileId"/>. Survives renaming
        /// and reordering.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name shown in the profile list.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the embedding backend this profile calls.
        /// </summary>
        public EmbeddingProviderKind Provider { get; set; } = EmbeddingProviderKind.OpenAi;

        /// <summary>
        /// Gets or sets the embedding model identifier, e.g. <c>text-embedding-3-small</c>.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider API key. Optional for a local server, which
        /// commonly needs none.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional base URL override. Required for
        /// <see cref="EmbeddingProviderKind.OpenAiCompatible"/>, e.g.
        /// <c>http://localhost:11434/v1</c>.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the vector width this profile produces. 0 means "whatever
        /// the model returns", which is the right answer until someone deliberately
        /// truncates.
        /// </summary>
        /// <remarks>
        /// Set explicitly only to use Matryoshka truncation — asking OpenAI's
        /// <c>text-embedding-3-*</c> for fewer dimensions than native, which trades a
        /// little quality for a large cut in the memory the index occupies. It is
        /// part of the index's identity: changing it invalidates every stored vector.
        /// </remarks>
        public int Dimensions { get; set; }

        /// <summary>
        /// Gets or sets the marker prepended to a <em>search query</em> before it is
        /// embedded.
        /// </summary>
        /// <remarks>
        /// <b>Not cosmetic, and not optional for the models that want it.</b>
        /// <c>bge-m3</c>, the E5 family and <c>nomic-embed-text</c> are trained with
        /// asymmetric markers distinguishing a query from a passage — typically
        /// <c>query: </c> and <c>passage: </c>. Using the wrong marker, or none,
        /// degrades retrieval <em>with no error and no symptom</em>: the vectors come
        /// back the right width, the index builds, the search runs, and the results
        /// are quietly worse than they should be. That is the worst failure shape
        /// there is, so the prefixes are stored, defaulted per known model, and
        /// recorded in the index's identity alongside the model and dimensionality.
        /// </remarks>
        public string QueryPrefix { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the marker prepended to an <em>indexed document</em> before
        /// it is embedded. See <see cref="QueryPrefix"/> — the pair travels together
        /// and changing either invalidates the index.
        /// </summary>
        public string DocumentPrefix { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets this profile's price in USD per million input tokens.
        /// 0 logs token counts without cost.
        /// </summary>
        /// <remarks>
        /// One price, not three: embeddings have no output tokens to bill and no
        /// prompt cache to read from.
        /// </remarks>
        public decimal InputCostPerMillion { get; set; }
    }
}
