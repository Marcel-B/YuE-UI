using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Backup;

/// <summary>The two WebDAV calls a backup needs, as module-o-mat makes them: MKCOL for folders, PUT for files.</summary>
public sealed class WebDavClient(IHttpClientFactory clients, IOptions<BackupOptions> options)
{
    public const string HttpClientName = "webdav";

    /// <summary>Folders known to exist in this backup, so each is asked for once and not before every file.</summary>
    private readonly HashSet<string> _folders = [];

    public void Forget() => _folders.Clear();

    /// <summary>Creates the backup folder and the folders of <paramref name="path"/> (relative, '/'-separated).</summary>
    public async Task EnsureFoldersAsync(string path, CancellationToken cancellationToken)
    {
        var current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            await EnsureFolderAsync(current, cancellationToken);
        }
    }

    public async Task EnsureFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (_folders.Contains(path))
        {
            return;
        }
        using var request = Request(new HttpMethod("MKCOL"), path.Length == 0 ? "" : path + "/");
        using var response = await Client(TimeSpan.FromMinutes(1)).SendAsync(request, cancellationToken);
        // 405: it exists already. 409 means the folder above is missing, which for anything below the backup folder
        // cannot happen (they are made top down), so it is the backup folder's parent.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            throw new WebDavException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Nextcloud refused the user or app password (401)",
                HttpStatusCode.Conflict => $"The folder above {Describe(path)} does not exist in the Nextcloud (409)",
                var status => $"Could not create {Describe(path)} (HTTP {(int)status})",
            });
        }
        _folders.Add(path);
    }

    /// <summary>Uploads the file, replacing what is at <paramref name="path"/>.</summary>
    public async Task PutAsync(string path, string file, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Put, path);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        // A seekable stream gives the request a Content-Length; Nextcloud handles chunked uploads badly.
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        try
        {
            response = await Client(options.Value.UploadTimeout).SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException($"Uploading {path} took longer than {options.Value.UploadTimeout.TotalMinutes:0} minutes", transient: true);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new WebDavException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Nextcloud refused the user or app password (401)",
                    HttpStatusCode.InsufficientStorage => "The Nextcloud is full (507)",
                    HttpStatusCode.RequestEntityTooLarge => $"The Nextcloud refused {path} as too large (413)",
                    HttpStatusCode.Locked => $"The Nextcloud has {path} locked (423)",
                    var status => $"Could not upload {path} (HTTP {(int)status})",
                }, transient: response.StatusCode is HttpStatusCode.Locked or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);
            }
        }
    }

    private HttpClient Client(TimeSpan timeout)
    {
        var client = clients.CreateClient(HttpClientName);
        client.Timeout = timeout;
        return client;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var settings = options.Value;
        var baseUri = settings.BaseUri ?? throw new WebDavException("No Nextcloud is configured");
        // Each segment escaped on its own: stem and voice file names may hold spaces, '#' or '?'.
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(method, new Uri(baseUri, escaped));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ResolvedUsername}:{settings.ResolvedPassword}")));
        return request;
    }

    private static string Describe(string path) => path.Length == 0 ? "the backup folder" : path;
}

/// <summary>The WebDAV server refused or did not answer; the message says what to do about it.</summary>
/// <param name="transient">
/// Worth trying again later rather than failing the backup: 423 (Nextcloud still holds a lock on the file, e.g. from
/// an upload a restart cut off, until its lock expires), 429, 502–504, or an upload that took too long.
/// </param>
public sealed class WebDavException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
}
