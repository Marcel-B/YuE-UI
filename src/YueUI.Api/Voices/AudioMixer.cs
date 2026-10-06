using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using YueUI.Api.Share;

namespace YueUI.Api.Voices;

/// <param name="Instrumental">The song without its vocals.</param>
/// <param name="Vocals">The converted vocals.</param>
/// <param name="OriginalVocals">The separated vocals before conversion, whose loudness the converted ones take.</param>
/// <param name="Reverb">The original's reverb to mix back in, or null.</param>
public sealed record MixInput(string Instrumental, string Vocals, string OriginalVocals, string? Reverb);

/// <summary>Puts converted vocals back under the instrumental. Tests replace it.</summary>
public interface IAudioMixer
{
    /// <summary>Writes the mix as FLAC to <paramref name="output"/>.</summary>
    Task MixAsync(MixInput input, string output, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="input"/> (a WAV) as FLAC to <paramref name="output"/>, keeping its sample rate.</summary>
    Task EncodeFlacAsync(string input, string output, CancellationToken cancellationToken);

    /// <summary>
    /// Reads any audio file ffmpeg knows (an upload: MP3, M4A, a phone's recording) and writes its first audio stream at
    /// 48 kHz to <paramref name="output"/>, as 16-bit WAV or FLAC by the output's ending.
    /// </summary>
    Task DecodeAsync(string input, string output, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="input"/> (a stored stem) as a 48 kHz 24-bit WAV to <paramref name="output"/>, the format
    /// the Logic template's vocal tracks hold.
    /// </summary>
    Task DecodeWaveAsync(string input, string output, CancellationToken cancellationToken);
}

/// <summary>
/// Mixes with ffmpeg, which ChangeMyVoice and StemMyWav need on this Mac anyway (afconvert cannot mix).
/// </summary>
/// <remarks>
/// Seed-VC does not keep the level of its input, so the converted vocals are first brought to the mean loudness of
/// the separated ones: then the balance against the instrumental is the one YuE2 made. Vocals and instrumental
/// come from the same song and have the same length, give or take a few samples of the vocoder's hop, so the
/// instrumental decides where the mix ends. A limiter catches what the sum of separately made stems adds on top.
/// <para>
/// Where the original is silent (an intro, a solo), the separated vocals still carry a little of what the separation
/// left behind, and Seed-VC turns that into an audible hiss. So the converted vocals pass a gate keyed by the
/// original vocals: they sound only where the original sang. The key is brought to a fixed mean level first, so the
/// threshold sits the same distance below the singing however loud YuE2 made it.
/// </para>
/// </remarks>
public sealed partial class FfmpegMixer(ILogger<FfmpegMixer> logger) : IAudioMixer
{
    /// <summary>A song of six minutes takes seconds; anything beyond this is hanging.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>More would mean one of the two is near silence, where the measurement says nothing.</summary>
    private const double MaxGainDb = 20;

    /// <summary>The mean level the gate's key is brought to, in dBFS.</summary>
    private const double KeyLevelDb = -20;

    /// <summary>
    /// The gate opens 25 dB below the singing's mean: quiet phrases pass, separation leftovers (typically 40 dB and
    /// more below) do not.
    /// </summary>
    private const double GateThresholdDb = KeyLevelDb - 25;

    public async Task MixAsync(MixInput input, string output, CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VoiceServiceException("ffmpeg is not installed.", System.Net.HttpStatusCode.NotImplemented);
        var original = await MeanVolumeAsync(ffmpeg, input.OriginalVocals, cancellationToken);
        var converted = await MeanVolumeAsync(ffmpeg, input.Vocals, cancellationToken);
        // Silence (an instrumental passage) measures as -inf: nothing to match then.
        var gain = original is { } o && converted is { } c ? Math.Clamp(o - c, -MaxGainDb, MaxGainDb) : 0;

        // Without a measurement (silent original) the key keeps its level; the gate then stays closed, as it should.
        var keyGain = original is { } k ? Math.Clamp(Math.Pow(10, (KeyLevelDb - k) / 20), 1 / 64.0, 64) : 1;

        List<string> arguments =
        [
            "-nostdin", "-loglevel", "error", "-y", "-i", input.Instrumental, "-i", input.Vocals, "-i", input.OriginalVocals,
        ];
        var sources = "[0:a][v]";
        if (input.Reverb is not null)
        {
            arguments.AddRange(["-i", input.Reverb]);
            sources += "[3:a]";
        }
        var inputs = input.Reverb is null ? 2 : 3;
        // Seed-VC writes mono; the stems are stereo at 48 kHz. Release long enough not to clip the ends of words.
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"[1:a]volume={gain:0.##}dB,aresample=48000,aformat=channel_layouts=stereo[c];"
            // The tiny offset keeps the key from digital silence, which ffmpeg's gate reads as "open".
            + $"[2:a]aresample=48000,aformat=channel_layouts=stereo,aeval='val(ch)+0.00001':c=same[k];"
            + $"[c][k]sidechaingate=level_sc={keyGain:0.####}:threshold={Math.Pow(10, GateThresholdDb / 20):0.#####}:range=0.001:ratio=20:attack=5:release=300[v];"
            + $"{sources}amix=inputs={inputs}:normalize=0:duration=first,alimiter=limit=0.95:level=disabled[m]");
        arguments.AddRange(["-filter_complex", filter, "-map", "[m]", "-ar", "48000", "-sample_fmt", "s16", "-c:a", "flac", output]);
        await RunAsync(ffmpeg, arguments, cancellationToken);
    }

    /// <remarks>
    /// Separation models write 32-bit float WAVs; FLAC has no float, and 24 bits keep far more than anyone hears while
    /// the file shrinks to a third or less. That matters: the phone streams them over Tailscale.
    /// </remarks>
    public async Task EncodeFlacAsync(string input, string output, CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VoiceServiceException("ffmpeg is not installed.", System.Net.HttpStatusCode.NotImplemented);
        await RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-y", "-i", input, "-map", "0:a", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-c:a", "flac", output], cancellationToken);
    }

    public async Task DecodeAsync(string input, string output, CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VoiceServiceException("ffmpeg is not installed.", System.Net.HttpStatusCode.NotImplemented);
        var codec = output.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "pcm_s16le";
        try
        {
            await RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-y", "-i", input, "-map", "0:a:0", "-vn", "-ar", "48000", "-sample_fmt", "s16", "-c:a", codec, output], cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new VoiceServiceException($"The file could not be read as audio: {exception.Message}", System.Net.HttpStatusCode.UnprocessableEntity);
        }
    }

    public async Task DecodeWaveAsync(string input, string output, CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VoiceServiceException("ffmpeg is not installed.", System.Net.HttpStatusCode.NotImplemented);
        await RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-y", "-i", input, "-map", "0:a:0", "-vn", "-ar", "48000", "-c:a", "pcm_s24le", output], cancellationToken);
    }

    /// <summary>ffmpeg's volumedetect in dBFS.</summary>
    private async Task<double?> MeanVolumeAsync(string ffmpeg, string file, CancellationToken cancellationToken)
    {
        var report = await RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-i", file, "-af", "volumedetect", "-f", "null", "-"], cancellationToken);
        var match = MeanVolume().Match(report);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <returns>What ffmpeg wrote to stderr, where its reports go.</returns>
    private async Task<string> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var message = $"ffmpeg exited with {process.ExitCode}: {(await error).Trim()}";
                logger.LogWarning("{Message}", message);
                throw new InvalidOperationException(message);
            }
            await output;
            return await error;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    [GeneratedRegex(@"mean_volume:\s*(-?[\d.]+) dB")]
    private static partial Regex MeanVolume();
}
