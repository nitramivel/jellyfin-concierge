using System;
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

        private async Task ServeScriptAsync(HttpContext context)
        {
            var script = ReadScript();
            if (script.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
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
        /// </remarks>
        private async Task InjectAsync(HttpContext context, Func<Task> next)
        {
            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await next().ConfigureAwait(false);

                buffer.Position = 0;
                var body = await new StreamReader(buffer).ReadToEndAsync().ConfigureAwait(false);

                // Only touch a document that looks like the client's shell and does
                // not already carry our tag — a reload must not stack tags up.
                var close = body.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (close < 0 || body.Contains(Marker, StringComparison.Ordinal))
                {
                    context.Response.Body = original;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                    return;
                }

                var tag = "<script id=\"concierge-client\" src=\"" + ScriptPath + "\" defer></script>";
                var patched = body[..close] + tag + body[close..];
                var bytes = Encoding.UTF8.GetBytes(patched);

                context.Response.Body = original;
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
