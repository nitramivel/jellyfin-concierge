using Jellyfin.Plugin.Concierge.Services;
using Jellyfin.Plugin.Concierge.Services.Budget;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Library;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Quotes;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Jellyfin.Plugin.Concierge.Services.Web;
using Microsoft.AspNetCore.Hosting;
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

            // Persisted, because a monthly cap that resets on restart is not a cap.
            serviceCollection.AddSingleton<ISpendStore, SpendStore>();

            serviceCollection.AddSingleton<IIndexStore, IndexStore>();
            serviceCollection.AddSingleton<EnrichmentService>();
            serviceCollection.AddSingleton<ItemIndexer>();
            serviceCollection.AddSingleton<IndexBuildRequest>();

            // A singleton because it caches the loaded index. Rebuilding the BM25
            // postings and re-reading the vector file per query would put disk work
            // inside a latency budget measured in a couple of seconds.
            serviceCollection.AddSingleton<SearchService>();

            serviceCollection.AddSingleton<IQuoteStore, QuoteStore>();

            // Singleton: it holds the phrase index, and rebuilding that means
            // reading every extracted track off disk.
            serviceCollection.AddSingleton<QuoteIndexProvider>();
            serviceCollection.AddSingleton<SubtitleIndexer>();

            // Adds one script tag to the web client's index page and serves the
            // script. The client half only ever touches nodes it created, so it
            // sits alongside whatever else already owns that page.
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjector>();

            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, IndexBuildTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, SubtitleExtractTask>();
        }
    }
}
