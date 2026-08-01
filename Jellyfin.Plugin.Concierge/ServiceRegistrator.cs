using Jellyfin.Plugin.Concierge.Services;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Library;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Concierge
{
    /// <summary>
    /// Registers Concierge's services with Jellyfin's DI container.
    /// </summary>
    /// <remarks>
    /// Both provider factories are registered by their interface. Orchestration
    /// takes the interface and never the concrete type — that seam is the only
    /// thing that makes an end-to-end pipeline test against canned responses
    /// possible (hard rule 5).
    /// </remarks>
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<ILibraryScanner, LibraryScanner>();
            serviceCollection.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();
            serviceCollection.AddSingleton<IEmbeddingProviderFactory, EmbeddingProviderFactory>();
            serviceCollection.AddSingleton<IQueryLogStore, QueryLogStore>();

            // A singleton because it holds the in-flight run in memory: the config
            // page polls for progress, and that must not mean reading a run document
            // — every prompt in it — off disk on each poll.
            serviceCollection.AddSingleton<IIndexRunLogStore, IndexRunLogStore>();

            serviceCollection.AddSingleton<IIndexStore, IndexStore>();
            serviceCollection.AddSingleton<EnrichmentService>();
            serviceCollection.AddSingleton<ItemIndexer>();

            // A singleton because it caches the loaded index. Rebuilding the BM25
            // postings and re-reading the vector file per query would put disk work
            // inside a latency budget measured in a couple of seconds.
            serviceCollection.AddSingleton<SearchService>();

            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, IndexBuildTask>();
        }
    }
}
