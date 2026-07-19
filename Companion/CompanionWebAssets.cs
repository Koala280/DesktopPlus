using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Serves the PWA shell from embedded resources (no physical wwwroot exists in a
    /// single-file publish). Unknown non-API routes fall back to index.html (SPA routing).
    /// </summary>
    internal static class CompanionWebAssets
    {
        private const string ResourcePrefix = "DesktopPlus.Companion.WebApp.";

        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/{**path}", async (HttpContext context, string? path) =>
            {
                string relative = string.IsNullOrWhiteSpace(path) ? "index.html" : path;

                // The API is handled by dedicated endpoints; never serve the shell for /api/*.
                if (relative.StartsWith("api/", System.StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("api", System.StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var asset = OpenAsset(relative) ?? OpenAsset("index.html");
                if (asset == null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = asset.Value.ContentType;
                using var stream = asset.Value.Stream;
                await stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
            });
        }

        private static (Stream Stream, string ContentType)? OpenAsset(string relative)
        {
            string resourceName = ResourcePrefix + relative.Replace('/', '.').TrimStart('.');
            var assembly = Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return null;
            }

            return (stream, GetContentType(relative));
        }

        private static string GetContentType(string name)
        {
            string extension = Path.GetExtension(name).ToLowerInvariant();
            return extension switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".webmanifest" => "application/manifest+json; charset=utf-8",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream"
            };
        }
    }
}
