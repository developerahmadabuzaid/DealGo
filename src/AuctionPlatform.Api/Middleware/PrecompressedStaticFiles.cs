using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace AuctionPlatform.Api.Middleware;

/// <summary>
/// Serves the `.br` or `.gz` copy of a static file when the browser accepts it.
///
/// The app built for the web is several megabytes of JavaScript and WebAssembly. Compressing it
/// on every request would burn CPU for the same answer each time, so the build compresses each
/// file once, as hard as Brotli goes, and this hands over the small copy. A browser that asks for
/// neither encoding, or a file that was never compressed, gets the original.
/// </summary>
public static class PrecompressedStaticFiles
{
    // Brotli first: it is both smaller and understood by every browser that runs this app.
    private static readonly (string Encoding, string Extension)[] Encodings =
    {
        ("br", ".br"),
        ("gzip", ".gz"),
    };

    public static IApplicationBuilder UsePrecompressedStaticFiles(this WebApplication app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;

            if (!HttpMethods.IsGet(context.Request.Method) || string.IsNullOrEmpty(path) ||
                path.EndsWith(".br", StringComparison.Ordinal) ||
                path.EndsWith(".gz", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            var accepted = context.Request.Headers.AcceptEncoding.ToString();

            foreach (var (encoding, extension) in Encodings)
            {
                if (!accepted.Contains(encoding, StringComparison.OrdinalIgnoreCase)) continue;

                var candidate = path + extension;
                var file = app.Environment.WebRootFileProvider.GetFileInfo(candidate.TrimStart('/'));
                if (!file.Exists) continue;

                // The body is the compressed copy; everything else — the address, the type, the
                // caching — stays as it was for the file the browser asked for.
                context.Response.Headers.ContentEncoding = encoding;
                context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                context.Request.Path = candidate;
                break;
            }

            await next();
        });
    }

    /// <summary>
    /// Reports the type of the file inside the compressed one: "main.dart.js.br" is JavaScript,
    /// not an unknown ".br" that the static-file middleware would refuse to serve.
    /// </summary>
    public sealed class ContentTypeProvider : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider _inner = new();

        public ContentTypeProvider()
        {
            // Flutter's engine ships as WebAssembly; older type maps do not name it.
            _inner.Mappings[".wasm"] = "application/wasm";
        }

        public bool TryGetContentType(string subpath, out string contentType)
        {
            if (subpath.EndsWith(".br", StringComparison.Ordinal) ||
                subpath.EndsWith(".gz", StringComparison.Ordinal))
            {
                subpath = subpath[..subpath.LastIndexOf('.')];
            }

            return _inner.TryGetContentType(subpath, out contentType!);
        }
    }
}
