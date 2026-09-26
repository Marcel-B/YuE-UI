using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Voices;

/// <param name="Vocals">The vocals without their reverb where StemMyWav could take it off, else as they are.</param>
/// <param name="Reverb">The reverb taken off the vocals, if any.</param>
public sealed record Stems(string Vocals, string Instrumental, string? Reverb);

/// <summary>
/// StemMyWav's Mac API: <c>POST /api/separate</c> takes the FLAC and answers, once the separation is done, with a
/// ZIP of 48 kHz WAVs. It is synchronous and one at a time, unlike its gateway, which queues; this server has its
/// own queue and calls it on the same Mac.
/// </summary>
public sealed class StemClient(IHttpClientFactory clients, IOptions<VoiceOptions> options)
{
    public const string HttpClientName = "stems";

    public async Task<Stems> SeparateAsync(string flac, string directory, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.StemsBaseUri is not { } baseUri || settings.ResolvedStemsApiKey is not { } key)
        {
            throw new VoiceServiceException("No stem service is configured (Voice:StemsBaseUrl and its key).", HttpStatusCode.NotImplemented);
        }
        await using var audio = File.OpenRead(flac);
        var content = new StreamContent(audio);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        // Dry vocals: Seed-VC would copy the reverb into the new voice, and it is mixed back in untouched instead.
        var path = $"api/separate?model={Uri.EscapeDataString(settings.StemModel)}&dereverb=true";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path)) { Content = content };
        request.Headers.Add("X-Api-Key", key);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new VoiceServiceException($"The stem service cannot be reached: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceServiceException("The stem service did not answer in time.", HttpStatusCode.GatewayTimeout);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new VoiceServiceException(await VoiceClient.ProblemAsync(response, "stem service", cancellationToken), VoiceClient.Passed(response.StatusCode));
            }
            Directory.CreateDirectory(directory);
            var zip = Path.Combine(directory, "stems.zip");
            await using (var file = File.Create(zip))
            {
                await response.Content.CopyToAsync(file, cancellationToken);
            }
            await ZipFile.ExtractToDirectoryAsync(zip, directory, overwriteFiles: true, cancellationToken);
            File.Delete(zip);
        }

        string? Stem(string name) => File.Exists(Path.Combine(directory, name)) ? Path.Combine(directory, name) : null;
        var vocals = Stem("vocals_dry.wav") ?? Stem("vocals.wav");
        var instrumental = Stem("instrumental.wav");
        if (vocals is null || instrumental is null)
        {
            throw new VoiceServiceException($"The stem service's model {settings.StemModel} gave no vocals and instrumental.");
        }
        return new Stems(vocals, instrumental, Stem("vocals_reverb.wav"));
    }
}
