using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace YueUI.Api;

/// <summary>
/// Serves the frontend's static files, the built scripts and styles compressed and cached for good.
/// </summary>
/// <remarks>
/// Neither Kestrel nor <c>tailscale serve</c> compresses, and the Mac's home upload is slow: the first load was
/// ~800 kB. The build writes a Brotli and a gzip copy next to each asset (<c>precompress</c> in
/// <c>vite.config.ts</c>), and a request whose browser accepts one is answered from it, so nothing is compressed per
/// request. Files below <see cref="AssetsPath"/> carry a content hash in their name, so browsers may keep them
/// without asking again; only <c>index.html</c> (<see cref="ClientAppEndpoints"/>) and the unhashed files next to it
/// (<c>version.json</c>, <c>sw.js</c>, …) are checked on every load.
/// </remarks>
public static class ClientAppAssets
{
    public const string AssetsPath = ClientAppEndpoints.BasePath + "/assets/";

    /// <summary>Preferred first; Brotli is ~15 % smaller than gzip on these files.</summary>
    private static readonly (string Encoding, string Extension)[] Encodings = [("br", ".br"), ("gzip", ".gz")];

    public static WebApplication UseClientAppAssets(this WebApplication app)
    {
        var files = app.Environment.WebRootFileProvider;
        app.Use((context, next) =>
        {
            var path = context.Request.Path.Value;
            if (path is not null
                && path.StartsWith(AssetsPath, StringComparison.Ordinal)
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                // Whichever variant is sent, a shared cache must not hand it to a browser that asked differently.
                context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                if (PickEncoding(context.Request.Headers.AcceptEncoding, path, files) is { } picked)
                {
                    context.Request.Path = path + picked.Extension;
                    context.Response.Headers.ContentEncoding = picked.Encoding;
                }
            }

            return next(context);
        });

        var types = new FileExtensionContentTypeProvider();
        app.UseStaticFiles(new StaticFileOptions
        {
            // A compressed copy has the type of what it contains; its own extension would make it a download.
            ContentTypeProvider = new CompressedContentTypeProvider(types),
            OnPrepareResponse = context =>
            {
                if (context.Context.Request.Path.Value?.StartsWith(AssetsPath, StringComparison.Ordinal) == true)
                {
                    context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
            },
        });
        return app;
    }

    private static (string Encoding, string Extension)? PickEncoding(
        StringValues acceptEncoding,
        string path,
        Microsoft.Extensions.FileProviders.IFileProvider files)
    {
        if (StringValues.IsNullOrEmpty(acceptEncoding)
            || !StringWithQualityHeaderValue.TryParseList(acceptEncoding, out var accepted))
        {
            return null;
        }

        foreach (var candidate in Encodings)
        {
            var accepts = accepted.Any(value =>
                value.Value.Equals(candidate.Encoding, StringComparison.OrdinalIgnoreCase) && value.Quality is not 0);
            if (accepts && files.GetFileInfo(path.TrimStart('/') + candidate.Extension).Exists)
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class CompressedContentTypeProvider(IContentTypeProvider inner) : IContentTypeProvider
    {
        public bool TryGetContentType(string subpath, out string contentType)
        {
            foreach (var (_, extension) in Encodings)
            {
                if (subpath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return inner.TryGetContentType(subpath[..^extension.Length], out contentType!);
                }
            }

            return inner.TryGetContentType(subpath, out contentType!);
        }
    }
}
