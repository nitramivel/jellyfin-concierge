using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Concierge.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Concierge
{
    /// <summary>
    /// The Concierge plugin: natural-language search over the library, answered
    /// from a local hybrid index and explained by a model.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override Guid Id => Guid.Parse("361b0830-e7c9-460a-b116-0164adec76dd");

        public override string Name => "Concierge";

        public override string Description =>
            "Search your library the way you'd describe a film to a friend: a sentence goes in, the items you actually own come back, each with a reason it matched.";

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return
            [
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                },
            ];
        }
    }
}
