using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using YueUI.Api.Share;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

/// <summary>Against the real ffmpeg, where there is one: the filter graph is what could break.</summary>
public sealed class AudioMixerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-mix-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Converted_mono_vocals_end_up_in_a_stereo_flac_as_long_as_the_instrumental()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // Nothing to run against on a machine without ffmpeg; the Mac and CI's runner have it.
        }
        var instrumental = await Tone(ffmpeg, "instrumental.wav", 220, seconds: 3, rate: 48000, channels: 2, volume: 0.3);
        var original = await Tone(ffmpeg, "vocals_dry.wav", 440, seconds: 3, rate: 48000, channels: 2, volume: 0.3);
        // Seed-VC writes mono, quieter than it got, and a hop shorter.
        var converted = await Tone(ffmpeg, "converted.wav", 440, seconds: 2.98, rate: 44100, channels: 1, volume: 0.03);
        var reverb = await Tone(ffmpeg, "vocals_reverb.wav", 660, seconds: 3, rate: 48000, channels: 2, volume: 0.05);
        var output = Path.Combine(_directory, "mix.flac");

        await new FfmpegMixer(NullLogger<FfmpegMixer>.Instance).MixAsync(new MixInput(instrumental, converted, original, reverb), output, CancellationToken.None);

        var probe = await Run(ffmpeg.Replace("ffmpeg", "ffprobe"), "-v", "error", "-show_entries", "stream=codec_name,sample_rate,channels:format=duration", "-of", "default=nw=1", output);
        Assert.Contains("codec_name=flac", probe);
        Assert.Contains("sample_rate=48000", probe);
        Assert.Contains("channels=2", probe);
        Assert.Matches(@"duration=3\.0", probe);
    }

    [Fact]
    public async Task Converted_vocals_stay_silent_where_the_original_did_not_sing()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        var instrumental = await Tone(ffmpeg, "instrumental.wav", 220, seconds: 3, rate: 48000, channels: 2, volume: 0);
        // An intro of one second, then singing.
        var original = Path.Combine(_directory, "vocals_dry.wav");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
            "-af", "volume=0.3,volume=enable='lt(t,1)':volume=0", "-ac", "2", original);
        // Seed-VC's hiss everywhere, the song's voice from the second second. A fixed seed: its peaks decide the
        // second check, which a random one failed now and then.
        var converted = Path.Combine(_directory, "converted.wav");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anoisesrc=color=white:amplitude=0.05:sample_rate=44100:duration=3:seed=1",
            "-ac", "1", converted);
        var output = Path.Combine(_directory, "mix.flac");

        await new FfmpegMixer(NullLogger<FfmpegMixer>.Instance).MixAsync(new MixInput(instrumental, converted, original, null), output, CancellationToken.None);

        Assert.True(await MaxVolume(ffmpeg, output, from: 0, seconds: 0.8) < -50);
        Assert.True(await MaxVolume(ffmpeg, output, from: 1.5, seconds: 1) > -30);
    }

    [Fact]
    public async Task A_float_stem_becomes_a_24_bit_flac_and_its_waveform_is_read_from_the_wav()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        var stem = Path.Combine(_directory, "drums.wav");
        // As separation models write them: 32-bit float.
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=100:sample_rate=48000:duration=2",
            "-af", "volume=0.5", "-ac", "2", "-c:a", "pcm_f32le", stem);
        var output = Path.Combine(_directory, "drums.flac");

        await new FfmpegMixer(NullLogger<FfmpegMixer>.Instance).EncodeFlacAsync(stem, output, CancellationToken.None);

        var probe = await Run(ffmpeg.Replace("ffmpeg", "ffprobe"), "-v", "error", "-show_entries", "stream=codec_name,sample_rate,channels,bits_per_raw_sample", "-of", "default=nw=1", output);
        Assert.Contains("codec_name=flac", probe);
        Assert.Contains("sample_rate=48000", probe);
        Assert.Contains("bits_per_raw_sample=24", probe);
        var (seconds, peaks) = WavPeaks.Read(stem, 20)!.Value;
        Assert.Equal(2, seconds, 2);
        // The same peak ffmpeg measures, in dB.
        var measured = await MaxVolume(ffmpeg, stem, from: 0, seconds: 2);
        Assert.All(peaks, p => Assert.Equal(measured, 20 * Math.Log10(p), 0));
    }

    private static async Task<double> MaxVolume(string ffmpeg, string file, double from, double seconds)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-ss", from.ToString(CultureInfo.InvariantCulture), "-t", seconds.ToString(CultureInfo.InvariantCulture), "-i", file, "-af", "volumedetect", "-f", "null", "-" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var report = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var match = System.Text.RegularExpressions.Regex.Match(report, @"max_volume:\s*(-?[\d.]+|-inf) dB");
        Assert.True(match.Success, report);
        return match.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private async Task<string> Tone(string ffmpeg, string name, int frequency, double seconds, int rate, int channels, double volume)
    {
        var path = Path.Combine(_directory, name);
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi",
            "-i", FormattableString.Invariant($"sine=frequency={frequency}:sample_rate={rate}:duration={seconds}"),
            "-af", FormattableString.Invariant($"volume={volume}"), "-ac", channels.ToString(), path);
        return path;
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
