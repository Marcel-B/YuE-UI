using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Voices;

/// <param name="Vocals">The vocals without their reverb where StemMyWav could take it off, else as they are.</param>
/// <param name="Reverb">The reverb taken off the vocals, if any.</param>
public sealed record Stems(string Vocals, string Instrumental, string? Reverb);

/// <summary>
/// StemMyWav's Mac API: <c>POST /api/separate</c> takes the FLAC and answers, once the separation is done, with a
/// ZIP of 48 kHz WAVs. It is synchronous and one at a time, unlike its gateway, which queues; this server has its
/// own queue and calls it on the same Mac. Used only where this server's own separator is not installed.
/// </summary>
public sealed class StemClient(IHttpClientFactory clients, IOptions<VoiceOptions> options) : IStemBackend
{
    public const string HttpClientName = "stems";

    /// <summary>
    /// Separates <paramref name="flac"/> into <paramref name="directory"/> and answers the WAVs the model made, in the
    /// ZIP's order. Which files those are depends on the model (vocals and instrumental, four stems, …).
    /// </summary>
    /// <param name="dereverb">Also splits the vocals into dry vocals and their reverb, which takes longer.</param>
    public async Task<IReadOnlyList<string>> ExtractAsync(string flac, string model, bool dereverb, string directory, CancellationToken cancellationToken)
    {
        var (baseUri, key) = Service();
        await using var audio = File.OpenRead(flac);
        var content = new StreamContent(audio);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        var path = $"api/separate?model={Uri.EscapeDataString(model)}&dereverb={(dereverb ? "true" : "false")}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path)) { Content = content };
        request.Headers.Add("X-Api-Key", key);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, "stems.zip");
        await using (var file = File.Create(zip))
        {
            await response.Content.CopyToAsync(file, cancellationToken);
        }
        List<string> files = [];
        await using (var archive = await ZipFile.OpenReadAsync(zip, cancellationToken))
        {
            foreach (var entry in archive.Entries)
            {
                // Only the WAVs at the top: the names go into paths and URLs, so nothing that could leave the folder.
                if (entry.FullName != entry.Name || !entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var target = Path.Combine(directory, entry.Name);
                await entry.ExtractToFileAsync(target, overwrite: true, cancellationToken);
                files.Add(target);
            }
        }
        File.Delete(zip);
        return files;
    }

    /// <summary>
    /// The models StemMyWav offers, as its gateway lists them (<c>GET api/models</c>). Null when the service does not
    /// answer that; the caller then offers only <see cref="VoiceOptions.StemModel"/>.
    /// </summary>
    public async Task<IReadOnlyList<StemModel>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        var (baseUri, key) = Service();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "api/models"));
        request.Headers.Add("X-Api-Key", key);
        try
        {
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<List<StemModel>>(ModelJson, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or NotSupportedException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ModelJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private (Uri BaseUri, string Key) Service()
    {
        var settings = options.Value;
        if (settings.StemsBaseUri is not { } baseUri || settings.ResolvedStemsApiKey is not { } key)
        {
            throw new VoiceServiceException("No stem service is configured (Voice:StemsBaseUrl and its key).", HttpStatusCode.NotImplemented);
        }
        return (baseUri, key);
    }

    /// <returns>A successful answer; the caller disposes it.</returns>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, completion, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new VoiceServiceException($"The stem service cannot be reached: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceServiceException("The stem service did not answer in time.", HttpStatusCode.GatewayTimeout);
        }
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw new VoiceServiceException(await VoiceClient.ProblemAsync(response, "stem service", cancellationToken), VoiceClient.Passed(response.StatusCode));
            }
        }
        return response;
    }
}
