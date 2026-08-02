using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Web
{
    /// <summary>
    /// Serves the client script and adds a tag for it to the web client's index page.
    /// </summary>
    /// <remarks>
    /// <b>Why middleware rather than the File Transformation plugin</b>, which the
    /// plan names: middleware needs no second plugin installed, no cross-plugin API
    /// to call, and no ordering agreement with whatever else is patching web files.
    /// It is also the mechanism Jellyfin Enhanced uses on this very server, so it is
    /// proven here rather than merely documented.
    /// <para>
    /// The rewrite is deliberately the smallest possible: one script tag inserted
    /// before <c>&lt;/body&gt;</c> on the index document, and nothing else touched.
    /// Everything of consequence happens in the script, where the rule is that it
    /// only ever touches nodes it created.
    /// </para>
    /// </remarks>
    public sealed class ScriptInjector : IStartupFilter
    {
        /// <summary>Where the script is served from.</summary>
        public const string ScriptPath = "/Concierge/client.js";

        private const string Marker = "id=\"concierge-client\"";

        /// <summary>
        /// The script's content hash, used as both the cache-busting token and the
        /// entity tag.
        /// </summary>
        /// <remarks>
        /// <b>This is the fix for an upgrade that silently did nothing.</b> The tag
        /// pointed at a URL that never changed from one release to the next, and the
        /// response carried no cache headers at all — so a browser that had fetched
        /// the script once kept serving that copy, and a new version of the plugin
        /// installed and loaded while the page went on running the old client. It
        /// looked exactly like the change had not been made.
        /// <para>
        /// Hashing the content rather than stamping the plugin version means the URL
        /// changes when and only when the script does, so an unchanged script still
        /// hits cache across upgrades and a changed one cannot be served stale.
        /// </para>
        /// <para>
        /// Computed per call rather than cached, because the script carries settings:
        /// change the debounce and the served file changes, so the URL has to change
        /// with it or the browser keeps the old number forever. Hashing twenty-odd
        /// kilobytes costs microseconds and only happens on index and script requests.
        /// </para>
        /// </remarks>
        private static string Fingerprint => Fingerprinted(Configured());

        private readonly ILogger<ScriptInjector> _logger;

        public ScriptInjector(ILogger<ScriptInjector> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Reads the script out of the assembly.
        /// </summary>
        /// <returns>The script, or empty when it is missing.</returns>
        public static string ReadScript()
        {
            var assembly = typeof(ScriptInjector).Assembly;

            // Found by suffix rather than by a constructed name. The manifest name
            // is derived from the root namespace and folder, so building it by
            // string surgery breaks silently the first time either is renamed —
            // and the symptom would be a search box that simply does nothing.
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith(".concierge.js", StringComparison.Ordinal));

            if (name is null)
            {
                return string.Empty;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Path.Equals(ScriptPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await ServeScriptAsync(context).ConfigureAwait(false);
                        return;
                    }

                    if (!IsIndexRequest(context))
                    {
                        await nextMiddleware().ConfigureAwait(false);
                        return;
                    }

                    await InjectAsync(context, nextMiddleware).ConfigureAwait(false);
                });

                next(app);
            };
        }

        /// <summary>
        /// Whether this request is for the web client's index document.
        /// </summary>
        /// <remarks>
        /// Checked on the path rather than on the response's content type, because
        /// buffering every response to find out would put the whole server's output
        /// through a memory stream to catch one document.
        /// </remarks>
        private static bool IsIndexRequest(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            return path.Length == 0
                || path.Equals("/", StringComparison.Ordinal)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The versioned URL the page should ask for.
        /// </summary>
        public static string VersionedScriptPath => ScriptPath + "?v=" + Fingerprint;

        /// <summary>
        /// Inserts the script tag into a document.
        /// </summary>
        /// <param name="body">The document as served.</param>
        /// <returns>The patched document, or null to leave it exactly as it is.</returns>
        /// <remarks>
        /// Separated from the middleware so the decision can be tested without a
        /// server. Only a document that looks like the client's shell and does not
        /// already carry the tag is touched — a reload must never stack tags up.
        /// </remarks>
        public static string? Patch(string body)
        {
            var close = body.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            if (close < 0 || body.Contains(Marker, StringComparison.Ordinal))
            {
                return null;
            }

            var tag = "<script id=\"concierge-client\" src=\"" + VersionedScriptPath
                + "\" defer></script>";

            return body[..close] + tag + body[close..];
        }

        private static string Fingerprinted(string script)
        {
            if (script.Length == 0)
            {
                return "0";
            }

            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script));

            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>
        /// The script as served: the embedded file with the owner's settings in it.
        /// </summary>
        /// <remarks>
        /// Substituted here rather than fetched by the client at startup, because a
        /// second request would mean the first keystroke either races it or waits for
        /// it. The value is a number in a file we already serve.
        /// </remarks>
        public static string Configured()
        {
            var script = ReadScript();
            var config = Plugin.Instance?.Configuration;

            if (script.Length == 0 || config is null)
            {
                return script;
            }

            var debounce = Math.Clamp(config.SearchDebounceMs, 250, 30000);

            script = System.Text.RegularExpressions.Regex.Replace(
                script,
                @"var DEBOUNCE_MS = \d+;",
                "var DEBOUNCE_MS = " + debounce.ToString(CultureInfo.InvariantCulture) + ";");

            return System.Text.RegularExpressions.Regex.Replace(
                script,
                @"var HIDE_SEERR_ICON = (?:true|false);",
                "var HIDE_SEERR_ICON = "
                    + (config.HideJellyseerrIcon ? "true" : "false") + ";");
        }

        private async Task ServeScriptAsync(HttpContext context)
        {
            var script = Configured();
            if (script.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var tag = "\"" + Fingerprint + "\"";

            // "no-cache" means revalidate, not "never store". Paired with the entity
            // tag it costs one conditional request per page load and answers 304 for
            // the rest, which is the cheap half of never serving a stale client.
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = tag;

            if (context.Request.Headers.IfNoneMatch.ToString().Contains(tag, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            context.Response.ContentType = "application/javascript; charset=utf-8";
            await context.Response.WriteAsync(script).ConfigureAwait(false);
        }

        /// <summary>
        /// Buffers the index document and inserts one script tag before the closing
        /// body tag.
        /// </summary>
        /// <remarks>
        /// Every failure path here restores the original response untouched. A plugin
        /// that could break the page it patches would take the whole web client down
        /// with it, and no search feature is worth that risk.
        /// <para>
        /// <b>The conditional request is handled here rather than upstream.</b>
        /// Jellyfin serves the index with its own entity tag, computed from the file
        /// on disk — which never changes when the plugin does. Left alone, a browser
        /// revalidates, gets a 304, and goes on using its cached copy of the patched
        /// page, complete with the script URL from whichever version it first saw.
        /// So the validators are stripped from the request to force a full body, and
        /// the response carries an entity tag over the patched document instead.
        /// </para>
        /// </remarks>
        private async Task InjectAsync(HttpContext context, Func<Task> next)
        {
            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            var wanted = context.Request.Headers.IfNoneMatch.ToString();
            context.Request.Headers.Remove("If-None-Match");
            context.Request.Headers.Remove("If-Modified-Since");

            try
            {
                await next().ConfigureAwait(false);

                buffer.Position = 0;
                var body = await new StreamReader(buffer).ReadToEndAsync().ConfigureAwait(false);

                var patched = Patch(body);

                if (patched is null)
                {
                    context.Response.Body = original;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(patched);
                var etag = "\"" + Fingerprinted(patched) + "\"";

                context.Response.Body = original;
                context.Response.Headers.LastModified = default;
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.ETag = etag;

                if (wanted.Contains(etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    context.Response.ContentLength = null;
                    return;
                }

                context.Response.ContentLength = bytes.Length;
                await original.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not add the client script; the page is served unchanged");

                context.Response.Body = original;

                try
                {
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                }
                catch (Exception copyFailure)
                {
                    _logger.LogError(copyFailure, "Concierge: the original response could not be restored");
                }
            }
            finally
            {
                context.Response.Body = original;
            }
        }
    }
}
